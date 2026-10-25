using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private TextBlock toolCommandHint;
        private bool toolCommandExecuting;
        private void BuildToolCommandHint(Panel panel)
        {
            toolCommandHint = ToolStyle.Hint(""); toolCommandHint.Visibility = Visibility.Collapsed; panel.Children.Add(toolCommandHint);
            UiLocalization.ToolTip(toolSearch, "输入名称或用途筛选 · Ctrl + K 搜索 · Enter 打开\n可直接输入：专注 45 分钟 / 查端口 8080 / 生成二维码 网址 / 复制模板 名称");
        }
        private bool UpdateToolCommandHint()
        {
            if (toolCommandHint == null) return false;
            var command = ToolCommandParser.Parse(toolSearch.Text); toolCommandHint.Visibility = command == null ? Visibility.Collapsed : Visibility.Visible;
            if (command != null) toolCommandHint.Text = command.Error ?? UiLocalization.Localize("按 Enter 执行：") + toolSearch.Text;
            return command != null;
        }
        private async void ExecuteToolSearch()
        {
            if (toolCommandExecuting || shuttingDown) return;
            var command = ToolCommandParser.Parse(toolSearch.Text);
            if (command == null) { string first = toolButtons.Where(item => item.Value.Visibility == Visibility.Visible).Select(item => item.Key).FirstOrDefault(); if (first != null) SelectTool(first); return; }
            if (command.Error != null) { toolCommandHint.Text = command.Error; return; }
            toolCommandExecuting = true;
            try
            {
                if (command.Kind == "focus") { OpenCompactTool("focus", true); focusPanel.StartFocus(command.Minutes); }
                else if (command.Kind == "ports") { OpenCompactTool("ports", true); portMonitorPanel.QueryPorts(command.Argument); }
                else if (command.Kind == "qr") { OpenCompactTool("qr", true); qrTransferPanel.AcceptExternalText(command.Argument); }
                else if (command.Kind == "template")
                {
                    await textTemplatePanel.PendingLoad; if (shuttingDown) return;
                    string rendered = textTemplatePanel.FindRendered(command.Argument); if (rendered == null) throw new ArgumentException("未找到唯一匹配的模板名称。");
                    if (!demo) Clipboard.SetText(rendered); OpenCompactTool("templates", true);
                }
                else if (command.Kind == "profile")
                {
                    var matches = layoutProfiles.All.Where(p => String.Equals(p.Id, command.Argument, StringComparison.OrdinalIgnoreCase) || String.Equals(p.Name, command.Argument, StringComparison.OrdinalIgnoreCase) || String.Equals(UiLocalization.Localize(p.Name), command.Argument, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1 || !ApplyLayoutProfile(matches[0].Id)) throw new ArgumentException("未找到唯一匹配的布局名称。");
                }
                else if (command.Kind == "scene") { if (!await workspaceScenePanel.LaunchNamedAsync(command.Argument)) throw new ArgumentException("未找到唯一匹配的工作场景，或正在启动。"); }
                toolSearch.Clear(); toolCommandHint.Visibility = Visibility.Collapsed; toolkitHint.Text = demo ? "演示命令已执行，不改变剪贴板或启动程序。" : "快捷命令已执行。";
            }
            catch (Exception error) { toolCommandHint.Visibility = Visibility.Visible; toolCommandHint.Text = error.Message; }
            finally { toolCommandExecuting = false; }
        }
    }
}
