using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Translator.Services
{
    /// <summary>
    /// LibreTranslate (open source, self-hosted, no usage limits).
    /// API: https://libretranslate.com/docs
    ///   GET  /languages  -> [{ "code": "en", "name": "English", "targets": [...] }, ...]
    ///   POST /translate  { "q": ["a","b"], "source": "auto", "target": "fr", "format": "text", "api_key": "..." }
    ///        -> { "translatedText": ["..",".."], "detectedLanguage": [{ "confidence": 90.0, "language": "en" }, ...] }
    ///   POST /detect     { "q": "text" } -> [{ "confidence": 90.0, "language": "es" }, ...]
    /// Each text is detected with /detect (accurate per text, confidence 0..100), then translated in
    /// batches grouped by language, because /translate with source "auto" uses one language per batch.
    /// </summary>
    public class LibreTranslateService : TranslationServiceBase
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // App code -> codes LibreTranslate may use for it (depends on the server version)
        private static readonly Dictionary<string, string[]> ServerCodeOptions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new[] { "zh-Hans", "zh", "zh-CN" },
            ["he"] = new[] { "he", "iw" },
        };

        private const int DefaultParallel = 2;
        private const int RequestsPerWorker = 2;

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly int _configuredParallel;
        private int _parallel = DefaultParallel;
        private Dictionary<string, string> _toServer;   // app code -> server code
        private IReadOnlyCollection<string> _supported; // app codes

        /// <param name="parallelRequests">
        /// Requests sent at the same time. 0 = automatic: 2 per healthy worker reported by the
        /// load balancer (GET /lb/status), or 2 for a single LibreTranslate server.
        /// </param>
        public LibreTranslateService(string baseUrl, string apiKey = null, TranslationCache cache = null, int parallelRequests = 0)
            : base("libretranslate",
                   // Own server: no need to space requests out, but translation on CPU can be slow
                   new ThrottledHttp(baseInterval: TimeSpan.Zero, maxParallel: DefaultParallel, timeout: TimeSpan.FromMinutes(10), maxRetries: 3),
                   cache)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _configuredParallel = parallelRequests;
        }

        public override string Name => "LibreTranslate";

        /// <summary>Healthy workers behind the load balancer (1 for a plain LibreTranslate server, 0 = not known yet).</summary>
        public int Workers { get; private set; }

        /// <summary>Requests sent at the same time.</summary>
        public int Parallel => _parallel;

        // Smaller batches: a self-hosted server translates on CPU, so keep each request short
        // (also spreads the work evenly over the load balancer's workers)
        protected override int MaxBatchItems => 25;
        protected override int MaxBatchChars => 2000;
        protected override int MaxConcurrentBatches => _parallel;

        public override async Task<IReadOnlyCollection<string>> GetSupportedLanguagesAsync(CancellationToken ct = default)
        {
            if (_supported != null) return _supported;
            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out _))
                throw new HttpRequestException($"Invalid server address '{_baseUrl}'. Set it in Settings.");

            var json = await Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/languages"), ct, maxRetries: 1)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var serverCodes = new HashSet<string>(
                doc.RootElement.EnumerateArray().Select(l => l.GetProperty("code").GetString()),
                StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in Languages.Targets.Keys)
            {
                var options = ServerCodeOptions.TryGetValue(code, out var o) ? o : new[] { code };
                var match = options.FirstOrDefault(serverCodes.Contains);
                if (match != null) map[code] = match;
            }

            _toServer = map;
            await WarmUpAsync(ct).ConfigureAwait(false);
            await ConfigureParallelismAsync(ct).ConfigureAwait(false);
            _supported = map.Keys.ToList();
            return _supported;
        }

        /// <summary>
        /// One detection request before any parallel work. LibreTranslate initialises its language
        /// detector on first use and that is not thread-safe: on a freshly started server, parallel
        /// first requests mostly return "en" with 0% confidence (measured 1 of 16 correct cold,
        /// 16 of 16 after one warm-up). The load balancer warms its workers itself; this covers a
        /// plain LibreTranslate server.
        /// </summary>
        private async Task WarmUpAsync(CancellationToken ct)
        {
            try
            {
                await DetectAsync("Bonjour, ceci est un test de démarrage.", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException)
            {
                // Not fatal: real requests report their own errors
            }
        }

        /// <summary>
        /// Asks the load balancer how many workers are healthy (GET /lb/status) and sends that many
        /// requests in parallel (x2, so a worker never waits for the next request). A plain
        /// LibreTranslate server has no /lb/status and counts as one worker.
        /// </summary>
        private async Task ConfigureParallelismAsync(CancellationToken ct)
        {
            Workers = 1;
            try
            {
                var json = await Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/lb/status"), ct, maxRetries: 0)
                    .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                Workers = Math.Max(1, doc.RootElement.GetProperty("healthy").GetInt32());
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                // Not behind the load balancer
            }

            _parallel = _configuredParallel > 0 ? _configuredParallel : Math.Max(DefaultParallel, Workers * RequestsPerWorker);
            Http.SetMaxParallel(_parallel);
        }

        /// <summary>
        /// With source "auto", LibreTranslate detects ONE language for the whole batch (tested with 1.9.6),
        /// so mixed-language batches would be translated from the wrong language. Instead each text is
        /// detected with /detect, then texts are translated in groups with their language as explicit source.
        /// </summary>
        protected override async Task<List<BatchItem>> BatchAsync(List<string> texts, string source, string target, CancellationToken ct)
        {
            await GetSupportedLanguagesAsync(ct).ConfigureAwait(false);
            if (source != "auto")
                return await TranslateBatchAsync(texts, source, target, ct).ConfigureAwait(false);

            var detections = await Task.WhenAll(texts.Select(t => DetectAsync(t, ct))).ConfigureAwait(false);
            var results = new BatchItem[texts.Count];

            // One request per detected language, sent in parallel (spread over the workers)
            var groups = Enumerable.Range(0, texts.Count).GroupBy(i => detections[i].Language ?? "auto").ToList();
            var translations = await Task.WhenAll(groups.Select(group =>
                Languages.SameLanguage(group.Key, target)
                    ? Task.FromResult(group.Select(i => new BatchItem { Text = texts[i] }).ToList()) // already in target
                    : TranslateBatchAsync(group.Select(i => texts[i]).ToList(), group.Key, target, ct))).ConfigureAwait(false);

            for (var g = 0; g < groups.Count; g++)
            {
                var indexes = groups[g].ToList();
                var translated = translations[g];
                if (translated.Count != indexes.Count)
                    throw new JsonException($"expected {indexes.Count} translations, got {translated.Count}");

                for (var k = 0; k < indexes.Count; k++)
                {
                    var i = indexes[k];
                    results[i] = new BatchItem
                    {
                        Text = translated[k].Text,
                        Language = detections[i].Language ?? translated[k].Language,
                        Confidence = detections[i].Confidence ?? translated[k].Confidence
                    };
                }
            }
            return results.ToList();
        }

        /// <summary>POST /detect for one text: [{ "confidence": 90.0, "language": "es" }, ...] (best first).</summary>
        private async Task<BatchItem> DetectAsync(string text, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["q"] = text, ["api_key"] = _apiKey }, JsonOptions);
            var json = await Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/detect")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return new BatchItem();

            var best = root[0];
            var language = FromServer(best.GetProperty("language").GetString());
            if (_toServer != null && !_toServer.ContainsKey(language))
                language = null; // detected a language the server cannot translate from: let it auto-detect

            return new BatchItem
            {
                Language = language,
                Confidence = best.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetDouble() / 100.0 // LibreTranslate uses 0..100
                    : (double?)null
            };
        }

        private async Task<List<BatchItem>> TranslateBatchAsync(List<string> texts, string source, string target, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["q"] = texts,
                ["source"] = source == "auto" ? "auto" : ToServer(source),
                ["target"] = ToServer(target),
                ["format"] = "text",
                ["api_key"] = _apiKey
            }, JsonOptions);

            var json = await Http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/translate")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new HttpRequestException(error.GetString());

            var translated = ReadArray(root.GetProperty("translatedText"));
            var detected = root.TryGetProperty("detectedLanguage", out var d) ? ReadArray(d) : new List<JsonElement>();

            var results = new List<BatchItem>();
            for (var i = 0; i < translated.Count; i++)
            {
                var item = new BatchItem { Text = translated[i].GetString(), Language = source == "auto" ? null : source };
                if (i < detected.Count && detected[i].ValueKind == JsonValueKind.Object)
                {
                    if (detected[i].TryGetProperty("language", out var lang))
                        item.Language = FromServer(lang.GetString());
                    if (detected[i].TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
                        item.Confidence = conf.GetDouble() / 100.0; // LibreTranslate uses 0..100
                }
                results.Add(item);
            }
            return results;
        }

        /// <summary>Single values are returned as-is for one text; normalise to a list.</summary>
        private static List<JsonElement> ReadArray(JsonElement element) =>
            element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Select(e => e.Clone()).ToList()
                : new List<JsonElement> { element.Clone() };

        private string ToServer(string code) =>
            _toServer != null && _toServer.TryGetValue(code, out var server) ? server : code;

        private static string FromServer(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            foreach (var pair in ServerCodeOptions)
                if (pair.Value.Contains(code, StringComparer.OrdinalIgnoreCase))
                    return pair.Key;
            return code;
        }
    }
}
