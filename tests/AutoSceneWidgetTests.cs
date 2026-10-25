using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal static class AutoSceneWidgetTests
    {
        internal static void Run()
        {
            RuleTests(); WidgetTests(); PersistenceTests(); PanelTests();
            Console.WriteLine("PASS: automatic scene conditions/overnight/priority/debounce/cooldown/manual pause, widget ordering/rotation/profile fallback, persistence isolation and bilingual narrow/wide detached layouts.");
        }
        private static void RuleTests()
        {
            var rule = new AutoSceneRule { Name = "night coding", Executable = "Code.exe", Start = "22:30", End = "06:15", Monitor = "DISPLAY2", Power = "ac", ProfileId = "code", Priority = 80, CooldownSeconds = 30 };
            string error; Check(AutoScenePolicy.Validate(rule, out error), "Valid compound overnight rule");
            var context = new AutoSceneContext { Executable = "CODE", Monitor = "display2", OnAc = true, LocalTime = new DateTime(2026, 1, 2, 23, 0, 0) };
            Check(AutoScenePolicy.Matches(rule, context), "Executable suffix/case and overnight before midnight match");
            context.LocalTime = new DateTime(2026, 1, 3, 6, 14, 0); Check(AutoScenePolicy.Matches(rule, context), "Overnight early morning matches");
            context.LocalTime = context.LocalTime.AddMinutes(1); Check(!AutoScenePolicy.Matches(rule, context), "End minute is exclusive");
            context.LocalTime = context.LocalTime.AddHours(17); context.OnAc = false; Check(!AutoScenePolicy.Matches(rule, context), "Power AND condition cannot be bypassed");
            context.OnAc = true; context.OwnProcess = true; Check(!AutoScenePolicy.Matches(rule, context), "Island foreground never triggers a rule");
            context.OwnProcess = false;
            var invalid = rule.Copy(); invalid.Start = "24:00"; Check(!AutoScenePolicy.Validate(invalid, out error), "Invalid clock rejected");
            invalid = rule.Copy(); invalid.Executable = @"C:\Apps\Code.exe"; Check(!AutoScenePolicy.Validate(invalid, out error), "Only executable names, not paths or commands");
            invalid = rule.Copy(); invalid.Name = null; Check(!AutoScenePolicy.Validate(invalid, out error), "Malformed persisted null fields rejected");
            var low = new AutoSceneRule { Name = "app", Executable = "Code", ProfileId = "music", Priority = 10, CooldownSeconds = 5 };
            var high = new AutoSceneRule { Name = "high", Executable = "code.exe", ProfileId = "code", Priority = 90, CooldownSeconds = 5 };
            var data = new AutoSceneRuleData { Enabled = true, Rules = new List<AutoSceneRule> { low, high } };
            var engine = new AutoSceneRuleEngine(); DateTime now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
            Check(engine.Evaluate(data, context, now, id => true) == null, "Initial match waits for foreground stability");
            Check(engine.Evaluate(data, context, now.AddMilliseconds(1199), id => true) == null, "Debounce cannot fire early");
            Check(engine.Evaluate(data, context, now.AddMilliseconds(1200), id => true) == high, "Highest priority wins after stable match");
            engine.MarkApplied(high, now.AddMilliseconds(1200));
            Check(engine.Evaluate(data, context, now.AddSeconds(20), id => true) == null, "Same matching rule does not repeatedly overwrite manual state");
            high.Enabled = false; Check(engine.Evaluate(data, context, now.AddSeconds(2), id => true) == null, "New candidate also debounces");
            Check(engine.Evaluate(data, context, now.AddSeconds(4), id => true) == null, "Cross-rule cooldown still applies");
            Check(engine.Evaluate(data, context, now.AddSeconds(8), id => true) == low, "Next rule fires after its cooldown");
            engine.Pause(now.AddSeconds(8), TimeSpan.FromMinutes(10)); Check(engine.Evaluate(data, context, now.AddMinutes(2), id => true) == null, "Manual pause blocks switching");
            engine.Resume(); Check(engine.Evaluate(data, context, now.AddMinutes(2), id => false) == null, "Removed target profile does not apply");
            data.Enabled = false; Check(engine.Evaluate(data, context, now.AddMinutes(20), id => true) == null, "Global disabled is inert");
        }
        private static void WidgetTests()
        {
            var layout = WidgetLayoutPolicy.Default("", new Settings());
            layout.Enabled = true; layout.RotateSeconds = 5; layout.Cards.Add(new WidgetCardDefinition { Key = "clock", Size = "small" });
            Check(WidgetLayoutPolicy.Valid(layout), "Widget pool validates known keys and safe sizes");
            DateTime origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Check(WidgetLayoutPolicy.Visible(layout, origin, origin)[0].Key == "codex-primary", "First visible pair respects ordering");
            Check(WidgetLayoutPolicy.Visible(layout, origin.AddSeconds(5), origin)[0].Key == "clock", "Odd widget pool rotates in pairs without dropping last card");
            Check(WidgetLayoutPolicy.Visible(layout, origin.AddSeconds(10), origin)[0].Key == "codex-secondary", "Odd pool visits every ordered card");
            Check(WidgetLayoutPolicy.Visible(layout, DateTime.MaxValue, origin).Length == 2, "Long elapsed time cannot overflow rotation");
            var copy = layout.Copy(); copy.Cards[0].Key = "battery"; Check(layout.Cards[0].Key == "codex-primary", "Draft is a deep snapshot");
            layout.ActivityPriority = 95; layout.ActivityOverride = false; copy = layout.Copy(); Check(copy.ActivityPriority == 95 && !copy.ActivityOverride, "Task activity priority and override are part of the saved layout");
            copy.ActivityPriority = 101; Check(!WidgetLayoutPolicy.Valid(copy), "Out-of-range task priority is rejected");
            copy = layout.Copy(); copy.Cards[1].Id = copy.Cards[0].Id; Check(!WidgetLayoutPolicy.Valid(copy), "Duplicate card identity rejected");
            copy = layout.Copy(); copy.Cards[0].Key = "unknown"; Check(!WidgetLayoutPolicy.Valid(copy), "Unknown component rejected");
            copy = layout.Copy(); copy.RotateSeconds = 4; Check(!WidgetLayoutPolicy.Valid(copy), "Over-fast rotation rejected");
            Check(new WidgetCardDefinition { Size = "large" }.Scale <= 1.06 && new WidgetCardDefinition { Size = "small" }.Scale >= .84, "Sizes remain within stable compact footprint");
        }
        private static void PersistenceTests()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "SideIsland.AutoWidgets." + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", directory);
                var store = new AutoSceneRuleStore(false); Check(!store.Data.Enabled, "Automatic rules disabled on first launch");
                var rule = new AutoSceneRule { Name = "中文 / English", Executable = "Code.exe", ProfileId = "code" };
                store.Save(new AutoSceneRuleData { Enabled = true, Rules = new List<AutoSceneRule> { rule } });
                var loaded = new AutoSceneRuleStore(false); Check(loaded.Data.Enabled && loaded.Data.Rules[0].Name == rule.Name, "Compound rule roundtrip preserves user names");
                var widgetStore = new WidgetLayoutStore(false); var global = WidgetLayoutPolicy.Default("", new Settings()); global.Enabled = true; widgetStore.Save(global);
                var custom = global.Copy(); custom.ProfileId = "music"; custom.Cards.Reverse(); widgetStore.Save(custom);
                var widgets = new WidgetLayoutStore(false);
                Check(widgets.Resolve("music").Cards[0].Key == "codex-secondary", "Per-profile order is independent");
                Check(widgets.Resolve("code").Cards[0].Key == "codex-primary", "Profiles without override inherit global");
                File.WriteAllText(Path.Combine(directory, "auto-scene-rules.json"), "{\"Enabled\":true,\"Rules\":[null]}");
                Check(new AutoSceneRuleStore(false).Data.Rules.Count == 0, "Malformed rule entries cannot execute");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static void PanelTests()
        {
            string language = UiLocalization.Language, theme = UiTheme.Name;
            try
            {
                foreach (string lang in new[] { "zh", "en" }) foreach (string color in new[] { "light", "dark" }) foreach (double width in new[] { 232.0, 300.0, 540.0, 820.0 })
                {
                    AppAppearance.Configure(lang, color);
                    var profiles = LayoutProfileStore.Builtins(); var custom = new LayoutProfile { Id = Guid.NewGuid().ToString("N"), Name = "Keep · Edit" }; profiles.Add(custom);
                    var rules = new AutoSceneRuleStore(true); rules.Data.Rules.Add(new AutoSceneRule { Name = "User rule · Edit", ProfileId = custom.Id });
                    var rulePanel = new AutoSceneRulesPanel(rules, () => profiles, () => null, () => { }, () => { }, () => { });
                    Check(PopupDpiTests.Descendants(rulePanel).OfType<TextBlock>().Any(t => t.Text.EndsWith("Keep · Edit", StringComparison.Ordinal)), "Rule description preserves an exact custom profile name " + lang);
                    var widgets = new WidgetLayoutStore(true); var widgetPanel = new WidgetLayoutEditorPanel(widgets, () => profiles, () => new Settings(), () => "", () => { });
                    foreach (FrameworkElement panel in new FrameworkElement[] { rulePanel, widgetPanel })
                    {
                        panel.Measure(new Size(width, Double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
                        Check(panel.DesiredSize.Width <= width + .1 && !Double.IsInfinity(panel.DesiredSize.Height), "Narrow/wide editor remains bounded " + lang + "/" + width);
                    }
                    var field = typeof(WidgetLayoutEditorPanel).GetField("draft", BindingFlags.Instance | BindingFlags.NonPublic);
                    var draft = (WidgetLayoutDefinition)field.GetValue(widgetPanel); string first = draft.Cards[0].Id, second = draft.Cards[1].Id;
                    widgetPanel.Reorder(second, first); Check(draft.Cards[0].Id == second && widgets.Data.Layouts.Count == 0, "Preview reorder edits draft without saving or changing user configuration");
                }
            }
            finally { AppAppearance.Configure(language, theme); }
        }
        private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    }
}
