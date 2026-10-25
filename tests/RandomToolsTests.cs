using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;

namespace CodexIsland
{
    internal static class RandomToolsTests
    {
        private static void Check(bool value, string label) { if (!value) throw new Exception("Random tools: " + label); }
        private static void Throws(Action action, string label) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Random tools: expected rejection: " + label); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void RunPanel(RandomToolsPanel panel, bool all = false) { panel.GetType().GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, new object[] { all }); }
        public static void Run()
        {
            Throws(() => RandomTools.Password(null), "missing options");
            foreach (int length in new[] { -1, 0, 7, 129, Int32.MaxValue }) Throws(() => RandomTools.Password(new PasswordOptions { Length = length }), "password length bounds");
            Throws(() => RandomTools.Password(new PasswordOptions { Uppercase = false, Lowercase = false, Digits = false, Symbols = false }), "empty alphabet");
            for (int mask = 1; mask < 16; mask++)
                foreach (bool exclude in new[] { false, true })
                    foreach (int length in new[] { 8, 20, 128 })
                    {
                        var options = new PasswordOptions { Length = length, Uppercase = (mask & 1) != 0, Lowercase = (mask & 2) != 0, Digits = (mask & 4) != 0, Symbols = (mask & 8) != 0, ExcludeAmbiguous = exclude };
                        var groups = RandomTools.PasswordGroups(options); string alphabet = String.Concat(groups);
                        for (int sample = 0; sample < 8; sample++)
                        {
                            string value = RandomTools.Password(options); Check(value.Length == length, "password length");
                            Check(value.All(c => alphabet.IndexOf(c) >= 0), "every character belongs to selected alphabet");
                            Check(groups.All(group => value.Any(c => group.IndexOf(c) >= 0)), "every selected category is present");
                            Check(!exclude || value.All(c => RandomTools.Ambiguous.IndexOf(c) < 0), "excluded ambiguous characters never occur");
                        }
                    }
            Throws(() => RandomTools.Integer(1, -1), "reversed integer range");
            foreach (int constant in new[] { Int32.MinValue, -1, 0, 1, Int32.MaxValue }) Check(RandomTools.Integer(constant, constant) == constant, "inclusive equal endpoints");
            for (int sample = 0; sample < 200; sample++)
            {
                Check(RandomTools.Integer(Int32.MinValue, Int32.MaxValue) >= Int32.MinValue, "full integer range has no overflow");
                int nearLow = RandomTools.Integer(Int32.MinValue, Int32.MinValue + 1), nearHigh = RandomTools.Integer(Int32.MaxValue - 1, Int32.MaxValue), negative = RandomTools.Integer(-7, -3);
                Check(nearLow == Int32.MinValue || nearLow == Int32.MinValue + 1, "low boundary membership"); Check(nearHigh == Int32.MaxValue - 1 || nearHigh == Int32.MaxValue, "high boundary membership"); Check(negative >= -7 && negative <= -3, "negative range membership");
            }
            using (var random = new SecureRandom()) { Check(random.Below(1) == 0, "one bucket"); Throws(() => random.Below(0), "zero bound"); Throws(() => random.Below((1UL << 32) + 1), "oversized bound"); }

            var original = RandomTools.ParseList(" A \r\nB\r A \n\t \nA\na\n", false); Check(original.Items.SequenceEqual(new[] { " A ", "B", " A ", "A", "a" }), "mixed newlines preserve original spaces and case, blank lines ignored");
            var unique = RandomTools.ParseList(" A \r\nB\r A \n\t \nA\na\n", true); Check(unique.OriginalCount == 5 && unique.DuplicatesRemoved == 1 && unique.Items.SequenceEqual(new[] { " A ", "B", "A", "a" }), "deduplication is exact ordinal and stable");
            Throws(() => RandomTools.ParseList(new string('a', RandomTools.MaximumListLength + 1), false), "total text bound");
            Throws(() => RandomTools.ParseList(String.Join("\n", Enumerable.Repeat("duplicate", 501)), true), "entry bound applies before deduplication");
            Check(RandomTools.ParseList(new string('x', RandomTools.MaximumListLength), false).Items.Count == 1, "text limit inclusive");
            Throws(() => RandomTools.Draw(RandomTools.ParseList(" \n\t", true), 1), "empty entries"); Throws(() => RandomTools.Draw(null, 1), "missing list"); Throws(() => RandomTools.Draw(unique, 0), "zero draw"); Throws(() => RandomTools.Draw(unique, 5), "draw beyond population");
            var large = RandomTools.ParseList(String.Join("\n", Enumerable.Range(0, 500).Select(x => "项 " + x)), true); string before = String.Join("\n", large.Items);
            for (int sample = 0; sample < 20; sample++)
            {
                var chosen = RandomTools.Draw(large, 73); Check(chosen.Count == 73 && chosen.Distinct().Count() == 73 && chosen.All(large.Items.Contains), "draw without replacement");
                var shuffled = RandomTools.Draw(large, large.Items.Count); Check(shuffled.Count == 500 && new HashSet<string>(shuffled).SetEquals(large.Items), "shuffle retains entire multiset");
            }
            Check(before == String.Join("\n", large.Items), "drawing leaves source unchanged"); Check(RandomTools.Draw(original, original.Items.Count).Count(x => x == " A ") == 2, "duplicate rows retain weighting when deduplication is off");
            PanelTests(); Console.WriteLine("PASS: secure password categories, full integer bounds, list draws, input invalidation and recovery.");
        }
        private static void PanelTests()
        {
            var panel = new RandomToolsPanel(true); var mode = Field<ComboBox>(panel, "mode"); var output = Field<TextBox>(panel, "output"); var length = Field<TextBox>(panel, "length"); var copy = Field<Button>(panel, "copy");
            Check(output.Text.StartsWith("示例：") && copy.IsEnabled, "demo is explicitly marked");
            length.Text = "7"; Check(output.Text == "" && !copy.IsEnabled, "input invalidates previous password"); RunPanel(panel); Check(!copy.IsEnabled && !Field<bool>(panel, "hasResult"), "bad password bounds leave no result");
            length.Text = "32"; RunPanel(panel); Check(output.Text.Length == 32 && copy.IsEnabled, "password recovers after error");
            foreach (string name in new[] { "uppercase", "lowercase", "digits", "symbols" }) Field<CheckBox>(panel, name).IsChecked = false;
            Check(!copy.IsEnabled && output.Text == "", "category options invalidate password"); RunPanel(panel); Check(output.Text == "" && !copy.IsEnabled, "no categories rejected"); Field<CheckBox>(panel, "digits").IsChecked = true; RunPanel(panel); Check(output.Text.Length == 32 && output.Text.All(Char.IsDigit), "single-category recovery");
            mode.SelectedIndex = 1; Check(output.Text == "" && !copy.IsEnabled, "mode invalidates old result"); Field<TextBox>(panel, "minimum").Text = "100"; Field<TextBox>(panel, "maximum").Text = "-1"; RunPanel(panel); Check(!copy.IsEnabled, "reversed range rejected by real controls");
            Field<TextBox>(panel, "minimum").Text = "-2147483648"; Field<TextBox>(panel, "maximum").Text = "-2147483648"; RunPanel(panel); Check(output.Text == "-2147483648", "integer minimum valid via UI"); Field<TextBox>(panel, "maximum").Text = "2147483648"; RunPanel(panel); Check(!copy.IsEnabled, "overflow rejected via UI");
            mode.SelectedIndex = 2; var input = Field<TextBox>(panel, "listInput"); var count = Field<TextBox>(panel, "drawCount"); input.Text = "same\nsame\nother"; count.Text = "3"; RunPanel(panel); Check(!copy.IsEnabled, "deduplicated population bound");
            Field<CheckBox>(panel, "deduplicate").IsChecked = false; RunPanel(panel); Check(output.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 3 && copy.IsEnabled, "duplicates preserved when opted in");
            count.Text = "0"; Check(output.Text == "" && !copy.IsEnabled, "count invalidates old draw"); RunPanel(panel); Check(!copy.IsEnabled, "zero count rejected"); RunPanel(panel, true); Check(copy.IsEnabled && output.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 3, "shuffle ignores separate draw count");
            input.Text = ""; RunPanel(panel, true); Check(!copy.IsEnabled && output.Text == "", "empty shuffle rejected"); input.Text = "唯一项"; count.Text = "1"; RunPanel(panel); Check(output.Text == "唯一项", "list recovers after error");
            panel.GetType().GetMethod("Copy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null); Check(Field<TextBlock>(panel, "status").Text.Contains("不写入剪贴板"), "demo copy guard");
            var live = new RandomToolsPanel(false); Check(Field<TextBox>(live, "output").Text == "" && !Field<bool>(live, "hasResult"), "real panel does not generate secrets at startup");
        }
    }
}
