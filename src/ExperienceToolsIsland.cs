using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private ClipboardHistoryPanel clipboardHistoryPanel;
        private LogWatchPanel logWatchPanel;
        private LayoutProfileStore layoutProfiles;
        private LayoutProfilePanel layoutProfilePanel;
        private DiagnosticsPanel diagnosticsPanel;
        private TextTemplatePanel textTemplatePanel;
        private ServiceReadyPanel serviceReadyPanel;
        private SettingsBackupPanel settingsBackupPanel;
        private void BuildExperienceTools(Panel navigation)
        {
            clipboardHistoryPanel = new ClipboardHistoryPanel(demo, Path.GetDirectoryName(store.FilePath)); logWatchPanel = new LogWatchPanel(demo);
            AddTool(navigation, "clipboardhistory", clipboardHistoryPanel); AddTool(navigation, "logwatch", logWatchPanel);
            logWatchPanel.OnNotify += (title, body) => NotifyBackgroundTool("logwatch", title, body);
            BuildDesktopExperience(navigation);
            layoutProfiles = new LayoutProfileStore(demo);
            RestoreInterruptedTemporaryLayout();
            layoutProfilePanel = new LayoutProfilePanel(layoutProfiles, id => ApplyLayoutProfile(id), name => LayoutProfile.Capture(settings, name), RestoreLayoutProfile, SetFocusLayoutRestoration);
            focusPanel.FocusCompleted += RestoreCompletedFocusLayout;
            AddTool(navigation, "profiles", layoutProfilePanel);
            BuildAutomaticSceneAndWidgetTools(navigation);
            diagnosticsPanel = new DiagnosticsPanel(demo, () => "Visible: " + IsVisible + "; expanded: " + expanded + "; dock: " + settings.Side + "; screen present: " + (FormsScreenPresent()) + "; hover: " + settings.HoverExpandEnabled + "/" + settings.HoverDelayMilliseconds + " ms; fullscreen policy: " + desktopPreferences.FullscreenMode + "; avoiding: " + desktopAvoiding + "; bypassed: " + desktopFullscreenBypassed);
            AddTool(navigation, "diagnostics", diagnosticsPanel);
            workspaceScenePanel.ConfigureLayouts(() => layoutProfiles.All, ApplyLayoutProfile);
            BuildOcrDropTools(navigation);
            textTemplatePanel = new TextTemplatePanel(demo); AddTool(navigation, "templates", textTemplatePanel);
            serviceReadyPanel = new ServiceReadyPanel(demo); AddTool(navigation, "ready", serviceReadyPanel);
            serviceReadyPanel.OnNotify += (title, body) => NotifyBackgroundTool("ready", title, body);
            settingsBackupPanel = new SettingsBackupPanel(demo); AddTool(navigation, "backup", settingsBackupPanel);
        }
        private bool FormsScreenPresent()
        { foreach (var monitor in System.Windows.Forms.Screen.AllScreens) if (monitor.DeviceName == settings.ScreenName || String.IsNullOrEmpty(settings.ScreenName)) return true; return false; }
        private bool ApplyLayoutProfile(string id)
        {
            var profile = layoutProfiles.Find(id); if (profile == null) return false;
            NotifyManualSceneSelection();
            if (layoutProfiles.Data.ActiveId != id) layoutProfiles.Data.Previous = LayoutProfile.Capture(settings, "Previous");
            layoutProfiles.Data.ActiveId = id;
            profile.Apply(settings); selectedTool = settings.LastTool; SaveSettings();
            layoutProfiles.Data.FocusRestoreArmed = layoutProfiles.Data.RestoreAfterFocus && profile.Mode == "focus" && layoutProfiles.Data.Previous != null;
            string status = "已应用场景布局。";
            try { layoutProfiles.Save(); } catch (Exception) { status = "布局暂时无法保存，请检查数据目录权限。"; }
            ConfigureToolkit(); UpdateToolkitCompact(); layoutProfilePanel.ShowStatus(status); return true;
        }
        private void RestoreLayoutProfile()
        {
            NotifyManualSceneSelection();
            var previous = layoutProfiles.Data.Previous; if (previous == null) return;
            previous.Apply(settings); selectedTool = settings.LastTool; layoutProfiles.Data.ActiveId = ""; layoutProfiles.Data.Previous = null;
            layoutProfiles.Data.FocusRestoreArmed = false;
            string status = "已恢复上个布局。"; SaveSettings(); try { layoutProfiles.Save(); } catch (Exception) { status = "布局暂时无法保存，请检查数据目录权限。"; }
            ConfigureToolkit(); UpdateToolkitCompact(); layoutProfilePanel.ShowStatus(status);
        }
        private void SetFocusLayoutRestoration(bool enabled)
        {
            layoutProfiles.Data.RestoreAfterFocus = enabled;
            var active = layoutProfiles.Find(layoutProfiles.Data.ActiveId);
            layoutProfiles.Data.FocusRestoreArmed = enabled && active != null && active.Mode == "focus" && layoutProfiles.Data.Previous != null && TemporaryLayoutPolicy.Matches(active, settings);
            try { layoutProfiles.Save(); } catch (Exception) { layoutProfilePanel.ShowStatus("布局暂时无法保存，请检查数据目录权限。"); }
        }
        private bool CanRestoreTemporaryLayout()
        { return layoutProfiles != null && layoutProfiles.Data.FocusRestoreArmed && layoutProfiles.Data.Previous != null && TemporaryLayoutPolicy.Matches(layoutProfiles.Find(layoutProfiles.Data.ActiveId), settings); }
        private void RestoreCompletedFocusLayout()
        {
            if (CanRestoreTemporaryLayout()) RestoreLayoutProfile();
            else if (layoutProfiles != null && layoutProfiles.Data.FocusRestoreArmed) { layoutProfiles.Data.FocusRestoreArmed = false; try { layoutProfiles.Save(); } catch (Exception) { } }
        }
        private void RestoreInterruptedTemporaryLayout()
        {
            if (layoutProfiles == null || !layoutProfiles.Data.FocusRestoreArmed) return;
            if (CanRestoreTemporaryLayout())
            {
                layoutProfiles.Data.Previous.Apply(settings); layoutProfiles.Data.Previous = null; layoutProfiles.Data.ActiveId = ""; layoutProfiles.Data.FocusRestoreArmed = false;
                if (!demo) try { store.Save(settings); } catch (Exception error) { StartupDiagnostics.Failure(error); }
            }
            else layoutProfiles.Data.FocusRestoreArmed = false;
            try { layoutProfiles.Save(); } catch (Exception) { }
        }
        private void StartExperienceTools() { clipboardHistoryPanel.Start(); logWatchPanel.Start(); serviceReadyPanel.Start(); StartDesktopExperience(); StartRuntimePerformance(); StartAutomaticSceneRules(); }
        private void StopExperienceTools()
        { StopAutomaticSceneRules(); StopRuntimePerformance(); StopOcrDropTools(); if (textTemplatePanel != null) textTemplatePanel.Dispose(); if (serviceReadyPanel != null) serviceReadyPanel.Dispose(); if (clipboardHistoryPanel != null) clipboardHistoryPanel.Dispose(); if (logWatchPanel != null) logWatchPanel.Dispose(); StopDesktopExperience(); }
    }
}
