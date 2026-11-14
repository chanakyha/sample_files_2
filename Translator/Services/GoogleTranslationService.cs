using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Translator.Models;

namespace Translator.Services
{
    /// <summary>
    /// Translates text with the public (key-less) Google Translate endpoint.
    ///
    /// Language detection:
    ///  1. Google auto-detects the language and returns a confidence score.
    ///  2. Common borrowed words ("Gracias", "Merci", "Ciao") are often labelled English
    ///     with low confidence. For short texts with confidence below <see cref="ConfidenceThreshold"/>
    ///     every candidate language is tried: the text is translated to English with that
    ///     source language and back again. The language whose round trip returns the
    ///     original text is the real one.
    ///  3. The text is then translated with that language as the explicit source.
    /// </summary>
    public class GoogleTranslationService : ITranslationService
    {
        private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
        private const double ConfidenceThreshold = 0.9;
        private const int MaxWordsToVerify = 3;
        private const string Pivot = "en";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Raw API calls cached per (source, target, text); detections cached per text
        private readonly ConcurrentDictionary<string, Task<ApiResult>> _calls = new ConcurrentDictionary<string, Task<ApiResult>>();
        private readonly ConcurrentDictionary<string, Task<Detection>> _detections = new ConcurrentDictionary<string, Task<Detection>>();

        public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new TranslationResult { Success = false, Error = "Empty text" };

            try
            {
                var detection = await DetectAsync(text).ConfigureAwait(false);

                string translated;
                if (Languages.SameLanguage(detection.Code, targetLanguage))
                    translated = text; // already in the target language
                else
                    translated = MatchCase(text, (await CallAsync(text, detection.Corrected ? detection.Code : "auto", targetLanguage)
                        .ConfigureAwait(false)).Text);

                return new TranslationResult
                {
                    Success = true,
                    TranslatedText = translated,
                    DetectedLanguageCode = detection.Code,
                    DetectedLanguageName = Languages.GetName(detection.Code),
                    Confidence = detection.Confidence,
                    DetectionCorrected = detection.Corrected,
                    DetectionUncertain = detection.Uncertain
                };
            }
            catch (Exception ex)
            {
                // Drop failed tasks from the caches so the next attempt retries
                ForgetFailed();
                return new TranslationResult
                {
                    Success = false,
                    TranslatedText = text,
                    DetectedLanguageName = "Unknown",
                    Error = "Translation failed: " + (ex.InnerException?.Message ?? ex.Message)
                };
            }
        }

        // ------------------------------------------------------------ Detection

        private Task<Detection> DetectAsync(string text) =>
            _detections.GetOrAdd(text, t => DetectCoreAsync(t));

        private async Task<Detection> DetectCoreAsync(string text)
        {
            var auto = await CallAsync(text, "auto", Pivot).ConfigureAwait(false);
            var detection = new Detection { Code = auto.DetectedLanguage, Confidence = auto.Confidence };

            if (auto.Confidence >= ConfidenceThreshold)
                return detection;

            var wordCount = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > MaxWordsToVerify)
            {
                detection.Uncertain = true;
                return detection;
            }

            var verified = await VerifyLanguageAsync(text, auto.DetectedLanguage).ConfigureAwait(false);
            if (verified == null)
            {
                detection.Uncertain = true;
            }
            else if (!Languages.SameLanguage(verified, detection.Code))
            {
                detection.Code = verified;
                detection.Corrected = true;
            }
            return detection;
        }

        /// <summary>
        /// Tries each candidate language: text --(candidate→en)--> English --(en→candidate)--> back.
        /// Returns the first candidate (Google's guess first, then menu order) whose round trip
        /// gives back the original text, or null when none does.
        /// </summary>
        private async Task<string> VerifyLanguageAsync(string text, string googleGuess)
        {
            var candidates = Languages.Targets.Keys
                .Where(k => !Languages.SameLanguage(k, Pivot))
                .OrderBy(k => Languages.SameLanguage(k, googleGuess) ? 0 : 1) // stable: keeps menu order otherwise
                .ToList();

            var checks = candidates.Select(async lang =>
            {
                var forward = await CallAsync(text, lang, Pivot).ConfigureAwait(false);
                if (Same(forward.Text, text)) return false; // the language did not recognise the text

                var back = await CallAsync(forward.Text, Pivot, lang).ConfigureAwait(false);
                return Same(back.Text, text);
            }).ToList();

            var results = await Task.WhenAll(checks).ConfigureAwait(false);
            for (var i = 0; i < candidates.Count; i++)
                if (results[i]) return candidates[i];

            return null;
        }

        /// <summary>
        /// Google sometimes returns ALL CAPS for single words when the source language is explicit
        /// ("Merci" -> "THANKS"). If the original was not all caps, use normal capitalisation.
        /// </summary>
        private static string MatchCase(string original, string translated)
        {
            if (string.IsNullOrEmpty(translated)) return translated;

            bool IsAllCaps(string s) => s.Any(char.IsLetter) && s.Where(char.IsLetter).All(char.IsUpper);
            if (!IsAllCaps(translated) || IsAllCaps(original)) return translated;

            var lower = translated.ToLower();
            var firstOriginal = original.FirstOrDefault(char.IsLetter);
            return char.IsUpper(firstOriginal) ? char.ToUpper(lower[0]) + lower.Substring(1) : lower;
        }

        private static bool Same(string a, string b) => Clean(a) == Clean(b);

        private static string Clean(string s) =>
            new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // ------------------------------------------------------------ API call

        private Task<ApiResult> CallAsync(string text, string source, string target) =>
            _calls.GetOrAdd(source + "|" + target + "|" + text, _ => CallCoreAsync(text, source, target));

        private static async Task<ApiResult> CallCoreAsync(string text, string source, string target)
        {
            var url = $"{Endpoint}?client=gtx&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}" +
                      $"&dt=t&dt=ld&q={Uri.EscapeDataString(text)}";

            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Response: [[["translated","original",...], ...], null, "detectedLang", null, null, null, confidence, ...]
            var sb = new StringBuilder();
            foreach (var segment in root[0].EnumerateArray())
            {
                if (segment[0].ValueKind == JsonValueKind.String)
                    sb.Append(segment[0].GetString());
            }

            var length = root.GetArrayLength();
            return new ApiResult
            {
                Text = sb.ToString(),
                DetectedLanguage = length > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() : source,
                Confidence = length > 6 && root[6].ValueKind == JsonValueKind.Number ? root[6].GetDouble() : 1.0
            };
        }

        private void ForgetFailed()
        {
            foreach (var pair in _calls.Where(p => p.Value.IsFaulted || p.Value.IsCanceled).ToList())
                _calls.TryRemove(pair.Key, out _);
            foreach (var pair in _detections.Where(p => p.Value.IsFaulted || p.Value.IsCanceled).ToList())
                _detections.TryRemove(pair.Key, out _);
        }

        private class ApiResult
        {
            public string Text { get; set; }
            public string DetectedLanguage { get; set; }
            public double Confidence { get; set; }
        }

        private class Detection
        {
            public string Code { get; set; }
            public double Confidence { get; set; }
            public bool Corrected { get; set; }
            public bool Uncertain { get; set; }
        }
    }
}
