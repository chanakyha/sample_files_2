using System;

namespace Translator.Models
{
    public enum EntryKind
    {
        Word,
        Sentence,
        Paragraph
    }

    public class TextEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; }
        public EntryKind Kind { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }
}
