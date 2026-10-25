using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private string pendingCompactAction;

        private string FindCompactAction(DependencyObject source)
        { return FindCompactActionAt(source, Mouse.GetPosition(this)); }

        private string FindCompactActionAt(DependencyObject source, Point position)
        {
            if (expanded || peeking) return null;
            string musicAction = FindCompactMusicAction(source, position);
            if (musicAction != null) return musicAction;
            while (source != null && !ReferenceEquals(source, this))
            {
                if (ReferenceEquals(source, compactMusicTarget) && ShowCompactMusic) return "media";
                if (ReferenceEquals(source, compactSlot1) && compactSlot1.IsVisible) return compactSlot1.Tag as string;
                if (ReferenceEquals(source, compactSlot2) && compactSlot2.IsVisible) return compactSlot2.Tag as string;
                if (ReferenceEquals(source, compactAlternate) && compactAlternate.IsVisible) return compactAlternate.Tag as string;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        private bool IsCompactActionPointerOver()
        {
            if (expanded || peeking) return false;
            return (compactMusicTarget != null && compactMusicTarget.IsVisible && compactMusicTarget.IsMouseOver)
                || (compactSlot1 != null && compactSlot1.IsVisible && compactSlot1.IsMouseOver)
                || (compactSlot2 != null && compactSlot2.IsVisible && compactSlot2.IsMouseOver)
                || (compactAlternate != null && compactAlternate.IsVisible && compactAlternate.IsMouseOver);
        }

        private void RecheckCompactActionHover()
        {
            if (!settings.HoverExpandEnabled) { hoverTimer.Stop(); return; }
            if (expanded || peeking || dragging || resizing || !IsMouseOver || hoverSuppressed || HasInteractionGuard()) return;
            if (IsCompactActionPointerOver()) hoverTimer.Stop();
            else if (!hoverTimer.IsEnabled) OnIslandMouseEnter();
        }

        private void RunCompactAction(string key)
        {
            if (shuttingDown || dragging || resizing) return;
            hoverTimer.Stop(); collapseTimer.Stop();
            if (key != null && key.StartsWith("music-", StringComparison.Ordinal)) { TryCompactMusicAction(key); return; }
            if (key == "focus" && focusPanel != null) { focusPanel.ToggleQuickFocus(); UpdateToolkitCompact(); }
            else if (key == "awake" && keepAwakePanel != null) { keepAwakePanel.ToggleQuick(); UpdateToolkitCompact(); }
            else if (key == "notifications") { SelectPage(true); RequestExpanded(true); }
            else if (key == "clock") OpenCompactTool("time");
            else if (key == "media") OpenCompactTool("media", true);
            else if (key == "ports") OpenCompactTool("ports", true);
            else if (key == "activities") OpenCompactTool("activities", true);
            else if (key == "cpu" || key == "memory" || key == "battery") OpenCompactTool("system");
            else { SelectPage(false); RequestExpanded(true); }
        }

        private void OpenCompactTool(string key, bool revealHidden = false)
        {
            if (revealHidden && !IsToolVisible(key))
            {
                settings.HiddenTools = String.Join(",", (settings.HiddenTools ?? "").Split(',').Where(item => item != key));
                SaveSettings(); ConfigureToolkit();
            }
            if (IsToolVisible(key)) { SelectToolsPage(); SelectTool(key); }
            else SelectPage(false);
            RequestExpanded(true);
        }

        private string CompactActionHint(string key)
        {
            string action;
            if (key == "focus" && focusPanel != null)
                action = UiLocalization.Format("点击：{0} {1}", UiLocalization.Localize(focusPanel.QuickActionText), UiLocalization.Localize(focusPanel.ActiveTimerLabel));
            else if (key == "awake" && keepAwakePanel != null)
                action = keepAwakePanel.IsRunning ? UiLocalization.Localize("点击停止保持唤醒") : UiLocalization.Format("点击保持唤醒 {0} 分钟", keepAwakePanel.QuickMinutes);
            else if (key.StartsWith("codex-", StringComparison.Ordinal)) action = UiLocalization.Localize("点击查看额度");
            else if (key == "notifications") action = UiLocalization.Localize("点击查看通知");
            else if (key == "clock") action = UiLocalization.Localize("点击打开日期与时区");
            else if (key == "media") action = UiLocalization.Localize("点击打开音乐与歌词");
            else if (key == "ports") action = UiLocalization.Localize("点击查看端口占用");
            else if (key == "activities") action = UiLocalization.Localize("点击查看任务动态");
            else action = UiLocalization.Localize("点击查看系统状态");
            return action + " · " + UiLocalization.Localize("按住拖动") + ((key == "focus" || key == "awake") ? "\n" + UiLocalization.Localize("右键打开组件设置") : "");
        }

        private void ConfigureCompactActionTarget(FrameworkElement target, string key, string description)
        {
            target.Cursor = Cursors.Hand; string hint = description + "\n" + CompactActionHint(key);
            if (!Object.Equals(target.ToolTip, hint)) target.ToolTip = hint;
            if (Object.Equals(target.Tag, key)) return;
            target.Tag = key;
            target.ContextMenu = null;
            if (key != "focus" && key != "awake") return;
            var menu = new ContextMenu(); UiTheme.Install(menu.Resources);
            var open = UiLocalization.Header(new MenuItem(), key == "focus" ? "设置专注时长…" : "设置保持唤醒…");
            open.Click += (s, e) => OpenCompactTool(key, true); menu.Items.Add(open);
            menu.Closed += (s, e) => QueueInteractionRecheck(); target.ContextMenu = menu;
        }
    }
}
