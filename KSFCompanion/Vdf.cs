using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KsfCompanion
{
    /// <summary>
    /// Reads Steam's text config files (Valve's KeyValues: libraryfolders.vdf, loginusers.vdf, registry.vdf,
    /// localconfig.vdf). Keys are looked up without regard to case, as Steam itself writes "apps" or "Apps".
    /// </summary>
    static class Vdf
    {
        public sealed class Node : Dictionary<string, object>
        {
            public Node() : base(StringComparer.OrdinalIgnoreCase) { }

            /// <summary>The node or text under a path of keys, or null.</summary>
            public object At(params string[] path)
            {
                object current = this;
                foreach (var key in path)
                {
                    if (!(current is Node node) || !node.TryGetValue(key, out current)) return null;
                }
                return current;
            }

            public Node NodeAt(params string[] path) => At(path) as Node;
            public string TextAt(params string[] path) => At(path) as string;
        }

        /// <summary>A file's contents, or null if it isn't there or can't be read.</summary>
        public static Node Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static Node Parse(string text)
        {
            var position = 0;
            return ReadNode(text, ref position);
        }

        static Node ReadNode(string text, ref int position)
        {
            var node = new Node();
            while (true)
            {
                var key = NextToken(text, ref position, out var keyKind);
                // A closing brace ends this node; the end of the text ends everything (even a cut-off file).
                if (keyKind == Token.End || keyKind == Token.Close) return node;
                if (keyKind == Token.Open) continue;

                var value = NextToken(text, ref position, out var valueKind);
                if (valueKind == Token.Open) node[key] = ReadNode(text, ref position);
                else if (valueKind == Token.Text) node[key] = value;
                else return node;
            }
        }

        enum Token { Text, Open, Close, End }

        static string NextToken(string text, ref int i, out Token kind)
        {
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '[')
                {
                    // [$WIN32] style conditions: ignored.
                    while (i < text.Length && text[i] != ']') i++;
                    i++;
                    continue;
                }
                if (c == '{') { i++; kind = Token.Open; return null; }
                if (c == '}') { i++; kind = Token.Close; return null; }
                kind = Token.Text;
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            var next = text[i + 1];
                            sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                            i += 2;
                            continue;
                        }
                        sb.Append(text[i++]);
                    }
                    i++;
                    return sb.ToString();
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"') i++;
                return text.Substring(start, i - start);
            }
            kind = Token.End;
            return null;
        }
    }
}
