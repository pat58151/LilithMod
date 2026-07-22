using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace LilithMod
{
    /// <summary>Extracts complete line objects from a streamed JSON reply.</summary>
    internal sealed class StreamingReplyParser
    {
        private readonly StringBuilder _content = new StringBuilder();
        private int _position;
        private bool _insideLines;
        private bool _finished;

        public string Content => _content.ToString();

        public List<JObject> Append(string fragment)
        {
            var completed = new List<JObject>();
            if (string.IsNullOrEmpty(fragment)) return completed;
            _content.Append(fragment);
            if (_finished) return completed;

            string text = _content.ToString();
            if (!_insideLines)
            {
                int key = text.IndexOf("\"lines\"", StringComparison.Ordinal);
                if (key < 0) return completed;
                int array = text.IndexOf('[', key + 7);
                if (array < 0) return completed;
                _insideLines = true;
                _position = array + 1;
            }

            while (_position < text.Length)
            {
                while (_position < text.Length &&
                       (char.IsWhiteSpace(text[_position]) || text[_position] == ','))
                    _position++;
                if (_position >= text.Length) break;
                if (text[_position] == ']')
                {
                    _finished = true;
                    break;
                }
                if (text[_position] != '{') break;

                int end = FindObjectEnd(text, _position);
                if (end < 0) break;
                try
                {
                    completed.Add(JObject.Parse(text.Substring(_position, end - _position + 1)));
                }
                catch
                {
                    _finished = true;
                    break;
                }
                _position = end + 1;
            }
            return completed;
        }

        private static int FindObjectEnd(string text, int start)
        {
            int depth = 0;
            bool quoted = false;
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }
    }
}
