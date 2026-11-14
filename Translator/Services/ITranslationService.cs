using System.Threading.Tasks;
using Translator.Models;

namespace Translator.Services
{
    public interface ITranslationService
    {
        /// <summary>Detects the source language of <paramref name="text"/> and translates it to <paramref name="targetLanguage"/>.</summary>
        Task<TranslationResult> TranslateAsync(string text, string targetLanguage);
    }
}
