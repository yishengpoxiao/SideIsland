using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexIsland
{
    public sealed class TextStatistics
    {
        public int Characters { get; set; }
        public int Words { get; set; }
        public int Lines { get; set; }
    }

    public static class TextTools
    {
        public const int MaximumInputLength = 65536;
        public const int MaximumOutputLength = 262144;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static TextStatistics Statistics(string text)
        {
            CheckInput(text);
            return new TextStatistics { Characters = new StringInfo(text).LengthInTextElements, Words = Regex.Matches(text, @"[\p{L}\p{N}_]+").Count, Lines = text.Length == 0 ? 0 : SplitLines(text).Length };
        }
        public static string Transform(string operation, string text)
        {
            CheckInput(text); string output;
            try
            {
                switch (operation)
                {
                    case "upper": output = text.ToUpperInvariant(); break;
                    case "lower": output = text.ToLowerInvariant(); break;
                    case "deduplicate": output = String.Join("\n", SplitLines(text).Distinct(StringComparer.Ordinal)); break;
                    case "nonempty": output = String.Join("\n", SplitLines(text).Where(line => !String.IsNullOrWhiteSpace(line))); break;
                    case "trim": output = String.Join("\n", SplitLines(text).Select(line => line.Trim())); break;
                    case "json-pretty": output = new JsonLayout(text, true).Format(); break;
                    case "json-compact": output = new JsonLayout(text, false).Format(); break;
                    case "url-encode": output = EncodeUrl(text); break;
                    case "url-decode": output = DecodeUrl(text); break;
                    case "base64-encode": output = Convert.ToBase64String(StrictUtf8.GetBytes(text)); break;
                    case "base64-decode": output = StrictUtf8.GetString(Convert.FromBase64String(text)); break;
                    case "sha256": using (var hash = SHA256.Create()) { output = BitConverter.ToString(hash.ComputeHash(StrictUtf8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); } break;
                    default: throw new ArgumentException("请选择可用的文字操作。");
                }
            }
            catch (DecoderFallbackException) { throw new ArgumentException("解码结果不是有效的 UTF-8 文字，原文已保留。"); }
            catch (EncoderFallbackException) { throw new ArgumentException("文字包含不完整的 Unicode 字符，原文已保留。"); }
            catch (FormatException) { throw new ArgumentException("编码内容格式无效，请检查后重试。原文已保留。"); }
            if (output.Length > MaximumOutputLength) throw new ArgumentException("结果过长，请分段处理。原文已保留。");
            return output;
        }
        private static void CheckInput(string text)
        {
            if (text == null || text.Length > MaximumInputLength) throw new ArgumentException("每次最多处理 65,536 个文本单元，请分段处理。");
        }
        private static string[] SplitLines(string text) { return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }
        private static string EncodeUrl(string text)
        {
            var output = new StringBuilder();
            foreach (byte value in StrictUtf8.GetBytes(text))
            {
                if ((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9') || value == '-' || value == '_' || value == '.' || value == '~') output.Append((char)value);
                else output.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
            return output.ToString();
        }
        private static string DecodeUrl(string text)
        {
            using (var bytes = new MemoryStream())
            {
                int position = 0;
                while (position < text.Length)
                {
                    if (text[position] == '%')
                    {
                        if (position + 2 >= text.Length || Hex(text[position + 1]) < 0 || Hex(text[position + 2]) < 0) throw new ArgumentException("URL 中的 % 后必须有两位十六进制数字，原文已保留。");
                        bytes.WriteByte((byte)(Hex(text[position + 1]) * 16 + Hex(text[position + 2]))); position += 3;
                    }
                    else
                    {
                        int next = text.IndexOf('%', position); if (next < 0) next = text.Length;
                        byte[] part = StrictUtf8.GetBytes(text.Substring(position, next - position)); bytes.Write(part, 0, part.Length); position = next;
                    }
                }
                return StrictUtf8.GetString(bytes.ToArray());
            }
        }
        private static int Hex(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

        // Validate structure while retaining each numeric/string token verbatim. Deserializing
        // and reserializing would round large numeric values or silently replace duplicate keys.
        private sealed class JsonLayout
        {
            private readonly string text; private readonly bool pretty; private int position;
            private readonly StringBuilder output = new StringBuilder();
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
            public JsonLayout(string value, bool indented) { text = value; pretty = indented; }
            public string Format() { Space(); Value(0); Space(); if (position != text.Length) Fail(); return output.ToString(); }
            private void Value(int depth)
            {
                if (depth > 48) throw new ArgumentException("JSON 嵌套超过 48 层，请简化后重试。原文已保留。");
                Space(); if (position >= text.Length) Fail(); char c = text[position];
                if (c == '{') Object(depth); else if (c == '[') Array(depth); else if (c == '"') output.Append(StringToken());
                else if (c == 't') Literal("true"); else if (c == 'f') Literal("false"); else if (c == 'n') Literal("null"); else Number();
                if (output.Length > MaximumOutputLength) throw new ArgumentException("JSON 排版结果过长，请分段处理。原文已保留。");
            }
            private void Object(int depth)
            {
                position++; output.Append('{'); Space(); if (Take('}')) { output.Append('}'); return; }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    NewLine(depth + 1); Space(); string token = StringToken(); string key;
                    try { key = serializer.Deserialize<string>(token); } catch { Fail(); return; }
                    if (!keys.Add(key)) throw new ArgumentException("JSON 包含重复字段名；为避免覆盖数据，未执行转换。原文已保留。");
                    output.Append(token); Space(); if (!Take(':')) Fail(); output.Append(pretty ? ": " : ":"); Value(depth + 1); Space();
                    if (Take('}')) { NewLine(depth); output.Append('}'); return; }
                    if (!Take(',')) Fail(); output.Append(',');
                }
            }
            private void Array(int depth)
            {
                position++; output.Append('['); Space(); if (Take(']')) { output.Append(']'); return; }
                while (true)
                {
                    NewLine(depth + 1); Value(depth + 1); Space();
                    if (Take(']')) { NewLine(depth); output.Append(']'); return; }
                    if (!Take(',')) Fail(); output.Append(',');
                }
            }
            private string StringToken()
            {
                int start = position; if (!Take('"')) Fail();
                while (position < text.Length)
                {
                    char c = text[position++]; if (c == '"') return text.Substring(start, position - start);
                    if (c < 32) Fail();
                    if (c == '\\')
                    {
                        if (position >= text.Length) Fail(); char escaped = text[position++];
                        if (escaped == 'u') { for (int i = 0; i < 4; i++) { if (position >= text.Length || Hex(text[position++]) < 0) Fail(); } }
                        else if ("\"\\/bfnrt".IndexOf(escaped) < 0) Fail();
                    }
                }
                Fail(); return null;
            }
            private void Number()
            {
                int start = position; Take('-');
                if (Take('0')) { if (position < text.Length && IsDigit(text[position])) Fail(); }
                else { if (position >= text.Length || text[position] < '1' || text[position] > '9') Fail(); while (position < text.Length && IsDigit(text[position])) position++; }
                if (Take('.')) { int fractional = position; while (position < text.Length && IsDigit(text[position])) position++; if (fractional == position) Fail(); }
                if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); int exponent = position; while (position < text.Length && IsDigit(text[position])) position++; if (exponent == position) Fail(); }
                output.Append(text, start, position - start);
            }
            private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
            private void Literal(string token) { if (position + token.Length > text.Length || String.CompareOrdinal(text, position, token, 0, token.Length) != 0) Fail(); position += token.Length; output.Append(token); }
            private bool Take(char c) { if (position >= text.Length || text[position] != c) return false; position++; return true; }
            private void Space() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
            private void NewLine(int depth) { if (pretty) output.Append('\n').Append(' ', depth * 2); }
            private void Fail() { throw new ArgumentException("JSON 格式无效（第 " + (position + 1).ToString(CultureInfo.InvariantCulture) + " 个文本单元附近）。原文已保留。"); }
        }
    }
}
