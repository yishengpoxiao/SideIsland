using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class LayoutProfilePanel : StackPanel
    {
        private readonly LayoutProfileStore store;
        private readonly Action<string> apply;
        private readonly Func<string, LayoutProfile> capture;
        private readonly StackPanel list = new StackPanel();
        private readonly TextBox name = ToolStyle.Input();
        private readonly TextBlock status = ToolStyle.Hint("");
        internal LayoutProfilePanel(LayoutProfileStore source, Action<string> applyProfile, Func<string, LayoutProfile> captureProfile, Action restore, Action<bool> autoRestore = null)
        {
            store = source; apply = applyProfile; capture = captureProfile;
            Children.Add(ToolStyle.Heading("场景布局")); Children.Add(ToolStyle.Hint("一键切换收起组件、常用工具和静默预览。可将布局关联到工作场景，在启动资源时一起应用。"));
            var automaticLabel = IslandWindow.Text("专注结束后自动恢复上个布局和通知预览", 11, "#DDE9E2"); automaticLabel.TextWrapping = TextWrapping.Wrap;
            var automatic = new CheckBox { IsChecked = store.Data.RestoreAfterFocus, Foreground = Display.Brush("#DDE9E2"), Margin = new Thickness(0, 6, 0, 6), Content = automaticLabel };
            automatic.Checked += (s, e) => { if (autoRestore != null) autoRestore(true); }; automatic.Unchecked += (s, e) => { if (autoRestore != null) autoRestore(false); }; Children.Add(automatic);
            var restoreRow = PanelActionLayout.Create(1, IslandWindow.Button("恢复上个布局", restore)); restoreRow.Margin = new Thickness(0, 0, 0, 6); Children.Add(restoreRow); Children.Add(list);
            Children.Add(ToolStyle.Hint("将当前布局保存为新场景")); name.MaxLength = 60; Children.Add(name);
            var saveRow = PanelActionLayout.Create(1, IslandWindow.Button("保存当前布局", SaveCurrent)); saveRow.Margin = new Thickness(0, 6, 0, 0); Children.Add(saveRow); Children.Add(status); Rebuild();
        }
        internal void Rebuild()
        {
            list.Children.Clear();
            foreach (var profile in store.All)
            {
                string id = profile.Id; var row = new StackPanel();
                var heading = new DockPanel();
                if (store.Data.ActiveId == id) { var active = IslandWindow.Text("●", 11, "#A1EACF", false, false); active.Margin = new Thickness(6, 0, 0, 0); active.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(active, Dock.Right); heading.Children.Add(active); }
                var title = IslandWindow.Text(profile.Name, 14, "#DDE9E2", true, LayoutProfileStore.IsBuiltin(id)); title.TextWrapping = TextWrapping.Wrap; heading.Children.Add(title); row.Children.Add(heading);
                var use = IslandWindow.Button("应用布局", () => apply(id));
                var actions = LayoutProfileStore.IsBuiltin(id) ? PanelActionLayout.Create(1, use) : PanelActionLayout.Create(2, use, IslandWindow.Button("删除", () => { int index = store.Data.Profiles.IndexOf(profile); store.Data.Profiles.Remove(profile); if (!TrySave()) store.Data.Profiles.Insert(index, profile); Rebuild(); })); actions.Margin = new Thickness(0, 6, 0, 0);
                row.Children.Add(actions); list.Children.Add(ToolStyle.Card(row));
            }
        }
        private void SaveCurrent()
        {
            string label = name.Text.Trim(); if (label.Length == 0 || store.Data.Profiles.Count >= 20) { status.Text = "请输入布局名称，最多保存 20 个自定义布局。"; return; }
            var profile = capture(label); store.Data.Profiles.Add(profile); if (TrySave()) { name.Clear(); Rebuild(); } else store.Data.Profiles.Remove(profile);
        }
        private bool TrySave() { try { store.Save(); status.Text = "布局已保存。"; return true; } catch (Exception) { status.Text = "布局暂时无法保存，请检查数据目录权限。"; return false; } }
        internal void ShowStatus(string text) { status.Text = text; Rebuild(); }
    }
}
