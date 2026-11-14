namespace Translator.Models
{
    public class TranslationResult
    {
        public string TranslatedText { get; set; }
        public string DetectedLanguageCode { get; set; }
        public string DetectedLanguageName { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }

        /// <summary>Google's detection confidence (0..1).</summary>
        public double Confidence { get; set; }

        /// <summary>Google's guess was wrong and the app corrected it (e.g. "Gracias" guessed as English).</summary>
        public bool DetectionCorrected { get; set; }

        /// <summary>Confidence was low and the app could not confirm the language.</summary>
        public bool DetectionUncertain { get; set; }
    }
}
