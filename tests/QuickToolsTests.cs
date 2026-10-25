using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace CodexIsland
{
    internal static class QuickToolsTests
    {
        public static void Run()
        {
            CalculatorTests();
            CalculatorPanelTests();
            ShortcutTests();
            ShortcutFailureTests();
        }

        private static void CalculatorPanelTests()
        {
            var panel = new CalculatorPanel(true);
            var expression = (System.Windows.Controls.TextBox)typeof(CalculatorPanel).GetField("expression", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
            var result = (System.Windows.Controls.TextBlock)typeof(CalculatorPanel).GetField("result", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
            var buttons = panel.Children.OfType<System.Windows.Controls.Panel>().SelectMany(x => x.Children.OfType<System.Windows.Controls.Button>());
            var copy = buttons.First(x => (string)x.Content == "复制结果");
            Check(result.Text == "230.4" && copy.IsEnabled, "A successful calculation produces a copyable result");
            expression.Text = "1 / 0";
            Check(result.Text == "—" && !copy.IsEnabled, "Editing an expression invalidates the old result immediately");
            panel.Calculate();
            Check(result.Text == "—" && !copy.IsEnabled, "A failed calculation cannot leave a stale value available for copying");
            expression.Text = "2 + 3"; panel.Calculate();
            Check(result.Text == "5" && copy.IsEnabled, "A valid calculation recovers after a previous error");
            expression.Text = "";
            Check(result.Text == "—" && !copy.IsEnabled, "Clearing the expression removes the previous calculation");
        }

        private static void CalculatorTests()
        {
            Near(Calculator.Evaluate("2 + 3 * 4"), 14, "Multiplication precedes addition");
            Near(Calculator.Evaluate("(2 + 3) * 4"), 20, "Parentheses override precedence");
            Near(Calculator.Evaluate(" 12 / 3 / 2 "), 2, "Division associates left to right");
            Near(Calculator.Evaluate("10 - 3 - 2"), 5, "Subtraction associates left to right");
            Near(Calculator.Evaluate("-2^2"), -4, "Exponentiation precedes unary minus");
            Near(Calculator.Evaluate("(-2)^2"), 4, "Parenthesized negative power base");
            Near(Calculator.Evaluate("2^3^2"), 512, "Powers associate right to left");
            Near(Calculator.Evaluate("2^-3"), 0.125, "Negative exponents");
            Near(Calculator.Evaluate("--2 + +3"), 5, "Unary signs");
            Near(Calculator.Evaluate("250 * 15%"), 37.5, "Percentage is a fraction of one hundred");
            Near(Calculator.Evaluate("(25 + 25)%"), 0.5, "Percentage can follow a parenthesized expression");
            Near(Calculator.Evaluate("sqrt(144) + abs(-2) + round(2.5)"), 17, "Supported functions compose");
            Near(Calculator.Evaluate("ROUND(-2.5)"), -3, "Function names ignore case and round ties away from zero");
            Near(Calculator.Evaluate("pi + e"), Math.PI + Math.E, "Mathematical constants");
            Near(Calculator.Evaluate("1e3 + 2E-2"), 1000.02, "Scientific notation");
            Near(Calculator.Evaluate(".5 + 1."), 1.5, "Decimal edge notation");
            Near(Calculator.Evaluate("\t2 +\n 4"), 6, "Whitespace in expressions");
            foreach (string invalid in new[] { null, "", " ", "1/0", "0/0", "sqrt(-1)", "(-1)^0.5", "1e309", "1e308 * 10", "2^99999", "(1+2", "1+2)", "2+", "1..2", "1e", "1e-", "2 3", "2(3)", "sin(2)", "abs 2", "round()", "sqrt(1,2)", "NaN", "Infinity", "1;2", "System.IO.File.Delete(1)" })
                RejectExpression(invalid);
            RejectExpression(new string('(', 40) + "1" + new string(')', 40));
            RejectExpression("1" + new string(' ', 256));

            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Near(Calculator.Evaluate("1.5 + 2.25"), 3.75, "Expression decimals stay invariant across Windows locales");
                RejectExpression("1,5 + 2");
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }

        private static void ShortcutTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexIslandQuickToolsTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string app = Path.Combine(directory, "工具 APP.EXE");
                string link = Path.Combine(directory, "快捷入口.lnk");
                File.WriteAllText(app, "test fixture, never executed");
                File.WriteAllText(link, "test fixture, never executed");
                Check(ShortcutStore.IsAllowedTarget(directory), "Existing absolute folder target allowed");
                Check(ShortcutStore.IsAllowedTarget(app), "Existing absolute executable target allowed, case insensitive");
                Check(ShortcutStore.IsAllowedTarget(link), "Existing shortcut target allowed");
                Check(ShortcutStore.IsAllowedTarget("https://example.com/tools?q=hello#top"), "HTTPS URL target allowed");
                Check(ShortcutStore.IsAllowedTarget("http://localhost:8000/"), "HTTP URL target allowed without connecting");
                foreach (string extension in new[] { ".cmd", ".bat", ".ps1", ".vbs", ".js", ".hta", ".py", ".txt" })
                {
                    string script = Path.Combine(directory, "fixture" + extension);
                    File.WriteAllText(script, "test fixture, never executed");
                    Check(!ShortcutStore.IsAllowedTarget(script), "Script and unsupported file target rejected: " + extension);
                }
                foreach (string invalid in new[] { null, "", " ", "shell:AppsFolder", "ms-settings:privacy", "javascript:alert(1)", "data:text/html,hello", "file:///C:/Windows/notepad.exe", "ftp://example.com/file", "https://", "www.example.com", "notepad.exe", "powershell.exe -Command exit", app + " --argument", Path.Combine(directory, "missing.exe"), Path.Combine(directory, "missing-folder"), new string('x', 2049), directory + "\\bad\0.exe", directory + "\\bad<name>.exe" })
                    Check(!ShortcutStore.IsAllowedTarget(invalid), "Unsupported or malformed shortcut rejected without throwing");

                string path = Path.Combine(directory, "nested", "shortcuts.json");
                var store = new ShortcutStore(path);
                Check(store.Load().Count == 0 && !File.Exists(path), "Missing shortcut data loads empty without writing");
                store.Save(new[] { new QuickShortcut { Name = "常用 工具", Target = app }, new QuickShortcut { Name = "文档", Target = "https://example.com/文档" } });
                var loaded = store.Load();
                Check(loaded.Count == 2 && loaded[0].Name == "常用 工具" && loaded[0].Target == app && loaded[1].Target == "https://example.com/文档", "Names, order, Unicode and targets round trip");
                store.Save(new[] { new QuickShortcut { Name = "文件夹", Target = directory } });
                Check(store.Load().Count == 1 && store.Load()[0].Target == directory, "Saving replaces previous shortcuts");
                Check(!File.Exists(path + ".tmp"), "Shortcut write leaves no temporary file");
                store.Save(Enumerable.Range(0, 25).Select(i => new QuickShortcut { Name = "入口 " + i, Target = "https://example.com/" + i }));
                loaded = store.Load();
                Check(loaded.Count == 20 && loaded[19].Name == "入口 19", "Shortcut count bounded to twenty in original order");
                store.Save(new[] { null, new QuickShortcut { Name = " ", Target = app }, new QuickShortcut { Name = "empty", Target = " " }, new QuickShortcut { Name = new string('名', 50), Target = "https://example.com/" + new string('x', 2200) } });
                loaded = store.Load();
                Check(loaded.Count == 1 && loaded[0].Name.Length == 35 && loaded[0].Target.Length == 2048, "Malformed entries filtered and display data bounded");
                foreach (string malformed in new[] { "{broken", "null", "42", "{}", "[null,{}, {\"Name\":\"\",\"Target\":\"https://example.com\"}]" })
                {
                    File.WriteAllText(path, malformed);
                    Check(store.Load().Count == 0, "Malformed shortcut data degrades to an empty collection");
                }
                store.Save(new List<QuickShortcut>());
                Check(store.Load().Count == 0, "Clearing shortcuts persists an empty collection");
            }
            finally
            {
                string full = Path.GetFullPath(directory);
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("CodexIslandQuickToolsTests-", StringComparison.Ordinal)) Directory.Delete(full, true);
            }
        }

        private static void ShortcutFailureTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexIslandShortcutRecovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string path = Path.Combine(directory, "shortcuts.json");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                var store = new ShortcutStore(path); store.Save(new[] { new QuickShortcut { Name = "keep this test link", Target = "https://example.com/" } });
                var panel = new LauncherPanel(false);
                var list = (System.Windows.Controls.StackPanel)typeof(LauncherPanel).GetField("list", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                var row = (System.Windows.Controls.DockPanel)list.Children[0];
                var remove = row.Children.OfType<System.Windows.Controls.Button>().Single(button => (string)button.Content == "×");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    remove.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Check(list.Children.OfType<System.Windows.Controls.DockPanel>().Count() == 1 && store.Load().Count == 1, "A failed shortcut delete keeps its existing UI entry and saved file intact");
                    Check(!panel.TryAddShortcut(new QuickShortcut { Name = "unsaved link", Target = "https://example.com/pending" }), "A failed shortcut add reports failure so the edit dialog remains open");
                    Check(list.Children.OfType<System.Windows.Controls.DockPanel>().Count() == 1 && store.Load().Count == 1, "Failed shortcut insertion does not leave an entry that disappears after restart");
                }
                remove.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(store.Load().Count == 0 && list.Children.OfType<System.Windows.Controls.DockPanel>().Count() == 0, "Shortcut removal succeeds when storage becomes writable again");
                File.WriteAllText(path, "[{\"Name\":\"recover this entry");
                var damaged = new ShortcutStore(path); damaged.Load(); damaged.Save(new[] { new QuickShortcut { Name = "new link", Target = "https://example.com/new" } });
                Check(Directory.GetFiles(directory, "*.bak").Any(backup => File.ReadAllText(backup) == "[{\"Name\":\"recover this entry"), "Editing shortcuts after corruption preserves the unreadable original");
                File.WriteAllText(path, new string('x', 129 * 1024));
                Check(damaged.Load().Count == 0 && damaged.LoadFailed, "Oversized shortcut metadata is rejected before parsing");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); Directory.Delete(directory, true); }
        }

        private static void Near(double actual, double expected, string label) { Check(Math.Abs(actual - expected) <= Math.Max(1, Math.Abs(expected)) * 1e-12, label); }
        private static void RejectExpression(string expression)
        {
            try { Calculator.Evaluate(expression); }
            catch (ArgumentException) { return; }
            throw new Exception("Quick tools: Invalid expression was accepted");
        }
        private static void Check(bool value, string label) { if (!value) throw new Exception("Quick tools: " + label); }
    }
}
