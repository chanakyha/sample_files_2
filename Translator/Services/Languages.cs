using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Translator.Services
{
    public static class Languages
    {
        public const string Default = "en";

        // The 24 target languages offered in the "Translate" menu (ISO codes; mapped to the server's codes by LibreTranslateService)
        public static readonly IReadOnlyDictionary<string, string> Targets =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = "English",
                ["fr"] = "French",
                ["es"] = "Spanish",
                ["de"] = "German",
                ["it"] = "Italian",
                ["pt"] = "Portuguese",
                ["nl"] = "Dutch",
                ["ru"] = "Russian",
                ["zh-CN"] = "Chinese (Simplified)",
                ["ja"] = "Japanese",
                ["ko"] = "Korean",
                ["ar"] = "Arabic",
                ["hi"] = "Hindi",
                ["ta"] = "Tamil",
                ["bn"] = "Bengali",
                ["tr"] = "Turkish",
                ["pl"] = "Polish",
                ["sv"] = "Swedish",
                ["el"] = "Greek",
                ["he"] = "Hebrew",
                ["th"] = "Thai",
                ["vi"] = "Vietnamese",
                ["id"] = "Indonesian",
                ["uk"] = "Ukrainian"
            };

        // Legacy codes the translation service may return when detecting a language
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["iw"] = "he",
                ["zh"] = "zh-CN",
                ["zh-Hans"] = "zh-CN",
                ["jw"] = "jv"
            };

        /// <summary>Returns the canonical target code, or the default when unknown.</summary>
        public static string Normalize(string code) =>
            Targets.Keys.FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase)) ?? Default;

        public static bool SameLanguage(string a, string b)
        {
            if (a == null || b == null) return false;
            if (Aliases.TryGetValue(a, out var aa)) a = aa;
            if (Aliases.TryGetValue(b, out var bb)) b = bb;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "Unknown";
            if (Aliases.TryGetValue(code, out var alias)) code = alias;
            if (Targets.TryGetValue(code, out var name)) return name;
            try
            {
                return CultureInfo.GetCultureInfo(code).EnglishName;
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }
    }
}
