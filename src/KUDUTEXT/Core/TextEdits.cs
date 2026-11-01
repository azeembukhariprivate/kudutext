using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudutext.Core
{
    /// <summary>The small pieces of checking and correcting the text: times, splitting and joining lines, replacing words.</summary>
    public static class TextEdits
    {
        private static readonly Regex Seconds = new Regex(@"^\d+(?:[.,]\d{1,3})?$", RegexOptions.CultureInvariant);
        private static readonly Regex Whole = new Regex(@"^\d+$", RegexOptions.CultureInvariant);

        /// <summary>"00:01:02.500", "1:02,5", "62.5" or "62" as milliseconds; null when it isn't a time.</summary>
        public static long? ParseTime(string text)
        {
            var parts = (text ?? string.Empty).Trim().Split(':');
            if (parts.Length > 3 || !Seconds.IsMatch(parts[parts.Length - 1])) return null;
            for (var k = 0; k < parts.Length - 1; k++)
                if (!Whole.IsMatch(parts[k]) || parts[k].Length > 4) return null;
            if (parts[parts.Length - 1].Length > 10) return null;
            var seconds = double.Parse(parts[parts.Length - 1].Replace(',', '.'), CultureInfo.InvariantCulture);
            long minutes = parts.Length >= 2 ? long.Parse(parts[parts.Length - 2], CultureInfo.InvariantCulture) : 0;
            long hours = parts.Length == 3 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
            // With minutes given, seconds must be under 60 ("1:75" isn't a time); with hours, minutes too.
            if (parts.Length >= 2 && seconds >= 60) return null;
            if (parts.Length == 3 && minutes >= 60) return null;
            return (long)Math.Round(((hours * 60 + minutes) * 60 + seconds) * 1000);
        }

        /// <summary>"00:01:02.500", the way times are shown for checking.</summary>
        public static string ShowTime(long ms) => SubtitleFiles.Time(TimeSpan.FromMilliseconds(ms), '.');

        /// <summary>Splits a line's words at the cursor; false when either part would be empty.</summary>
        public static bool SplitText(string text, int caret, out string first, out string second)
        {
            text = text ?? string.Empty;
            caret = Math.Max(0, Math.Min(text.Length, caret));
            first = text.Substring(0, caret).Trim();
            second = text.Substring(caret).Trim();
            return first.Length > 0 && second.Length > 0;
        }

        /// <summary>Where a split line's first part ends: shared out by the number of letters in each part.</summary>
        public static long SplitTime(long start, long end, int firstLength, int secondLength)
        {
            if (end <= start || firstLength + secondLength == 0) return start;
            return start + (long)Math.Round((end - start) * (double)firstLength / (firstLength + secondLength));
        }

        public static string JoinText(string first, string second) =>
            string.Join(" ", new[] { first, second }.Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0));

        /// <summary>Replaces every <paramref name="find"/> in <paramref name="text"/>; <paramref name="count"/> says how many.</summary>
        public static string Replace(string text, string find, string replacement, bool matchCase, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find)) return text ?? string.Empty;
            var how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new StringBuilder();
            var at = 0;
            int i;
            while ((i = text.IndexOf(find, at, how)) >= 0)
            {
                sb.Append(text, at, i - at).Append(replacement ?? string.Empty);
                at = i + find.Length;
                count++;
            }
            return count == 0 ? text : sb.Append(text, at, text.Length - at).ToString();
        }

        public static bool Contains(string text, string find, bool matchCase) =>
            !string.IsNullOrEmpty(find) && (text ?? string.Empty).IndexOf(find, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// What's wrong with a line's times, in plain words, or null: a time that isn't a time, an end before the start,
        /// or a start before the line above's.
        /// </summary>
        public static string Problem(string start, string end, long? previousStart)
        {
            var s = ParseTime(start);
            var e = ParseTime(end);
            if (s == null) return "The start isn't a time (like 00:01:02.500).";
            if (e == null) return "The end isn't a time (like 00:01:02.500).";
            if (e < s) return "It ends before it starts.";
            if (previousStart != null && s < previousStart) return "It starts before the line above.";
            return null;
        }

        /// <summary>The lines as they'll be saved: lines with no words are left out.</summary>
        public static Transcript Build(string language, IEnumerable<(long Start, long End, string Text)> lines) => new Transcript
        {
            Language = language,
            Segments = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .Select(l => new Segment { StartMs = l.Start, EndMs = l.End, Text = l.Text.Trim() }).ToList()
        };
    }
}
