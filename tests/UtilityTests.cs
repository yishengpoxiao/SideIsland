using System;
using System.Linq;

namespace CodexIsland
{
    internal static class UtilityTests
    {
        private static void Check(bool condition, string label) { if (!condition) throw new Exception("Utility tools: " + label); }
        private static void Near(double actual, double expected, string label) { Check(Math.Abs(actual - expected) <= Math.Max(1e-10, Math.Abs(expected) * 1e-12), label); }
        private static void Throws(Action action, string label) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Utility tools: expected failure for " + label); }
        public static void Run()
        {
            foreach (var category in ConversionTools.Categories)
            {
                var first = category.Units[0];
                foreach (var unit in category.Units)
                {
                    double converted = ConversionTools.Convert(category, first, unit, 123.456);
                    Near(ConversionTools.Convert(category, unit, first, converted), 123.456, category.Name + " conversion roundtrip " + unit.Name);
                }
            }
            var length = ConversionTools.Categories[0]; Near(ConversionTools.Convert(length, length.Units[4], length.Units[0], 1), .0254, "exact inch to metre");
            var temperature = ConversionTools.Categories[2]; var c = temperature.Units[0]; var f = temperature.Units[1]; var k = temperature.Units[2];
            Near(ConversionTools.Convert(temperature, c, f, 0), 32, "Celsius affine freezing point");
            Near(ConversionTools.Convert(temperature, f, c, 212), 100, "Fahrenheit boiling point");
            Near(ConversionTools.Convert(temperature, c, f, -40), -40, "temperature negative crossover");
            Near(ConversionTools.Convert(temperature, f, k, -459.67), 0, "Fahrenheit absolute zero roundoff");
            Near(ConversionTools.Convert(temperature, k, c, 0), -273.15, "Kelvin absolute zero");
            Throws(() => ConversionTools.Convert(temperature, c, k, -273.1501), "below absolute zero Celsius");
            Throws(() => ConversionTools.Convert(temperature, f, k, -459.6701), "below absolute zero Fahrenheit");
            Throws(() => ConversionTools.Convert(temperature, k, c, -0.00000000001), "negative Kelvin");
            foreach (string invalid in new[] { "NaN", "Infinity", "1,000", "", "1e999", "-Infinity" }) Throws(() => ConversionTools.Parse(invalid), "invalid numeric input " + invalid);
            Near(ConversionTools.Parse(" 1.25e3 "), 1250, "scientific input");
            Throws(() => ConversionTools.Convert(length, length.Units[1], length.Units[0], Double.MaxValue), "conversion overflow");
            Near(ConversionTools.Convert(length, length.Units[1], length.Units[1], 1e307), 1e307, "same-unit large finite values do not overflow through base units");
            Near(ConversionTools.Convert(length, length.Units[1], length.Units[7], 1e307), 1e307 * (1000 / 1609.344), "large finite conversion uses a ratio without intermediate overflow");
            Check(ConversionTools.Convert(length, length.Units[3], length.Units[3], Double.Epsilon) == Double.Epsilon, "same-unit smallest value does not underflow through base units");
            Throws(() => ConversionTools.Convert(length, length.Units[0], k, 1), "cross-category units");
            var data = ConversionTools.Categories[6]; Near(ConversionTools.Convert(data, data.Units[6], data.Units[2], 1), 1.024, "binary kibibyte vs decimal kilobyte");
            Near(ConversionTools.Convert(data, data.Units[0], data.Units[1], 1), 8, "bits vs bytes");

            string unicode = "Hello, 世界 🌕 e\u0301 + / ? # %";
            foreach (var pair in new[] { new[] { "base64-encode", "base64-decode" }, new[] { "url-encode", "url-decode" } })
                Check(TextTools.Transform(pair[1], TextTools.Transform(pair[0], unicode)) == unicode, pair[0] + " Unicode roundtrip");
            Check(TextTools.Transform("url-encode", "a b+c/~") == "a%20b%2Bc%2F~", "RFC3986 component encoding");
            Check(TextTools.Transform("url-decode", "a+b") == "a+b", "literal plus remains plus");
            foreach (string malformed in new[] { "%", "%1", "%GG", "%FF", "%C0%AF", "%ED%A0%80" }) Throws(() => TextTools.Transform("url-decode", malformed), "strict URL decoding " + malformed);
            foreach (string malformed in new[] { "!!!", "SGV", "/w==" }) Throws(() => TextTools.Transform("base64-decode", malformed), "strict Base64 and UTF8");
            Throws(() => TextTools.Transform("base64-encode", "\ud800"), "unpaired surrogate encoding");
            Check(TextTools.Transform("sha256", "abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "known SHA256 vector");
            Check(TextTools.Transform("deduplicate", "a\r\nb\ra\nb") == "a\nb", "stable dedup and mixed line endings");
            Check(TextTools.Transform("nonempty", "a\n \t\nb\n") == "a\nb", "remove whitespace-only lines");
            Check(TextTools.Transform("trim", "  a \n b ") == "a\nb", "per-line whitespace trim");
            var stats = TextTools.Statistics("e\u0301\r\nhello 世界"); Check(stats.Words == 3 && stats.Lines == 2, "Unicode word groups and CRLF lines");
            Check(TextTools.Statistics("e\u0301").Characters == 1, "combining character text element count");
            Check(TextTools.Statistics("").Lines == 0, "empty text has zero lines");
            string json = "{\"number\":90071992547409931234567890,\"fraction\":1.2300000e-999,\"escaped\":\"a\\n\\u4f60\",\"items\":[null,true,false,{\"x\":-0}]}";
            string pretty = TextTools.Transform("json-pretty", json);
            Check(pretty.Contains("\n  ") && TextTools.Transform("json-compact", pretty) == json, "JSON preserves all numeric and escaped lexemes");
            Check(TextTools.Transform("json-compact", "  [ 1, { \"x\" : [] } ] \n") == "[1,{\"x\":[]}]", "JSON compact structure");
            Check(TextTools.Transform("json-compact", "{\"a\":1,\"nested\":{\"a\":2}}") == "{\"a\":1,\"nested\":{\"a\":2}}", "same field in different objects accepted");
            foreach (string invalid in new[] { "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "[1,]", "{\"x\":1,}", "01", "1.", "1e", "true false", "{x:1}", "{\"x\":NaN}", "\"\\x41\"", "\"a\nb\"", "", "[", "-", "{\"a\":/*comment*/1}" })
                Throws(() => TextTools.Transform("json-pretty", invalid), "strict JSON parsing");
            Throws(() => TextTools.Transform("json-pretty", new String('[', 50) + "0" + new String(']', 50)), "JSON nesting limit");
            Throws(() => TextTools.Transform("upper", new String('a', TextTools.MaximumInputLength + 1)), "text input bound");
            Throws(() => TextTools.Transform("url-encode", new String('中', TextTools.MaximumInputLength)), "encoded result bound");
            PanelInteractionTests();
            Console.WriteLine("PASS: unit conversion, temperature bounds, Unicode encoding, strict lossless JSON and text tools.");
        }

        private static T Field<T>(object owner, string name)
        {
            return (T)owner.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
        }
        private static void Invoke(object owner, string name)
        {
            owner.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(owner, null);
        }
        private static void PanelInteractionTests()
        {
            var panel = new TextToolsPanel(true);
            var input = Field<System.Windows.Controls.TextBox>(panel, "input");
            var output = Field<System.Windows.Controls.TextBox>(panel, "output");
            var operation = Field<System.Windows.Controls.ComboBox>(panel, "operation");
            Check(output.Text.Contains("你好 Side Island"), "text panel creates its demo result without clipboard access");
            input.Text = "{\"broken\":}"; operation.SelectedIndex = 5; Invoke(panel, "Run");
            Check(input.Text == "{\"broken\":}" && output.Text == "" && !Field<bool>(panel, "hasResult"), "text panel retains invalid JSON input and cannot copy the previous result");
            input.Text = "你好 + 🌕"; operation.SelectedIndex = 9; Invoke(panel, "Run");
            string encoded = output.Text; Invoke(panel, "Apply");
            Check(input.Text == encoded && output.Text == "" && !Field<bool>(panel, "hasResult"), "using a result as input preserves its content and invalidates the old result");
            operation.SelectedIndex = 10; Invoke(panel, "Run");
            Check(output.Text == "你好 + 🌕", "real text controls recover from an earlier error and roundtrip Unicode");
            input.Text = new string(' ', TextTools.MaximumInputLength); operation.SelectedIndex = 7; Invoke(panel, "Run");
            Check(output.Text.Length == 3 * TextTools.MaximumInputLength, "large valid encoded output remains available");
            string original = input.Text; Invoke(panel, "Apply");
            Check(input.Text == original && output.Text.Length == 3 * TextTools.MaximumInputLength, "oversized result cannot silently truncate when moved into input");

            var converter = new ConverterPanel(true);
            var numeric = Field<System.Windows.Controls.TextBox>(converter, "input");
            var category = Field<System.Windows.Controls.ComboBox>(converter, "category");
            numeric.Text = "1 / 2";
            Check(Field<string>(converter, "copyValue") == null, "invalid unit input clears the copyable result");
            category.SelectedIndex = 2; numeric.Text = "-274";
            Check(Field<string>(converter, "copyValue") == null, "temperature controls enforce absolute zero");
            numeric.Text = "0";
            Near(ConversionTools.Parse(Field<string>(converter, "copyValue")), 32, "temperature control recovers after invalid input");
            Invoke(converter, "Swap");
            Near(ConversionTools.Parse(numeric.Text), 32, "swap uses the previous converted value as the new input");
            Near(ConversionTools.Parse(Field<string>(converter, "copyValue")), 0, "swap returns the previous original temperature");
        }
    }
}
