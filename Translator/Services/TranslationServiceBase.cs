using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Translator.Models;

namespace Translator.Services
{
    /// <summary>One translated item returned by a provider's batch call.</summary>
    public class BatchItem
    {
        public string Text { get; set; }

        /// <summary>Detected (or given) source language, in the app's codes (see <see cref="Languages"/>).</summary>
        public string Language { get; set; }

        /// <summary>Detection confidence 0..1, or null when unknown.</summary>
        public double? Confidence { get; set; }
    }

    /// <summary>
    /// Translation pipeline built for large data sets:
    /// - batching (<see cref="MaxBatchItems"/> texts / <see cref="MaxBatchChars"/> characters per request)
    /// - parallel batches (<see cref="MaxConcurrentBatches"/>), so a load balancer with several
    ///   workers is kept busy
    /// - disk cache (a text is never translated twice for the same target)
    /// - throttling + retry/backoff on rate limiting or busy servers (<see cref="ThrottledHttp"/>)
    /// - language-detection fix for borrowed words ("Ciao", "Gracias" labelled English):
    ///   short texts labelled English with low confidence are verified with a batched round trip
    ///   (text → English → back) for each candidate language; the language that returns the
    ///   original text is the real one.
    /// </summary>
    public abstract class TranslationServiceBase : ITranslationService
    {
        protected const string Pivot = "en";
        private const double ConfidenceThreshold = 0.9;
        private const int MaxWordsToVerify = 3;

        // Borrowed words mislabelled as English are written in Latin script, most likely first
        private static readonly string[] CandidateLanguages =
            { "es", "fr", "it", "de", "pt", "nl", "sv", "pl", "tr", "id", "vi" };

        protected readonly ThrottledHttp Http;
        private readonly TranslationCache _cache;

        protected TranslationServiceBase(string cacheName, ThrottledHttp http, TranslationCache cache)
        {
            Http = http;
            _cache = cache ?? new TranslationCache(TranslationCache.DefaultPath(cacheName));
        }

        /// <summary>Display name, e.g. "LibreTranslate".</summary>
        public abstract string Name { get; }

        protected virtual int MaxBatchItems => 100;
        protected virtual int MaxBatchChars => 4000;

        /// <summary>How many batches are sent at the same time.</summary>
        protected virtual int MaxConcurrentBatches => 2;

        /// <summary>Translates a batch. <paramref name="source"/> is "auto" or a language code.</summary>
        protected abstract Task<List<BatchItem>> BatchAsync(List<string> texts, string source, string target, CancellationToken ct);

        /// <summary>Target languages available (app codes), or null when all are available.</summary>
        public virtual Task<IReadOnlyCollection<string>> GetSupportedLanguagesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(null);

        public event Action<string> StatusChanged
        {
            add => Http.StatusChanged += value;
            remove => Http.StatusChanged -= value;
        }

        /// <summary>Number of HTTP requests sent so far (diagnostics).</summary>
        public int RequestCount => Http.RequestCount;

        public void ClearCache() => _cache.Clear();

        public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default) =>
            (await TranslateManyAsync(new[] { text }, targetLanguage, null, ct).ConfigureAwait(false))[0];

        public async Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(
            IReadOnlyList<string> texts, string targetLanguage,
            IProgress<TranslationProgress> progress = null, CancellationToken ct = default)
        {
            var run = new Run(targetLanguage, progress);
            var unique = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            string fatalError = null;

            try
            {
                IReadOnlyCollection<string> supported;
                try
                {
                    supported = await GetSupportedLanguagesAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new RetriesExhaustedException($"Cannot connect to {Name}: {ex.Message}", ex);
                }

                if (supported != null && !supported.Contains(targetLanguage, StringComparer.OrdinalIgnoreCase))
                {
                    fatalError = $"{Languages.GetName(targetLanguage)} is not available on this {Name} server.";
                }
                else
                {
                    run.Candidates = CandidateLanguages
                        .Where(c => supported == null || supported.Contains(c, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    await TranslateUniqueAsync(unique, run, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Keep what was translated so far
            }
            catch (RetriesExhaustedException ex)
            {
                fatalError = ex.Message;
            }
            finally
            {
                _cache.Save();
            }

            run.Report(unique.Count(t => _cache.TryGetTranslation(targetLanguage, t, out _)), unique.Count, "Done");
            return texts.Select(t => BuildResult(t, run, fatalError, ct.IsCancellationRequested)).ToList();
        }

        // ------------------------------------------------------------------ pipeline

        private async Task TranslateUniqueAsync(List<string> unique, Run run, CancellationToken ct)
        {
            var target = run.Target;
            var todo = unique.Where(t => !_cache.TryGetTranslation(target, t, out _)).ToList();
            run.Done = unique.Count - todo.Count;
            run.Total = unique.Count;
            run.Report("Translating...");
            if (todo.Count == 0) return;

            // Texts whose language was corrected in an earlier run need an explicit source language
            var corrected = todo.Where(t => _cache.TryGetDetection(t, out var d) && d.Corrected).ToList();
            var autoTexts = todo.Except(corrected).ToList();

            // 1. Detect + translate in parallel batches
            var suspects = new List<string>();
            var autoTranslations = new Dictionary<string, string>();

            await ForEachBatchAsync(autoTexts, "auto", target, run, ct, (text, item) =>
            {
                var known = _cache.TryGetDetection(text, out var detection);
                if (!known)
                {
                    detection = new DetectionInfo { Code = item.Language, Confidence = item.Confidence ?? 1.0 };
                    _cache.SetDetection(text, detection);
                }

                if (!known && Languages.SameLanguage(detection.Code, Pivot) && WordCount(text) <= MaxWordsToVerify
                    && detection.Confidence < ConfidenceThreshold)
                {
                    suspects.Add(text); // possibly a borrowed word: verify before saving
                    autoTranslations[text] = item.Text;
                }
                else
                {
                    _cache.SetTranslation(target, text, item.Text);
                    run.Done++;
                }
            }).ConfigureAwait(false);

            // 2. Find the real language of low-confidence short texts (batched round trip)
            var verified = await VerifyLanguagesAsync(suspects, run, ct).ConfigureAwait(false);

            foreach (var text in suspects)
            {
                _cache.TryGetDetection(text, out var d);
                if (verified.TryGetValue(text, out var lang))
                {
                    _cache.SetDetection(text, new DetectionInfo { Code = lang, Corrected = true, Confidence = d?.Confidence ?? 0 });
                    corrected.Add(text);
                    continue;
                }

                if (d != null) d.Uncertain = true;
                _cache.SetTranslation(target, text, autoTranslations[text]);
                run.Done++;
            }

            // 3. Translate texts with a corrected source language (one group per language)
            foreach (var group in corrected.GroupBy(t => _cache.TryGetDetection(t, out var d) ? d.Code : "auto"))
            {
                if (Languages.SameLanguage(group.Key, target))
                {
                    foreach (var text in group)
                    {
                        _cache.SetTranslation(target, text, text);
                        run.Done++;
                    }
                    continue;
                }

                await ForEachBatchAsync(group.ToList(), group.Key, target, run, ct, (text, item) =>
                {
                    _cache.SetTranslation(target, text, MatchCase(text, item.Text));
                    run.Done++;
                }).ConfigureAwait(false);
            }
            run.Report("Translating...");
        }

        /// <summary>
        /// For each candidate language (most likely first): translate all pending texts candidate→English
        /// in one batch, then English→candidate in one batch. Texts that come back unchanged are that
        /// language. Stops as soon as every text is resolved.
        /// </summary>
        private async Task<Dictionary<string, string>> VerifyLanguagesAsync(List<string> texts, Run run, CancellationToken ct)
        {
            var verified = new Dictionary<string, string>();
            var pending = texts.ToList();

            foreach (var candidate in run.Candidates)
            {
                if (pending.Count == 0) break;
                run.Report($"Verifying language of {pending.Count} word(s): trying {Languages.GetName(candidate)}...");

                var forward = new List<(string Text, string English)>();
                await ForEachBatchAsync(pending, candidate, Pivot, null, ct, (text, item) =>
                {
                    if (!Same(item.Text, text)) forward.Add((text, item.Text));
                }).ConfigureAwait(false);
                if (forward.Count == 0) continue;

                var backTexts = forward.Select(f => f.English).Distinct().ToList();
                var back = new Dictionary<string, string>();
                await ForEachBatchAsync(backTexts, Pivot, candidate, null, ct, (english, item) => back[english] = item.Text)
                    .ConfigureAwait(false);

                foreach (var (text, english) in forward)
                {
                    if (back.TryGetValue(english, out var roundTrip) && Same(roundTrip, text))
                    {
                        verified[text] = candidate;
                        pending.Remove(text);
                    }
                }
            }
            return verified;
        }

        /// <summary>
        /// Sends texts in batches, up to <see cref="MaxConcurrentBatches"/> at the same time, and calls
        /// <paramref name="onItem"/>(text, result) for each text (one batch at a time, so callbacks
        /// need no locking). A failing batch marks only its own texts as failed; if the service is
        /// unreachable or keeps rate limiting, the remaining batches are cancelled.
        /// </summary>
        private async Task ForEachBatchAsync(
            List<string> texts, string source, string target, Run run, CancellationToken ct,
            Action<string, BatchItem> onItem)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var limiter = new SemaphoreSlim(Math.Max(1, MaxConcurrentBatches));
            var sync = new object();

            var tasks = Chunk(texts).Select(async batch =>
            {
                await limiter.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    List<BatchItem> items = null;
                    string error = null;
                    try
                    {
                        items = await BatchAsync(batch, source, target, stop.Token).ConfigureAwait(false);
                        if (items.Count != batch.Count)
                            error = $"Unexpected response: expected {batch.Count} results, got {items.Count}";
                    }
                    catch (RetriesExhaustedException)
                    {
                        stop.Cancel(); // the service is down: don't send the remaining batches
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        error = "Translation failed: " + ex.Message;
                    }
                    catch (JsonException ex)
                    {
                        error = "Unexpected response: " + ex.Message;
                    }

                    lock (sync)
                    {
                        if (error == null)
                            for (var i = 0; i < batch.Count; i++) onItem(batch[i], items[i]);
                        else if (run != null)
                            foreach (var text in batch) run.Errors[text] = error;
                        run?.Report("Translating...");
                    }
                }
                finally
                {
                    limiter.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Report the real cause rather than the cancellations it triggered in other batches
                var fatal = tasks.Where(t => t.IsFaulted)
                    .SelectMany(t => t.Exception.InnerExceptions)
                    .OfType<RetriesExhaustedException>()
                    .FirstOrDefault();
                if (fatal != null) throw fatal;
                throw;
            }
        }

        /// <summary>Splits texts into batches limited by item count and total characters.</summary>
        private IEnumerable<List<string>> Chunk(List<string> texts)
        {
            var batch = new List<string>();
            var chars = 0;
            foreach (var text in texts)
            {
                if (batch.Count > 0 && (batch.Count >= MaxBatchItems || chars + text.Length > MaxBatchChars))
                {
                    yield return batch;
                    batch = new List<string>();
                    chars = 0;
                }
                batch.Add(text);
                chars += text.Length;
            }
            if (batch.Count > 0) yield return batch;
        }

        // ------------------------------------------------------------------ results

        private TranslationResult BuildResult(string text, Run run, string fatalError, bool cancelled)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new TranslationResult { Success = false, TranslatedText = text, DetectedLanguageName = "Unknown", Error = "Empty text" };

            _cache.TryGetDetection(text, out var detection);
            var result = new TranslationResult
            {
                DetectedLanguageCode = detection?.Code,
                DetectedLanguageName = Languages.GetName(detection?.Code),
                Confidence = detection?.Confidence ?? 0,
                DetectionCorrected = detection?.Corrected ?? false,
                DetectionUncertain = detection?.Uncertain ?? false
            };

            if (_cache.TryGetTranslation(run.Target, text, out var translation))
            {
                result.Success = true;
                result.TranslatedText = translation;
            }
            else
            {
                result.Success = false;
                result.TranslatedText = text;
                result.Error = run.Errors.TryGetValue(text, out var error) ? error
                    : fatalError ?? (cancelled ? "Cancelled" : "Not translated");
            }
            return result;
        }

        /// <summary>
        /// Some engines return ALL CAPS for single words when the source language is explicit
        /// ("Merci" -> "THANKS"). If the original was not all caps, use normal capitalisation.
        /// </summary>
        private static string MatchCase(string original, string translated)
        {
            if (string.IsNullOrEmpty(translated)) return translated;

            static bool IsAllCaps(string s) => s.Any(char.IsLetter) && s.Where(char.IsLetter).All(char.IsUpper);
            if (!IsAllCaps(translated) || IsAllCaps(original)) return translated;

            var lower = translated.ToLower();
            var firstOriginal = original.FirstOrDefault(char.IsLetter);
            return char.IsUpper(firstOriginal) ? char.ToUpper(lower[0]) + lower.Substring(1) : lower;
        }

        private static int WordCount(string text) =>
            text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;

        private static bool Same(string a, string b) => Clean(a) == Clean(b);

        private static string Clean(string s) =>
            new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>State of one TranslateManyAsync call.</summary>
        private class Run
        {
            private readonly IProgress<TranslationProgress> _progress;

            public Run(string target, IProgress<TranslationProgress> progress)
            {
                Target = target;
                _progress = progress;
            }

            public string Target { get; }
            public int Done { get; set; }
            public int Total { get; set; }
            public List<string> Candidates { get; set; } = new List<string>();
            public Dictionary<string, string> Errors { get; } = new Dictionary<string, string>();

            public void Report(string message) => Report(Done, Total, message);

            public void Report(int done, int total, string message) =>
                _progress?.Report(new TranslationProgress { Done = done, Total = total, Message = message });
        }
    }
}
