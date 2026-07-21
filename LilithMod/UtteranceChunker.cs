using System;
using System.Collections.Generic;

namespace LilithMod
{
    /// <summary>Splits replies for lower-latency synthesis and aligned subtitles.</summary>
    internal static class UtteranceChunker
    {
        // Weighted lengths keep Latin and CJK splitting comparable.
        private const int CjkWeight = 2;
        // Below this the line already synthesises fast enough that splitting only
        // adds request overhead and chops her delivery into fragments.
        private const int MinWeightToSplit = 60;
        // A piece shorter than this is a fragment, not a sentence ("Mm...");
        // merged into its neighbour rather than spoken alone.
        private const int MinChunkWeight = 14;
        // An ellipsis only ends a piece once this much has accumulated, so her
        // heavily punctuated style does not shatter into one-word utterances.
        private const int SoftTargetWeight = 50;
        private const int MaxChunks = 4;

        /// <summary>Splits one utterance into synthesis-ready pieces.</summary>
        public static List<Utterance> Chunk(Utterance source)
        {
            var single = new List<Utterance> { source };
            if (source == null || source.NativeDialogue != null) return single;

            string spokenText = source.JaText ?? string.Empty;
            if (Weight(spokenText) < MinWeightToSplit) return single;

            List<string> spoken = Split(spokenText);
            if (spoken.Count < 2) return single;

            List<string> shown = Split(source.EnText ?? string.Empty);

            // Split subtitles only when both languages have matching sentence counts.
            bool paired = shown.Count == spoken.Count;

            int count = Math.Min(spoken.Count, MaxChunks);
            if (count < 2) return single;

            List<string> spokenParts = Regroup(spoken, count);
            // Unequal sides keep one subtitle while audio remains chunked.
            List<string> shownParts = paired ? Regroup(shown, count) : null;

            var result = new List<Utterance>(count);
            for (int i = 0; i < count; i++)
            {
                result.Add(new Utterance
                {
                    JaText = spokenParts[i],
                    EnText = shownParts != null ? shownParts[i] : (i == 0 ? source.EnText : string.Empty),
                    Language = source.Language,
                    SuppressSubtitle = source.SuppressSubtitle ||
                                       (shownParts == null && i > 0),
                });
            }
            return result;
        }

        /// <summary>Splits at sentence endings while preserving short ellipses.</summary>
        private static List<string> Split(string text)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return parts;

            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsBoundaryChar(text[i])) continue;

                int end = i;
                while (end + 1 < text.Length && IsBoundaryChar(text[end + 1])) end++;
                while (end + 1 < text.Length && IsTrailer(text[end + 1])) end++;

                bool ellipsis = IsEllipsis(text, i, end);
                int length = end - start + 1;
                int weight = Weight(text.Substring(start, length));
                // "3.5" and "12.30" are not two sentences.
                bool decimalPoint = text[i] == '.' && i == end && i > start &&
                                    char.IsDigit(text[i - 1]) &&
                                    i + 1 < text.Length && char.IsDigit(text[i + 1]);

                if (decimalPoint || (ellipsis && weight < SoftTargetWeight))
                {
                    i = end;
                    continue;
                }

                string piece = text.Substring(start, length).Trim();
                if (piece.Length > 0) parts.Add(piece);
                start = end + 1;
                i = end;
            }

            if (start < text.Length)
            {
                string tail = text.Substring(start).Trim();
                if (tail.Length > 0) parts.Add(tail);
            }

            return Merge(parts);
        }

        /// <summary>Folds fragments into a neighbour so nothing is spoken alone.</summary>
        private static List<string> Merge(List<string> parts)
        {
            var merged = new List<string>();
            foreach (string part in parts)
            {
                if (merged.Count > 0 && Weight(merged[merged.Count - 1]) < MinChunkWeight)
                    merged[merged.Count - 1] = Join(merged[merged.Count - 1], part);
                else
                    merged.Add(part);
            }
            // A short LAST piece has no following neighbour to absorb it.
            if (merged.Count >= 2 && Weight(merged[merged.Count - 1]) < MinChunkWeight)
            {
                merged[merged.Count - 2] = Join(merged[merged.Count - 2], merged[merged.Count - 1]);
                merged.RemoveAt(merged.Count - 1);
            }
            return merged;
        }

        /// <summary>Groups contiguous sentences into near-even pieces.</summary>
        private static List<string> Regroup(List<string> parts, int count)
        {
            var result = new List<string>(count);
            int taken = 0;
            for (int i = 0; i < count; i++)
            {
                int remaining = parts.Count - taken;
                int size = Math.Max(1, (int)Math.Round(remaining / (double)(count - i)));
                if (size > remaining - (count - i - 1)) size = remaining - (count - i - 1);
                string group = parts[taken];
                for (int j = 1; j < size; j++)
                    group = Join(group, parts[taken + j]);
                result.Add(group);
                taken += size;
            }
            return result;
        }

        // Japanese runs its sentences together; English does not.
        private static string Join(string left, string right)
        {
            if (string.IsNullOrEmpty(left)) return right;
            if (string.IsNullOrEmpty(right)) return left;
            return IsCjk(left[left.Length - 1]) || IsCjk(right[0])
                ? left + right
                : left + " " + right;
        }

        /// <summary>Rough speaking length, so the same threshold suits both scripts.</summary>
        private static int Weight(string text)
        {
            int total = 0;
            foreach (char c in text)
                total += IsCjk(c) ? CjkWeight : 1;
            return total;
        }

        private static bool IsCjk(char c)
        {
            return (c >= '　' && c <= '〿') ||   // CJK punctuation
                   (c >= '぀' && c <= 'ヿ') ||   // kana
                   (c >= '㐀' && c <= '鿿') ||   // han
                   (c >= '＀' && c <= '･');     // fullwidth forms
        }

        private static bool IsBoundaryChar(char c)
        {
            return c == '.' || c == '!' || c == '?' || c == '…' ||
                   c == '。' || c == '！' || c == '？';
        }

        // Punctuation that belongs to the sentence just ended, not the next one.
        private static bool IsTrailer(char c)
        {
            return c == '"' || c == '\'' || c == ')' || c == ']' ||
                   c == '」' || c == '』' || c == '）' || c == '”';
        }

        private static bool IsEllipsis(string text, int start, int end)
        {
            if (text[start] == '…') return true;
            return text[start] == '.' && end > start && text[end] == '.';
        }
    }
}
