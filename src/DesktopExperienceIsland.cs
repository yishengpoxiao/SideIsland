using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        private DesktopExperiencePreferences desktopPreferences;
        private DesktopExperienceStore desktopPreferencesStore;
        private DesktopExperiencePanel desktopExperiencePanel;
        private readonly DispatcherTimer desktopExperienceTimer = new DispatcherTimer();
        private Window desktopSlimWindow;
        private bool desktopAvoiding, desktopFullscreenBypassed, desktopWasHitTestVisible, desktopWasTransparent, desktopStarted, desktopVolumeBusy, desktopMuteBusy, desktopMiddleCanceled;
        private string desktopDisconnectedMonitor;
        private long desktopOriginalStyle;
        private int desktopPendingVolume;
        private Point? desktopMiddlePress;
        private DateTime desktopLastWheelAction;
        private string desktopHotkeyStatus = "";
        private string desktopAvoidanceMode = "";
        internal Func<FullscreenSample> DesktopFullscreenReader = null;

        private void BuildDesktopExperience(Panel navigation)
        {
            desktopPreferencesStore = new DesktopExperienceStore(); desktopPreferences = demo ? new DesktopExperiencePreferences() : desktopPreferencesStore.Load();
            desktopExperiencePanel = new DesktopExperiencePanel(desktopPreferences); AddTool(navigation, "desktop", desktopExperiencePanel);
            desktopExperiencePanel.ApplyRequested = ApplyDesktopExperience;
            desktopExperiencePanel.MoveMonitorRequested = MoveToExperienceMonitor;
            WireExperienceGestures();
            desktopExperienceTimer.Interval = TimeSpan.FromMilliseconds(450);
            desktopExperienceTimer.Tick += (s, e) => UpdateFullscreenAvoidance();
        }
        private void StartDesktopExperience()
        {
            if (desktopStarted || desktopPreferences == null) return; desktopStarted = true;
            desktopPreferences.Remember(CurrentExperienceMonitor(), settings);
            if (!demo)
            {
                hotkeys = new HotkeyRegistration(this); string error;
                if (!RegisterDesktopHotkeys(desktopPreferences, out error)) { desktopHotkeyStatus = error; desktopExperiencePanel.SetStatus(error); }
                desktopExperienceTimer.Start();
            }
            ConfigureDesktopExperience();
        }
        private void ConfigureDesktopExperience()
        {
            if (desktopPreferences == null) return;
            if (toolkitHint != null)
                toolkitHint.Text = desktopHotkeyStatus.Length > 0 ? UiLocalization.Localize(desktopHotkeyStatus) : UiLocalization.Format("展开／收起：{0} · 隐私：{1}", String.IsNullOrWhiteSpace(desktopPreferences.ToggleHotkey) ? UiLocalization.Localize("已禁用") : desktopPreferences.ToggleHotkey, String.IsNullOrWhiteSpace(desktopPreferences.PrivacyHotkey) ? UiLocalization.Localize("已禁用") : desktopPreferences.PrivacyHotkey);
            if (desktopAvoiding) { Opacity = 0; IsHitTestVisible = false; }
            if (desktopSlimWindow != null) { desktopSlimWindow.Opacity = settings.OpacityPercent / 100.0; PositionDesktopSlim(); }
        }
        private void StopDesktopExperience()
        {
            desktopExperienceTimer.Stop(); desktopMiddlePress = null;
            if (!demo && desktopPreferences != null)
            { desktopPreferences.Remember(CurrentExperienceMonitor(), settings); desktopPreferencesStore.TrySave(desktopPreferences); }
            if (desktopSlimWindow != null) { desktopSlimWindow.Close(); desktopSlimWindow = null; }
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
        }
        private string ApplyDesktopExperience(DesktopExperiencePreferences next)
        {
            Dictionary<string, HotkeyChord> bindings; string error;
            if (!HotkeyChord.TryRead(next, out bindings, out error)) return error;
            // Keep monitor anchors recorded since the panel was opened.
            next.Monitors = desktopPreferences.Monitors.Select(anchor => new MonitorAnchor { Device = anchor.Device, X = anchor.X, Y = anchor.Y, Side = anchor.Side, Free = anchor.Free }).ToList();
            var previous = desktopPreferences;
            if (!demo && hotkeys != null && !RegisterDesktopHotkeys(next, out error))
            { if (error.IndexOf("恢复失败", StringComparison.Ordinal) >= 0) { desktopHotkeyStatus = error; ConfigureDesktopExperience(); } return error; }
            if (!demo && !desktopPreferencesStore.TrySave(next))
            {
                if (hotkeys != null) { string rollbackError; if (!RegisterDesktopHotkeys(previous, out rollbackError)) { desktopHotkeyStatus = "桌面设置未能保存，快捷键也未能恢复；请重新设置快捷键。"; ConfigureDesktopExperience(); return desktopHotkeyStatus; } }
                return "桌面设置未能保存，已保留原设置。";
            }
            desktopPreferences = next; desktopHotkeyStatus = ""; ConfigureDesktopExperience(); RestoreDesktopAvoidance(); desktopFullscreenBypassed = false; SyncNowPlayingActivity(); UpdateFullscreenAvoidance(); return null;
        }
        private bool RegisterDesktopHotkeys(DesktopExperiencePreferences value, out string error)
        {
            Dictionary<string, HotkeyChord> bindings; if (!HotkeyChord.TryRead(value, out bindings, out error)) return false;
            if (hotkeys == null) { error = ""; return true; }
            return hotkeys.Replace(bindings, action => {
                RestoreDesktopExperienceForReveal();
                if (action == "toggle") Reveal(!expanded);
                else if (action == "privacy") { settings.HideNotificationBody = !settings.HideNotificationBody; SaveSettings(); ConfigureNotifications(); }
                else RunExperienceAction(action, 0);
            }, out error);
        }

        private void UpdateFullscreenAvoidance()
        {
            if ((demo && DesktopFullscreenReader == null) || shuttingDown || desktopPreferences == null) return;
            FullscreenSample sample = DesktopFullscreenReader == null ? DesktopExperienceNative.ReadForeground() : DesktopFullscreenReader();
            // Restoring or clicking our strip may temporarily foreground this
            // process. That is not evidence that the fullscreen app has ended.
            if (sample != null && sample.OwnProcess && desktopFullscreenBypassed) return;
            bool fullscreen = FullscreenPolicy.IsFullscreen(sample, CurrentExperienceMonitor());
            if (!fullscreen) { desktopFullscreenBypassed = false; RestoreDesktopAvoidance(); return; }
            string mode = FullscreenPolicy.ModeFor(sample, desktopPreferences);
            if (mode == "off" || desktopFullscreenBypassed || !IsVisible || dragging || resizing || HasInteractionGuard())
            { if (desktopAvoiding) RestoreDesktopAvoidance(); return; }
            if (desktopAvoiding && desktopAvoidanceMode == mode) return;
            if (desktopAvoiding) RestoreDesktopAvoidance();
            hoverTimer.Stop(); collapseTimer.Stop(); StopVisualTransition(true);
            desktopWasHitTestVisible = IsHitTestVisible;
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                desktopOriginalStyle = DesktopExperienceNative.GetExtendedStyle(source.Handle); desktopWasTransparent = (desktopOriginalStyle & 0x20L) != 0;
                DesktopExperienceNative.SetExtendedStyle(source.Handle, desktopOriginalStyle | 0x20L);
            }
            desktopAvoiding = true; desktopAvoidanceMode = mode; IsHitTestVisible = false; Opacity = 0;
            if (mode == "slim") ShowDesktopSlim();
        }
        private void RestoreDesktopAvoidance()
        {
            if (desktopSlimWindow != null) desktopSlimWindow.Hide();
            if (!desktopAvoiding) return; desktopAvoiding = false; desktopAvoidanceMode = "";
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null && !source.IsDisposed)
            {
                long style = DesktopExperienceNative.GetExtendedStyle(source.Handle);
                DesktopExperienceNative.SetExtendedStyle(source.Handle, desktopWasTransparent ? style | 0x20L : style & ~0x20L);
            }
            Opacity = settings.OpacityPercent / 100.0; IsHitTestVisible = desktopWasHitTestVisible;
            // Visibility is never changed here: a user-hidden window stays hidden.
        }
        private void RestoreDesktopExperienceForReveal()
        { if (desktopPreferences == null) return; desktopFullscreenBypassed = true; RestoreDesktopAvoidance(); }
        private void ShowDesktopSlim()
        {
            if (desktopSlimWindow == null)
            {
                var strip = UiLocalization.ToolTip(new Border { CornerRadius = new CornerRadius(5), Background = UiTheme.Brush("#A1EACF"), BorderBrush = UiTheme.Brush("#007AFF"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, SnapsToDevicePixels = true }, "点击恢复灵动岛");
                desktopSlimWindow = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Content = strip, UseLayoutRounding = true, SnapsToDevicePixels = true };
                AppAppearance.Register(desktopSlimWindow);
                strip.MouseLeftButtonUp += (s, e) => { RestoreDesktopExperienceForReveal(); Reveal(false); e.Handled = true; };
            }
            PositionDesktopSlim(); desktopSlimWindow.Opacity = settings.OpacityPercent / 100.0; desktopSlimWindow.Show();
        }
        private void PositionDesktopSlim()
        {
            if (desktopSlimWindow == null) return; Rect area = WorkArea(); bool horizontal = settings.Side == "top" || settings.Side == "bottom";
            desktopSlimWindow.Width = horizontal ? 76 : 9; desktopSlimWindow.Height = horizontal ? 9 : 76;
            desktopSlimWindow.Left = horizontal ? area.Left + (area.Width - 76) * settings.HorizontalPosition : settings.Side == "left" ? area.Left + 1 : area.Right - 10;
            desktopSlimWindow.Top = horizontal ? settings.Side == "top" ? area.Top + 1 : area.Bottom - 10 : area.Top + (area.Height - 76) * settings.VerticalPosition;
        }

        private string CurrentExperienceMonitor()
        {
            return (Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == settings.ScreenName) ?? Forms.Screen.PrimaryScreen).DeviceName;
        }
        private void RememberExperienceMonitorAnchor()
        {
            if (desktopPreferences == null || !desktopPreferences.RememberMonitors) return;
            desktopPreferences.Remember(CurrentExperienceMonitor(), settings);
            // Crossing a monitor or reaching the initial drag threshold must
            // never wait for a durable disk flush. Release commits all anchors.
            if (dragging || resizing) return;
            if (!demo && !desktopPreferencesStore.TrySave(desktopPreferences) && desktopExperiencePanel != null) desktopExperiencePanel.SetStatus("显示器位置未能保存，请检查数据目录权限。");
        }
        private void ChangeExperienceDragMonitor(string device)
        {
            if (String.Equals(settings.ScreenName, device, StringComparison.OrdinalIgnoreCase)) return;
            RememberExperienceMonitorAnchor(); settings.ScreenName = device; desktopDisconnectedMonitor = null;
        }
        private void MoveToExperienceMonitor(string device)
        {
            if (dragging || resizing || !Forms.Screen.AllScreens.Any(screen => screen.DeviceName == device)) return;
            RememberExperienceMonitorAnchor(); RestoreDesktopExperienceForReveal(); StopVisualTransition(true);
            settings.ScreenName = device; desktopDisconnectedMonitor = null; desktopPreferences.Restore(device, settings);
            SetExpanded(expanded, false); Dock(); SaveSettings(); RememberExperienceMonitorAnchor();
        }
        private void OnDesktopDisplaySettingsChanged()
        {
            if (desktopPreferences == null || shuttingDown) return;
            RestoreDesktopAvoidance();
            if (!String.IsNullOrEmpty(desktopDisconnectedMonitor) && Forms.Screen.AllScreens.Any(screen => screen.DeviceName == desktopDisconnectedMonitor))
            { settings.ScreenName = desktopDisconnectedMonitor; desktopDisconnectedMonitor = null; desktopPreferences.Restore(settings.ScreenName, settings); }
            else if (!String.IsNullOrEmpty(settings.ScreenName) && !Forms.Screen.AllScreens.Any(screen => screen.DeviceName == settings.ScreenName))
            {
                // The departed display no longer resolves via Screen.From*.
                // Save its named anchor before loading the fallback display.
                desktopPreferences.Remember(settings.ScreenName, settings);
                desktopDisconnectedMonitor = settings.ScreenName; settings.ScreenName = Forms.Screen.PrimaryScreen.DeviceName; desktopPreferences.Restore(settings.ScreenName, settings);
            }
            desktopExperiencePanel.RefreshMonitors(); UpdateCompactLayout(); SetExpanded(expanded, false); SaveSettings();
            if (!demo) desktopPreferencesStore.TrySave(desktopPreferences);
        }

        private void WireExperienceGestures()
        {
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && TryCancelExperienceDrag()) e.Handled = true; };
            PreviewMouseDown += (s, e) => {
                if (e.ChangedButton != MouseButton.Middle || expanded || peeking || dragging || resizing || desktopPreferences == null || desktopPreferences.MiddleAction == "none") return;
                hoverTimer.Stop(); collapseTimer.Stop(); desktopMiddlePress = e.GetPosition(this); desktopMiddleCanceled = false;
                if (!CaptureMouse()) desktopMiddlePress = null; e.Handled = true;
            };
            PreviewMouseUp += (s, e) => {
                if (e.ChangedButton != MouseButton.Middle || !desktopMiddlePress.HasValue) return;
                bool click = FinishExperienceMiddleGesture(e.GetPosition(this));
                if (Mouse.Captured == this) ReleaseMouseCapture();
                if (click && !expanded && !peeking) RunExperienceAction(desktopPreferences.MiddleAction, 0);
                e.Handled = true;
            };
            PreviewMouseMove += (s, e) => {
                TrackExperienceMiddleGesture(e.GetPosition(this));
            };
            LostMouseCapture += (s, e) => { if (ReferenceEquals(e.OriginalSource, this)) { desktopMiddlePress = null; desktopMiddleCanceled = false; } };
            PreviewMouseWheel += (s, e) => {
                if (expanded || peeking || dragging || resizing || desktopPreferences == null) return;
                string action = desktopPreferences.WheelAction;
                if (action == "default") { if (TryCompactMusicWheel(e.OriginalSource as DependencyObject, e.Delta)) e.Handled = true; return; }
                if (action == "none") return;
                if (action != "volume" && (DateTime.UtcNow - desktopLastWheelAction).TotalMilliseconds < 180) { e.Handled = true; return; }
                desktopLastWheelAction = DateTime.UtcNow; RunExperienceAction(action, e.Delta); e.Handled = true;
            };
        }
        private void TrackExperienceMiddleGesture(Point point)
        {
            if (!desktopMiddlePress.HasValue) return;
            Vector movement = point - desktopMiddlePress.Value;
            if (Math.Abs(movement.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(movement.Y) >= SystemParameters.MinimumVerticalDragDistance) desktopMiddleCanceled = true;
        }
        private bool FinishExperienceMiddleGesture(Point point)
        {
            if (!desktopMiddlePress.HasValue) return false;
            TrackExperienceMiddleGesture(point); bool click = !desktopMiddleCanceled;
            desktopMiddlePress = null; desktopMiddleCanceled = false; return click;
        }
        private void RunExperienceCompactClick(string pendingAction)
        {
            if (expanded || peeking) { RequestExpanded(!expanded); return; }
            string configured = desktopPreferences == null ? "default" : desktopPreferences.ClickAction;
            if (configured != "default") { RunExperienceAction(configured, 0); return; }
            if (pendingAction != null) RunCompactAction(pendingAction); else RequestExpanded(!expanded);
        }
        private async void RunExperienceAction(string action, int wheelDelta)
        {
            if (shuttingDown || dragging || resizing || action == "none" || action == "default") return;
            hoverTimer.Stop(); collapseTimer.Stop();
            if (action == "expand") RequestExpanded(!expanded);
            else if (action == "focus") { if (focusPanel != null) { focusPanel.ToggleQuickFocus(); UpdateToolkitCompact(); } }
            else if (action == "search") { Reveal(true); OpenToolSearch(); }
            else if (action == "launcher") OpenCompactTool("launch", true);
            else if (action == "screenpin") { if (screenPinPanel != null && !demo) await screenPinPanel.BeginCaptureAsync(); }
            else if (action.StartsWith("media-", StringComparison.Ordinal)) { if (!demo) TryCompactMusicAction("music-" + action.Substring(6)); }
            else if (action == "volume")
            {
                if (wheelDelta == 0) { OpenCompactTool("media", true); return; }
                if (demo) return; desktopPendingVolume += wheelDelta; if (desktopVolumeBusy) return;
                desktopVolumeBusy = true;
                try
                {
                    while (!shuttingDown && desktopPendingVolume != 0)
                    {
                        int delta = desktopPendingVolume; desktopPendingVolume = 0;
                        await Task.Run(() => { var audio = AudioService.Read(); if (audio.OutputAvailable) AudioService.SetVolume(audio.Volume + delta / 120.0 * 2); });
                    }
                }
                finally { desktopVolumeBusy = false; }
            }
            else if (action == "mute-output" || action == "mute-mic")
            {
                if (demo || desktopMuteBusy) return; desktopMuteBusy = true;
                try { bool success = await Task.Run(() => AudioService.ToggleMute(action == "mute-mic")); if (!shuttingDown && desktopExperiencePanel != null) desktopExperiencePanel.SetStatus(success ? "音频静音状态已切换。" : "音频设备暂不可用。"); }
                finally { desktopMuteBusy = false; }
            }
        }
    }
}
