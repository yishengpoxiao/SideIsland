using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class AutoSceneRulesPanel : StackPanel
    {
        private readonly AutoSceneRuleStore store;
        private readonly Func<IEnumerable<LayoutProfile>> profiles;
        private readonly Func<AutoSceneContext> context;
        private readonly Action changed, pause, resume;
        private AutoSceneRuleData draft;
        private string selectedId;
        private string lastLanguage = "";
        private readonly StackPanel rows = new StackPanel();
        private readonly CheckBox enabled = new CheckBox(), ruleEnabled = new CheckBox();
        private readonly TextBox name = ToolStyle.Input(), executable = ToolStyle.Input(), start = ToolStyle.Input(), end = ToolStyle.Input(), monitor = ToolStyle.Input(), priority = ToolStyle.Input(), cooldown = ToolStyle.Input();
        private readonly ComboBox power = SceneCombo(), profile = SceneCombo();
        private readonly TextBlock status = ToolStyle.Hint(""), runtime = ToolStyle.Hint("");
        internal AutoSceneRulesPanel(AutoSceneRuleStore source, Func<IEnumerable<LayoutProfile>> available, Func<AutoSceneContext> readContext, Action onChanged, Action pauseRules, Action resumeRules)
        {
            store = source; profiles = available; context = readContext; changed = onChanged; pause = pauseRules; resume = resumeRules;
            Children.Add(ToolStyle.Heading("自动场景规则"));
            Children.Add(ToolStyle.Hint("按当前应用、时间、显示器和电源切换布局；多个条件需同时满足。默认关闭，手动切换布局后暂停自动切换 10 分钟。"));
            ConfigureCheck(enabled, "启用自动场景切换"); Children.Add(enabled);
            Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("保存全部规则", SaveAll), IslandWindow.Button("取消未保存修改", Reload)));
            var pauseActions = PanelActionLayout.Create(2, IslandWindow.Button("暂停 30 分钟", () => { pause(); }), IslandWindow.Button("恢复自动规则", () => { resume(); })); pauseActions.Margin = new Thickness(0, 6, 0, 0); Children.Add(pauseActions);
            Children.Add(runtime); Children.Add(rows);
            var editor = new StackPanel();
            editor.Children.Add(ToolStyle.Heading("规则编辑"));
            AddField(editor, "规则名称", name); name.MaxLength = 60;
            ConfigureCheck(ruleEnabled, "启用此规则"); editor.Children.Add(ruleEnabled);
            AddField(editor, "程序名称（留空不限）", executable); executable.MaxLength = 128;
            editor.Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("填入最近的前台应用", CaptureApplication)));
            AddField(editor, "开始时间 HH:mm（留空不限）", start); start.MaxLength = 5;
            AddField(editor, "结束时间 HH:mm（可跨午夜）", end); end.MaxLength = 5;
            AddField(editor, "显示器名称（留空不限）", monitor); monitor.MaxLength = 128;
            power.Items.Add("不限电源"); power.Items.Add("接通电源"); power.Items.Add("使用电池"); AddField(editor, "电源条件", power);
            AddField(editor, "目标场景布局", profile); UiLocalization.Protect(profile);
            priority.MaxLength = 3; cooldown.MaxLength = 4; AddField(editor, "优先级 0—100（较高优先）", priority); AddField(editor, "切换冷却时间（秒）", cooldown);
            var editActions = PanelActionLayout.Create(2, IslandWindow.Button("添加／更新规则", CommitRule), IslandWindow.Button("新建规则", ClearEditor)); editActions.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(editActions);
            Children.Add(ToolStyle.Card(editor)); Children.Add(status); Reload();
            IsVisibleChanged += (s, e) => { if (IsVisible) RefreshProfiles(); };
        }
        private static void ConfigureCheck(CheckBox check, string text)
        { check.Content = IslandWindow.Text(text, 11, "#DDE9E2"); check.Margin = new Thickness(0, 6, 0, 6); }
        private static ComboBox SceneCombo() { return new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 0, 4), FontSize = 12 }; }
        private static void AddField(Panel parent, string caption, UIElement control)
        { parent.Children.Add(ToolStyle.Hint(caption)); parent.Children.Add(control); }
        private void RefreshProfiles()
        {
            string selected = profile.SelectedValue as string;
            profile.Items.Clear(); profile.SelectedValuePath = "Tag";
            foreach (var value in profiles())
            {
                var item = new ComboBoxItem { Content = value.Name, Tag = value.Id };
                if (LayoutProfileStore.IsBuiltin(value.Id)) UiLocalization.Mark(item, ContentControl.ContentProperty, value.Name); else UiLocalization.Protect(item);
                profile.Items.Add(item);
            }
            profile.SelectedValue = selected ?? "code";
        }
        private void Reload()
        {
            draft = new AutoSceneRuleData { Enabled = store.Data.Enabled, Rules = store.Data.Rules.Select(r => r.Copy()).ToList() };
            enabled.IsChecked = draft.Enabled; ClearEditor(); Rebuild(); status.Text = "";
        }
        private void ClearEditor()
        {
            selectedId = null; name.Clear(); executable.Clear(); start.Clear(); end.Clear(); monitor.Clear();
            ruleEnabled.IsChecked = true; power.SelectedIndex = 0; priority.Text = "50"; cooldown.Text = "30"; RefreshProfiles(); profile.SelectedValue = "code";
        }
        private void CaptureApplication()
        {
            var sample = context();
            if (sample == null || sample.OwnProcess || String.IsNullOrWhiteSpace(sample.Executable)) { status.Text = "暂未取得其他前台应用，请切换到目标应用后再回来。"; return; }
            executable.Text = sample.Executable + (sample.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "" : ".exe");
            if (String.IsNullOrWhiteSpace(name.Text)) name.Text = sample.Executable;
        }
        private void CommitRule()
        {
            int order, seconds;
            if (!Int32.TryParse(priority.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out order) || !Int32.TryParse(cooldown.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) { status.Text = "优先级和冷却时间必须是整数。"; return; }
            var rule = new AutoSceneRule { Id = selectedId ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), Executable = executable.Text.Trim(), Start = start.Text.Trim(), End = end.Text.Trim(), Monitor = monitor.Text.Trim(), Power = power.SelectedIndex == 1 ? "ac" : power.SelectedIndex == 2 ? "battery" : "any", ProfileId = profile.SelectedValue as string ?? "", Enabled = ruleEnabled.IsChecked == true, Priority = order, CooldownSeconds = seconds };
            string error;
            if (!AutoScenePolicy.Validate(rule, out error) || !profiles().Any(p => p.Id == rule.ProfileId)) { status.Text = error ?? "目标布局已不存在，请重新选择。"; return; }
            int index = draft.Rules.FindIndex(r => r.Id == rule.Id);
            if (index < 0) { if (draft.Rules.Count >= 40) { status.Text = "最多保存 40 条自动规则。"; return; } draft.Rules.Add(rule); }
            else draft.Rules[index] = rule;
            selectedId = rule.Id; Rebuild(); status.Text = "规则已加入编辑列表，点击“保存全部规则”后生效。";
        }
        private void Edit(AutoSceneRule rule)
        {
            selectedId = rule.Id; name.Text = rule.Name; executable.Text = rule.Executable; start.Text = rule.Start; end.Text = rule.End; monitor.Text = rule.Monitor;
            ruleEnabled.IsChecked = rule.Enabled; power.SelectedIndex = rule.Power == "ac" ? 1 : rule.Power == "battery" ? 2 : 0;
            priority.Text = rule.Priority.ToString(CultureInfo.InvariantCulture); cooldown.Text = rule.CooldownSeconds.ToString(CultureInfo.InvariantCulture); RefreshProfiles(); profile.SelectedValue = rule.ProfileId;
        }
        private void Rebuild()
        {
            rows.Children.Clear();
            foreach (var rule in draft.Rules.OrderByDescending(r => r.Priority))
            {
                var local = rule; var body = new StackPanel();
                var label = IslandWindow.Text(rule.Name, 12, "#DDE9E2", true, false); label.TextWrapping = TextWrapping.Wrap; body.Children.Add(label);
                var target = profiles().FirstOrDefault(p => p.Id == rule.ProfileId);
                string targetName = target == null ? rule.ProfileId : LayoutProfileStore.IsBuiltin(target.Id) ? UiLocalization.Localize(target.Name) : target.Name;
                var description = ToolStyle.Hint((rule.Enabled ? "● " : "○ ") + (rule.Executable.Length == 0 ? "*" : rule.Executable) + "  →  " + targetName, false);
                body.Children.Add(description);
                body.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("编辑", () => Edit(local)), IslandWindow.Button("移除", () => { draft.Rules.Remove(local); if (selectedId == local.Id) ClearEditor(); Rebuild(); status.Text = "规则已移出编辑列表，保存后生效。"; })));
                rows.Children.Add(ToolStyle.Card(body));
            }
        }
        private void SaveAll()
        {
            try
            {
                draft.Enabled = enabled.IsChecked == true;
                store.Save(new AutoSceneRuleData { Enabled = draft.Enabled, Rules = draft.Rules.Select(r => r.Copy()).ToList() }); changed(); status.Text = "自动规则已保存。";
            }
            catch (Exception error) { if (!(error is System.IO.IOException || error is UnauthorizedAccessException || error is ArgumentException)) throw; status.Text = "自动规则未能保存，编辑内容仍保留。"; }
        }
        internal void SetRuntime(string text) { runtime.Text = text; }
        internal void RefreshLanguage() { if (lastLanguage == UiLocalization.Language) return; lastLanguage = UiLocalization.Language; Rebuild(); }
    }
}
