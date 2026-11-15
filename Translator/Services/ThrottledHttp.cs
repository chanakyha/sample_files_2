using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Translator.Services
{
    /// <summary>Thrown when the service keeps rate limiting (or the network keeps failing) after all retries.</summary>
    public class RetriesExhaustedException : HttpRequestException
    {
        public RetriesExhaustedException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// HTTP sender that avoids and recovers from rate limiting:
    /// - limits parallel requests and spaces them out (minimum interval between requests)
    /// - on HTTP 429/5xx or network errors: waits (Retry-After header or exponential backoff),
    ///   pauses ALL requests during that time, and slows down the request rate
    /// - after successful requests the rate slowly speeds up again
    /// </summary>
    public sealed class ThrottledHttp
    {
        private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(120);

        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate;
        private readonly TimeSpan _baseInterval;
        private readonly int _defaultMaxRetries;
        private readonly object _lock = new object();
        private readonly Random _random = new Random();

        private DateTime _nextSlot = DateTime.MinValue;
        private DateTime _pausedUntil = DateTime.MinValue;
        private TimeSpan _interval;

        /// <param name="baseInterval">Minimum time between requests (default 300 ms).</param>
        /// <param name="maxParallel">Maximum parallel requests (default 2).</param>
        /// <param name="timeout">Timeout per request (default 30 s).</param>
        /// <param name="maxRetries">Retries on rate limiting / network errors (default 6).</param>
        public ThrottledHttp(TimeSpan? baseInterval = null, int maxParallel = 2, TimeSpan? timeout = null, int maxRetries = 6)
        {
            _baseInterval = baseInterval ?? TimeSpan.FromMilliseconds(300);
            _interval = _baseInterval;
            _gate = new SemaphoreSlim(maxParallel);
            _maxParallel = maxParallel;
            _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
            _defaultMaxRetries = maxRetries;
        }

        private int _maxParallel;

        /// <summary>
        /// Raises the number of parallel requests (e.g. when a load balancer reports more workers).
        /// Lowering is not supported; the limit only grows.
        /// </summary>
        public void SetMaxParallel(int maxParallel)
        {
            lock (_lock)
            {
                if (maxParallel <= _maxParallel) return;
                _gate.Release(maxParallel - _maxParallel);
                _maxParallel = maxParallel;
            }
        }

        /// <summary>Raised (on a background thread) with messages such as "Rate limited, waiting 16s".</summary>
        public event Action<string> StatusChanged;

        /// <summary>Number of HTTP requests sent (for diagnostics).</summary>
        public int RequestCount;

        /// <param name="maxRetries">Retries on rate limiting / network errors; null = default, 0 = fail immediately.</param>
        public async Task<string> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct, int? maxRetries = null)
        {
            var retries = maxRetries ?? _defaultMaxRetries;
            for (var attempt = 1; ; attempt++)
            {
                HttpStatusCode? status = null;
                TimeSpan? retryAfter = null;
                Exception networkError = null;

                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await WaitForSlotAsync(ct).ConfigureAwait(false);

                    using var request = createRequest();
                    Interlocked.Increment(ref RequestCount);
                    using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        OnSuccess();
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    status = response.StatusCode;
                    retryAfter = response.Headers.RetryAfter?.Delta;
                    if (!IsRetryable(status.Value))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new HttpRequestException($"HTTP {(int)status.Value}: {ErrorMessage(body) ?? response.ReasonPhrase}");
                    }
                }
                catch (HttpRequestException ex) when (status == null)
                {
                    networkError = ex;
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    networkError = new TimeoutException("The request timed out", ex);
                }
                finally
                {
                    _gate.Release();
                }

                if (attempt > retries)
                {
                    throw new RetriesExhaustedException(status != null
                        ? $"The service is rate limiting requests (HTTP {(int)status.Value}). Gave up after {retries} retries - try again later."
                        : $"Network error: {networkError?.Message}", networkError);
                }

                var delay = retryAfter ?? Backoff(attempt);
                OnThrottled(delay);
                StatusChanged?.Invoke(status != null
                    ? $"Rate limit reached (HTTP {(int)status.Value}). Waiting {delay.TotalSeconds:0}s, then retrying ({attempt}/{retries})..."
                    : $"Network problem ({networkError?.Message}). Retrying in {delay.TotalSeconds:0}s ({attempt}/{retries})...");
            }
        }

        /// <summary>Extracts { "error": "..." } from a JSON error body, if present.</summary>
        private static string ErrorMessage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var error))
                    return error.ToString();
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private static bool IsRetryable(HttpStatusCode code) =>
            code == (HttpStatusCode)429 ||
            code == HttpStatusCode.InternalServerError ||
            code == HttpStatusCode.BadGateway ||
            code == HttpStatusCode.ServiceUnavailable ||
            code == HttpStatusCode.GatewayTimeout;

        /// <summary>4s, 8s, 16s, 32s, 64s, 120s (+ up to 1s random jitter).</summary>
        private TimeSpan Backoff(int attempt)
        {
            var seconds = Math.Min(Math.Pow(2, attempt + 1), MaxBackoff.TotalSeconds);
            lock (_lock) return TimeSpan.FromSeconds(seconds + _random.NextDouble());
        }

        private async Task WaitForSlotAsync(CancellationToken ct)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var start = now;
                if (_nextSlot > start) start = _nextSlot;
                if (_pausedUntil > start) start = _pausedUntil;
                _nextSlot = start + _interval;
                wait = start - now;
            }
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        private void OnThrottled(TimeSpan delay)
        {
            lock (_lock)
            {
                var until = DateTime.UtcNow + delay;
                if (until > _pausedUntil) _pausedUntil = until;
                var doubled = Math.Max(_interval.Ticks * 2, TimeSpan.FromMilliseconds(300).Ticks);
                _interval = TimeSpan.FromTicks(Math.Min(doubled, MaxInterval.Ticks));
            }
        }

        private void OnSuccess()
        {
            lock (_lock)
                _interval = TimeSpan.FromTicks(Math.Max((long)(_interval.Ticks * 0.9), _baseInterval.Ticks));
        }
    }
}
