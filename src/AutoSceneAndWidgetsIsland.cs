using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private AutoSceneRuleStore autoSceneRules;
        private AutoSceneRulesPanel autoSceneRulesPanel;
        private readonly AutoSceneRuleEngine autoSceneEngine = new AutoSceneRuleEngine();
        private readonly DispatcherTimer autoSceneTimer = new DispatcherTimer();
        private bool applyingAutomaticScene;
        private AutoSceneContext lastExternalSceneContext;
        private WidgetLayoutStore widgetLayouts;
        private WidgetLayoutEditorPanel widgetLayoutEditor;
        private DateTime widgetRotationOrigin = DateTime.UtcNow, widgetNotificationUntil, widgetFocusUntil;
        private string widgetResolvedProfile = "";
        private int widgetPreviousUnread;
        private void BuildAutomaticSceneAndWidgetTools(Panel navigation)
        {
            autoSceneRules = new AutoSceneRuleStore(demo); widgetLayouts = new WidgetLayoutStore(demo);
            autoSceneRulesPanel = new AutoSceneRulesPanel(autoSceneRules, () => layoutProfiles.All, () => lastExternalSceneContext,
                () => { autoSceneEngine.Reset(); UpdateAutomaticSceneRules(); },
                () => { autoSceneEngine.Pause(DateTime.UtcNow, TimeSpan.FromMinutes(30)); UpdateAutomaticSceneRules(); },
                () => { autoSceneEngine.Resume(); UpdateAutomaticSceneRules(); });
            widgetLayoutEditor = new WidgetLayoutEditorPanel(widgetLayouts, () => layoutProfiles.All, () => settings, () => layoutProfiles.Data.ActiveId, () => {
                widgetRotationOrigin = DateTime.UtcNow; UpdateToolkitCompact(); SyncNowPlayingActivity();
                if (portMonitorPanel != null) portMonitorPanel.SetCompactMonitoring(WidgetPoolContains("ports") || (settings.CompactMode == "quota" && (settings.CompactSlot1 == "ports" || settings.CompactSlot2 == "ports")));
            });
            AddTool(navigation, "autorules", autoSceneRulesPanel); AddTool(navigation, "widgets", widgetLayoutEditor);
            focusPanel.FocusCompleted += () => { widgetFocusUntil = DateTime.UtcNow.AddSeconds(8); UpdateToolkitCompact(); };
            autoSceneTimer.Interval = TimeSpan.FromSeconds(1); autoSceneTimer.Tick += (s, e) => UpdateAutomaticSceneRules();
        }
        private void StartAutomaticSceneRules() { if (!demo) autoSceneTimer.Start(); }
        private void StopAutomaticSceneRules() { autoSceneTimer.Stop(); }
        private void NotifyManualSceneSelection()
        { if (!applyingAutomaticScene && autoSceneRules != null) autoSceneEngine.Pause(DateTime.UtcNow, TimeSpan.FromMinutes(10)); }
        private void UpdateAutomaticSceneRules()
        {
            if (autoSceneRulesPanel == null || shuttingDown || runtimeSuspended) return;
            if (widgetLayoutEditor != null) widgetLayoutEditor.RefreshLanguage();
            bool editing = expanded && toolsPageSelected && selectedTool == "autorules" && autoSceneRulesPanel.IsVisible;
            if (!autoSceneRules.Data.Enabled && !editing) { autoSceneRulesPanel.SetRuntime("自动规则已关闭。"); return; }
            AutoSceneContext sample = AutoSceneContextReader.Read();
            if (!sample.OwnProcess && sample.Executable.Length > 0) lastExternalSceneContext = sample;
            if (!autoSceneRules.Data.Enabled) { autoSceneRulesPanel.SetRuntime("自动规则已关闭。"); return; }
            DateTime utc = DateTime.UtcNow;
            if (utc < autoSceneEngine.PausedUntil) { autoSceneRulesPanel.SetRuntime(UiLocalization.Format("自动切换暂停至 {0}", autoSceneEngine.PausedUntil.ToLocalTime().ToString("HH:mm", UiLocalization.Culture))); return; }
            if (dialogOpen || dragging || resizing) return;
            var rule = autoSceneEngine.Evaluate(autoSceneRules.Data, sample, utc, id => layoutProfiles.Find(id) != null);
            if (rule == null) { autoSceneRulesPanel.SetRuntime("自动规则监测中；应用稳定后再切换。"); return; }
            try
            {
                applyingAutomaticScene = true;
                if (ApplyLayoutProfile(rule.ProfileId)) { autoSceneEngine.MarkApplied(rule, utc); autoSceneRulesPanel.SetRuntime(UiLocalization.Format("已自动应用规则：{0}", rule.Name)); }
            }
            catch (Exception error)
            {
                if (!(error is System.IO.IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is ArgumentException)) throw;
                autoSceneEngine.Pause(utc, TimeSpan.FromSeconds(30)); autoSceneRulesPanel.SetRuntime("自动布局暂时无法应用，已暂停 30 秒。");
            }
            finally { applyingAutomaticScene = false; }
        }
        private WidgetLayoutDefinition ActiveWidgetLayout
        {
            get
            {
                if (widgetLayouts == null || layoutProfiles == null) return null;
                var layout = widgetLayouts.Resolve(layoutProfiles.Data.ActiveId);
                return layout != null && layout.Enabled && WidgetLayoutPolicy.Valid(layout) ? layout : null;
            }
        }
        private bool WidgetPoolContains(string key)
        { var layout = ActiveWidgetLayout; return layout != null && layout.Cards.Any(c => c.Key == key); }
        private int CurrentWidgetTransientPriority
        {
            get
            {
                var layout = ActiveWidgetLayout; if (layout == null) return -1; DateTime utc = DateTime.UtcNow;
                int notification = layout.NotificationOverride && notificationFeed.UnreadCount > 0 && utc < widgetNotificationUntil ? layout.NotificationPriority : -1;
                int focus = layout.FocusOverride && utc < widgetFocusUntil ? layout.FocusPriority : -1;
                return Math.Max(notification, focus);
            }
        }
        private WidgetCardDefinition[] ResolveWidgetCards(DateTime utc)
        {
            var layout = ActiveWidgetLayout; if (layout == null) return null;
            string id = layoutProfiles.Data.ActiveId ?? "";
            if (id != widgetResolvedProfile) { widgetResolvedProfile = id; widgetRotationOrigin = utc; }
            if (notificationFeed.UnreadCount > widgetPreviousUnread) widgetNotificationUntil = utc.AddSeconds(8);
            widgetPreviousUnread = notificationFeed.UnreadCount;
            var visible = WidgetLayoutPolicy.Visible(layout, utc, widgetRotationOrigin);
            bool notification = layout.NotificationOverride && notificationFeed.UnreadCount > 0 && utc < widgetNotificationUntil;
            bool focus = layout.FocusOverride && utc < widgetFocusUntil;
            string replacement = notification && (!focus || layout.NotificationPriority >= layout.FocusPriority) ? "notifications" : focus ? "focus" : null;
            if (replacement != null && visible[0].Key != replacement)
                visible[1] = new WidgetCardDefinition { Key = replacement, Size = visible[1].Size };
            return visible;
        }
        private void SetWidgetCardSize(Grid slot, CompactWidgetVisual visual, WidgetCardDefinition card)
        {
            double scale = card == null ? 1 : card.Scale;
            slot.Width = 54 * scale; slot.Height = 67 * scale;
            var current = visual.LayoutTransform as ScaleTransform;
            if (scale == 1) { if (!ReferenceEquals(visual.LayoutTransform, Transform.Identity)) visual.LayoutTransform = Transform.Identity; }
            else if (current == null || Math.Abs(current.ScaleX - scale) > .001) visual.LayoutTransform = new ScaleTransform(scale, scale);
        }
        private void RefreshSceneAutomationAppearance()
        { if (widgetLayoutEditor != null) widgetLayoutEditor.RefreshLanguage(); if (autoSceneRulesPanel != null) autoSceneRulesPanel.RefreshLanguage(); }
    }
}
