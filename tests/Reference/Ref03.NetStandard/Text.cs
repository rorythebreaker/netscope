using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ref.NetStandard
{
    /// <summary>Small text utilities of the kind shared between .NET Framework and .NET Core projects.</summary>
    public static class TextUtil
    {
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Slug(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        public static string CollapseSpaces(string text) => Spaces.Replace(text ?? string.Empty, " ").Trim();

        public static IEnumerable<string> Wrap(string text, int width)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var line = new StringBuilder();
            foreach (var word in CollapseSpaces(text).Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
        }

        public static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        public static string Closest(string word, IEnumerable<string> candidates) =>
            candidates.OrderBy(c => Levenshtein(word, c)).ThenBy(c => c, StringComparer.Ordinal).FirstOrDefault();
    }

    public sealed class CsvRow
    {
        private readonly List<string> _fields;

        private CsvRow(List<string> fields) => _fields = fields;

        public int Count => _fields.Count;
        public string this[int index] => _fields[index];

        public static CsvRow Parse(string line, char separator = ',')
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == separator) { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(c);
            }
            if (quoted) throw new FormatException("unterminated quote");
            fields.Add(field.ToString());
            return new CsvRow(fields);
        }

        public override string ToString() => string.Join(",", _fields.Select(f => f.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));
    }
}
