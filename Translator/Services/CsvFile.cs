using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Translator.Services
{
    /// <summary>Minimal CSV / TXT import and CSV export.</summary>
    public static class CsvFile
    {
        /// <summary>
        /// Reads the texts to translate.
        /// .txt: one text per non-empty line.
        /// .csv: the column named "text" if there is a header with that name, otherwise the first column.
        /// The delimiter (comma, semicolon or tab) is detected from the first line.
        /// </summary>
        public static List<string> ReadTexts(string path)
        {
            var content = File.ReadAllText(path, Encoding.UTF8);

            if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return content.Split('\n')
                    .Select(l => l.Trim('\r').Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
            }

            var firstLine = content.Split('\n')[0];
            var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
            var rows = Parse(content, delimiter);
            if (rows.Count == 0) return new List<string>();

            var column = 0;
            var header = rows[0].FindIndex(h => string.Equals(h.Trim(), "text", StringComparison.OrdinalIgnoreCase));
            if (header >= 0)
            {
                column = header;
                rows.RemoveAt(0);
            }

            return rows
                .Where(r => r.Count > column)
                .Select(r => r[column].Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }

        /// <summary>Writes rows as UTF-8 CSV (with BOM so Excel shows accents correctly).</summary>
        public static void Write(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", header.Select(Escape)));
            foreach (var row in rows)
                writer.WriteLine(string.Join(",", row.Select(Escape)));
        }

        private static string Escape(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>RFC 4180 parser: supports quoted fields with delimiters, quotes and line breaks.</summary>
        private static List<List<string>> Parse(string content, char delimiter)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') inQuotes = false;
                    else field.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\n')
                {
                    row.Add(field.ToString().TrimEnd('\r'));
                    field.Clear();
                    if (row.Any(f => f.Length > 0)) rows.Add(row);
                    row = new List<string>();
                }
                else field.Append(c);
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString().TrimEnd('\r'));
                if (row.Any(f => f.Length > 0)) rows.Add(row);
            }
            return rows;
        }
    }
}
