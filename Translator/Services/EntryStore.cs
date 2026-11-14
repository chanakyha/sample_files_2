using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Translator.Models;

namespace Translator.Services
{
    /// <summary>Thread-safe in-memory store of the texts shown on the screen.</summary>
    public class EntryStore
    {
        private readonly List<TextEntry> _entries = new List<TextEntry>();
        private readonly object _lock = new object();

        public EntryStore()
        {
            // Seed data in a mix of languages so detection is visible straight away
            Add("Hello");
            Add("Gracias");
            Add("Fromage");
            Add("Where is the nearest train station?");
            Add("Je voudrais un café, s'il vous plaît.");
            Add("¿Cómo estás hoy, amigo?");
            Add("Guten Morgen, wie geht es dir?");
            Add("The quick brown fox jumps over the lazy dog. It was a sunny afternoon, " +
                "and the whole village had gathered in the square to watch the annual festival.");
            Add("La ville de Paris est connue pour ses musées, ses cafés et son architecture. " +
                "Chaque année, des millions de touristes viennent la visiter.");
            Add("El mercado abre temprano por la mañana. Los vendedores ofrecen frutas frescas, " +
                "pan recién horneado y flores de todos los colores.");
        }

        public IReadOnlyList<TextEntry> GetAll()
        {
            lock (_lock) return _entries.ToList();
        }

        public TextEntry Add(string text)
        {
            text = text.Trim();
            var entry = new TextEntry { Text = text, Kind = Classify(text) };
            lock (_lock) _entries.Add(entry);
            return entry;
        }

        public bool Remove(Guid id)
        {
            lock (_lock) return _entries.RemoveAll(e => e.Id == id) > 0;
        }

        /// <summary>Word = no whitespace, Paragraph = line breaks or 2+ sentences, otherwise Sentence.</summary>
        public static EntryKind Classify(string text)
        {
            if (!Regex.IsMatch(text, @"\s")) return EntryKind.Word;
            if (text.Contains('\n')) return EntryKind.Paragraph;

            var sentenceCount = Regex.Matches(text, @"[^.!?]+[.!?]+").Count;
            return sentenceCount >= 2 ? EntryKind.Paragraph : EntryKind.Sentence;
        }
    }
}
