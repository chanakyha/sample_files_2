using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Translator.Models;

namespace Translator.Services
{
    public class TranslationProgress
    {
        public int Done { get; set; }
        public int Total { get; set; }
        public string Message { get; set; }
    }

    public interface ITranslationService
    {
        /// <summary>Display name, e.g. "Argos Translate".</summary>
        string Name { get; }

        /// <summary>Target languages available (app codes), or null when all are available.</summary>
        Task<IReadOnlyCollection<string>> GetSupportedLanguagesAsync(CancellationToken ct = default);

        /// <summary>Status messages such as rate-limit waits (raised on a background thread).</summary>
        event Action<string> StatusChanged;

        /// <summary>Detects the language of one text and translates it.</summary>
        Task<TranslationResult> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default);

        /// <summary>
        /// Detects and translates many texts using batched requests. Results are returned in the same order.
        /// When cancelled, texts already translated are returned and the rest are marked "Cancelled".
        /// </summary>
        Task<IReadOnlyList<TranslationResult>> TranslateManyAsync(
            IReadOnlyList<string> texts, string targetLanguage,
            IProgress<TranslationProgress> progress = null, CancellationToken ct = default);

        /// <summary>Removes all cached translations.</summary>
        void ClearCache();
    }
}
