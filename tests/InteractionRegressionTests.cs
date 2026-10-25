using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Exercises only a demo window and isolated settings. No global input, cursor
    // movement, account reads, real notifications or real clipboard changes.
    internal static class InteractionRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct DiagnosticPoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out DiagnosticPoint point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(DiagnosticPoint point);

        public static void Run()
        {
            string previousData = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string testData = Path.Combine(Path.GetTempPath(), "CodexIsland.InteractionTests." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", testData);
                new SettingsStore().Save(new Settings {
                    AlwaysOnTop = false, AutoCollapse = true, NotificationsEnabled = false,
                    CustomExpandedSize = true, ExpandedWidth = 388, ExpandedHeight = 520
                });
                window = new IslandWindow(true) { ShowActivated = false };
                window.Loaded += (s, e) => StopTimers(window);
                window.Show(); Pump(1); StopTimers(window);
                RapidTransitions(window);
                ClickResponseAndAnimationTiming(window);
                ToolSelection(window);
                PopupAndCaptureGuards(window);
                NotificationPreviews(window);
                ActiveEditorGuard(window);
                Console.WriteLine("PASS: interrupted four-edge transitions, tool search/filtering, popup/capture protection and private notification preview routing.");
            }
            finally
            {
                if (window != null)
                {
                    var captured = Mouse.Captured as DependencyObject;
                    if (captured != null && Window.GetWindow(captured) == window) Mouse.Capture(null);
                    window.Close();
                }
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previousData);
                if (Directory.Exists(testData)) Directory.Delete(testData, true);
            }
        }

        private static void RapidTransitions(IslandWindow window)
        {
            var settings = (Settings)Read(window, "settings");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                settings.Side = side; settings.FreePosition = false;
                settings.HorizontalPosition = .4; settings.VerticalPosition = .3;
                window.SetExpanded(false, false); window.UpdateLayout();
                Rect compact = Bounds(window);
                window.SetExpanded(true, true); Pump(25);
                window.SetExpanded(false, true); Pump(25);
                window.SetExpanded(true, false); window.UpdateLayout();
                Rect expanded = Bounds(window);
                Check((bool)Read(window, "expanded") && ((FrameworkElement)Read(window, "detailsScroll")).IsVisible,
                    side + " immediate expand wins over old collapse");
                Pump(IslandWindow.TransitionMilliseconds + 40); window.UpdateLayout();
                Check(SameBounds(expanded, Bounds(window)), side + " canceled animation cannot override explicit expansion");
                window.SetExpanded(false, true); Pump(20);
                window.SetExpanded(true, true); Pump(20);
                window.SetExpanded(false, false); window.UpdateLayout();
                Check(SameBounds(compact, Bounds(window)), side + " fast toggle preserves compact anchor and orientation");
                Pump(IslandWindow.TransitionMilliseconds + 40); window.UpdateLayout();
                Check(!(bool)Read(window, "expanded") && SameBounds(compact, Bounds(window)),
                    side + " canceled animation cannot reopen explicit collapse");
                Check(Near((double)window.GetAnimationBaseValue(Window.WidthProperty), window.Width) &&
                    Near((double)window.GetAnimationBaseValue(Window.HeightProperty), window.Height),
                    side + " settled window dimensions do not retain a stale animation base");
                StopTimers(window);
            }
        }

        private static void ToolSelection(IslandWindow window)
        {
            var settings = (Settings)Read(window, "settings");
            settings.HiddenTools = "";
            Call(window, "ConfigureToolkit"); Call(window, "SelectToolsPage"); window.SetExpanded(true, false);
            var panels = (Dictionary<string, UIElement>)Read(window, "toolPanels");
            var buttons = (Dictionary<string, Button>)Read(window, "toolButtons");
            var search = (TextBox)Read(window, "toolSearch");
            var picker = (Expander)Read(window, "toolPicker");
            string[] established = { "media", "focus", "notes", "launch", "clipboard", "calculator", "system", "reminders", "files", "converter", "text", "colors" };
            Check(established.All(panels.ContainsKey), "all established modules remain selectable after a toolkit upgrade");
            Check(panels.Keys.All(buttons.ContainsKey) && panels.Count == buttons.Count, "every installed module has exactly one picker entry");
            foreach (string key in panels.Keys)
            {
                picker.IsExpanded = true; Call(window, "SelectTool", key);
                Check(panels.Count(entry => entry.Value.Visibility == Visibility.Visible) == 1 && panels[key].Visibility == Visibility.Visible,
                    key + " is the sole visible module after selection");
                Check(!picker.IsExpanded, key + " selection closes the tool picker");
            }
            search.Text = "CONVERTER";
            Check(buttons["converter"].Visibility == Visibility.Visible && buttons.Count(entry => entry.Value.Visibility == Visibility.Visible) == 1,
                "case-insensitive module search");
            search.Text = "不存在的功能";
            Check(buttons.All(entry => entry.Value.Visibility == Visibility.Collapsed) && ((UIElement)Read(window, "noToolMatches")).Visibility == Visibility.Visible,
                "empty module search has an explicit result");
            search.Text = ""; settings.HiddenTools = "converter,colors";
            Call(window, "ConfigureToolkit");
            Check(buttons["converter"].Visibility == Visibility.Collapsed && buttons["colors"].Visibility == Visibility.Collapsed,
                "hidden modules do not leak into the picker");
            settings.HiddenTools = ""; Call(window, "ConfigureToolkit");
            StopTimers(window);
        }

        private static void ClickResponseAndAnimationTiming(IslandWindow window)
        {
            var settings = (Settings)Read(window, "settings");
            var shell = (Border)Read(window, "shell");
            bool automatic = settings.AutoCollapse; settings.AutoCollapse = false;
            var opens = new List<double>(); var closes = new List<double>();
            try
            {
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                {
                    settings.Side = side; window.SetExpanded(false, false); window.UpdateLayout();
                    InitializeMouseSource(window); StopTimers(window);
                    // Raising a mouse-down locally does not change the physical
                    // button state. Ignore only capture's synthetic hover refresh;
                    // the production down/up handlers still perform the click.
                    MouseEventHandler syntheticMove = (s, e) => { if (Mouse.LeftButton != MouseButtonState.Pressed) e.Handled = true; };
                    shell.PreviewMouseMove += syntheticMove;
                    try
                    {
                        opens.Add(AnimatedUserAction(window, () => {
                            shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                                RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = shell
                            });
                            Check(ReferenceEquals(Mouse.Captured, shell) && (bool)Read(window, "dragging"), side + " compact click captures its shell");
                            shell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                                RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = shell
                            });
                        }, true, side + " compact mouse click"));
                    }
                    finally { shell.PreviewMouseMove -= syntheticMove; }
                    var collapse = Descendants(window).OfType<Button>().Single(button => (string)button.ToolTip == "立即收起（Esc）");
                    Check(collapse.IsVisible, side + " has a visible explicit collapse button");
                    closes.Add(AnimatedUserAction(window, () => collapse.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), false, side + " minus button"));
                    AnimatedUserAction(window, () => window.Reveal(), true, side + " reveal/tray/hotkey route");
                    AnimatedUserAction(window, () => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent }), false, side + " Escape key");
                    StopTimers(window);
                }
                HoverAndLeaveUseSameAnimation(window);
                Console.WriteLine("TIMING: own-window click handler mean/max " + opens.Average().ToString("F1") + "/" + opens.Max().ToString("F1") +
                    " ms; minus handler mean/max " + closes.Average().ToString("F1") + "/" + closes.Max().ToString("F1") +
                    " ms; actual click/minus/Escape/reveal/hover routes retained intermediate visual frames.");
                window.SetExpanded(false, false); StopTimers(window);
            }
            finally { settings.AutoCollapse = automatic; if (ReferenceEquals(Mouse.Captured, shell)) shell.ReleaseMouseCapture(); }
        }

        private static double AnimatedUserAction(IslandWindow window, Action action, bool expanded, string label)
        {
            Rect before = window.CurrentVisualBounds();
            var frames = new List<Rect>();
            var diagnostic = new List<string>(); var observing = Stopwatch.StartNew();
            EventHandler sample = (s, e) => { frames.Add(window.CurrentVisualBounds()); diagnostic.Add(observing.ElapsedMilliseconds + "ms frame " + AnimationDiagnostic(window)); };
            MouseEventHandler entered = (s, e) => diagnostic.Add(observing.ElapsedMilliseconds + "ms MouseEnter " + AnimationDiagnostic(window));
            MouseEventHandler left = (s, e) => diagnostic.Add(observing.ElapsedMilliseconds + "ms MouseLeave " + AnimationDiagnostic(window));
            window.MouseEnter += entered; window.MouseLeave += left;
            var handler = Stopwatch.StartNew();
            action(); handler.Stop();
            diagnostic.Add(observing.ElapsedMilliseconds + "ms returned " + AnimationDiagnostic(window));
            Check((bool)Read(window, "expanded") == expanded, label + " changes logical mode before returning");
            if (SystemParameters.ClientAreaAnimation)
            {
                Check((bool)Read(window, "visualTransitionActive"), label + " starts visual motion immediately");
                Check(SameBounds(window.CurrentVisualBounds(), before), label + " begins at the current visual frame instead of jumping to target");
            }
            // Subscribe after the transition's driver so each observation sees
            // that frame's newly drawn geometry, not the previous callback's state.
            CompositionTarget.Rendering += sample;
            try { Pump(IslandWindow.TransitionMilliseconds + 110); }
            finally { CompositionTarget.Rendering -= sample; window.MouseEnter -= entered; window.MouseLeave -= left; StopTimers(window); }
            Rect after = window.CurrentVisualBounds();
            bool settled = !(bool)Read(window, "visualTransitionActive") && (bool)Read(window, "expanded") == expanded;
            if (!settled || label.Contains("timer callback")) Console.WriteLine("DIAGNOSTIC: " + label + "; target=" + expanded + "; frames=" + frames.Count + "; final " + AnimationDiagnostic(window) + "\n" + String.Join("\n", diagnostic));
            Check(!(bool)Read(window, "visualTransitionActive") && (bool)Read(window, "expanded") == expanded, label + " settles at the requested mode");
            Check(!SameBounds(after, before), label + " changes visual dimensions");
            if (SystemParameters.ClientAreaAnimation)
                Check(frames.Count(frame => !SameBounds(frame, before) && !SameBounds(frame, after)) >= 2, label + " presents more than one genuine intermediate frame");
            Check(SameBounds(after, Bounds(window)), label + " final native geometry matches the visible island");
            return handler.Elapsed.TotalMilliseconds;
        }

        private static string AnimationDiagnostic(IslandWindow window)
        {
            var clock = Read(window, "transitionWatch") as Stopwatch;
            var focus = Keyboard.FocusedElement as DependencyObject;
            DiagnosticPoint cursor; bool cursorKnown = GetCursorPos(out cursor);
            var own = PresentationSource.FromVisual(window) as HwndSource;
            return "logical=" + Read(window, "expanded") + "; motion=" + Read(window, "visualTransitionActive") + "; clock=" + (clock == null ? "null" : clock.ElapsedMilliseconds + "ms") +
                "; version=" + Read(window, "transitionVersion") + "; over=" + window.IsMouseOver + "; direct=" + (Mouse.DirectlyOver == null ? "null" : Mouse.DirectlyOver.GetType().Name) +
                "; physicalOwn=" + (cursorKnown && own != null && WindowFromPoint(cursor) == own.Handle) + "; hover=" + ((DispatcherTimer)Read(window, "hoverTimer")).IsEnabled +
                "; collapse=" + ((DispatcherTimer)Read(window, "collapseTimer")).IsEnabled + "; suppressed=" + Read(window, "hoverSuppressed") + "; left=" + Mouse.LeftButton +
                "; focus=" + (focus == null ? "null" : focus.GetType().Name) + "; focusOwn=" + (focus != null && Window.GetWindow(focus) == window) + "; active=" + window.IsActive +
                "; visual=" + window.CurrentVisualBounds() + "; native=" + Bounds(window);
        }

        private static void HoverAndLeaveUseSameAnimation(IslandWindow window)
        {
            var settings = (Settings)Read(window, "settings");
            bool automatic = settings.AutoCollapse; settings.AutoCollapse = false;
            // ChangeMouseOver updates this test process's WPF input tree only.
            // It does not move the user's pointer or send input to another app.
            MethodInfo changeOver = typeof(MouseDevice).GetMethod("ChangeMouseOver", Private, null, new[] { typeof(IInputElement), typeof(int) }, null);
            IInputElement prior = Mouse.DirectlyOver;
            try
            {
                window.SetExpanded(false, false); window.UpdateLayout();
                Write(window, "hoverSuppressed", false);
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { (IInputElement)Read(window, "shell"), Environment.TickCount });
                Check(window.IsMouseOver, "own-window hover fixture enters the actual WPF mouse-over tree");
                Check(((DispatcherTimer)Read(window, "hoverTimer")).IsEnabled, "hover entry arms its delay timer");
                AnimatedUserAction(window, () => Call(window, "OnHoverElapsed"), true, "hover timer callback");
                settings.AutoCollapse = true;
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { (IInputElement)Read(window, "shell"), Environment.TickCount });
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { null, Environment.TickCount });
                Check(!window.IsMouseOver, "own-window hover fixture leaves the WPF mouse-over tree");
                Check(((DispatcherTimer)Read(window, "collapseTimer")).IsEnabled, "mouse leave arms automatic collapse delay");
                AnimatedUserAction(window, () => Call(window, "OnCollapseElapsed"), false, "automatic collapse timer callback");
            }
            finally
            {
                changeOver.Invoke(Mouse.PrimaryDevice, new object[] { prior, Environment.TickCount });
                settings.AutoCollapse = automatic; StopTimers(window);
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        private static void PopupAndCaptureGuards(IslandWindow window)
        {
            Call(window, "SelectToolsPage"); Call(window, "SelectTool", "converter"); window.SetExpanded(true, false); window.UpdateLayout();
            var panels = (Dictionary<string, UIElement>)Read(window, "toolPanels");
            var category = (ComboBox)panels["converter"].GetType().GetField("category", Private).GetValue(panels["converter"]);
            try
            {
                category.IsDropDownOpen = true; Pump(1);
                Check(category.IsDropDownOpen && (bool)Call(window, "HasInteractionGuard"), "unit dropdown protects its owning island");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse does not dismiss an open dropdown");
            }
            finally { category.IsDropDownOpen = false; Pump(1); StopTimers(window); }

            var shell = (Border)Read(window, "shell");
            try
            {
                shell.ContextMenu.IsOpen = true; Pump(1);
                Check((bool)Call(window, "HasInteractionGuard"), "open island context menu protects window state");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse preserves an open context menu");
                window.SetExpanded(false, false);
                Call(window, "OnHoverElapsed");
                Check(!(bool)Read(window, "expanded"), "hover does not expand behind a context menu");
            }
            finally { shell.ContextMenu.IsOpen = false; Pump(1); StopTimers(window); }

            window.SetExpanded(true, false); window.UpdateLayout();
            InitializeMouseSource(window);
            try
            {
                Check(shell.CaptureMouse(), "own window can capture for gesture regression");
                Check((bool)Call(window, "HasInteractionGuard"), "capture belonging to this island protects gestures");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse preserves captured interaction");
            }
            finally { if (ReferenceEquals(Mouse.Captured, shell)) shell.ReleaseMouseCapture(); StopTimers(window); }

            Write(window, "dialogOpen", true);
            try
            {
                Check((bool)Call(window, "HasInteractionGuard"), "dialog ownership protects the island");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse preserves a dialog interaction");
                window.SetExpanded(false, false);
                Call(window, "ShowPeek", Message("dialog-guard", "测试通知"));
                Check(!(bool)Read(window, "peeking"), "notification preview does not interrupt an active dialog");
            }
            finally { Write(window, "dialogOpen", false); StopTimers(window); }

            window.SetExpanded(true, false);
            var source = PresentationSource.FromVisual(window) as HwndSource;
            try
            {
                // Native file/folder dialogs disable their owner HWND and do not
                // appear in WPF OwnedWindows. Simulate only our own owner state.
                EnableWindow(source.Handle, false);
                Check((bool)Call(window, "HasInteractionGuard"), "native-dialog owner state protects the island");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse preserves a native file-dialog owner");
            }
            finally { EnableWindow(source.Handle, true); StopTimers(window); }
        }

        private static void NotificationPreviews(IslandWindow window)
        {
            var settings = (Settings)Read(window, "settings");
            settings.HideNotificationBody = true;
            window.SetExpanded(false, false);
            Call(window, "ShowPeek", Message("privacy-test", "不可泄露的标题"));
            Check((bool)Read(window, "peeking"), "notification preview opened");
            Check(((TextBlock)Read(window, "peekTitle")).Text == "收到一条新通知" && ((TextBlock)Read(window, "peekBody")).Text == "消息内容已隐藏",
                "privacy masks both notification title and body");
            Call(window, "OnCollapseElapsed");
            Check((bool)Read(window, "peeking"), "preview lifetime is not shortened by mouse-leave collapse");
            Call(window, "OpenPeekTarget");
            Check((bool)Read(window, "expanded") && !(bool)Read(window, "peeking") && (bool)Read(window, "notificationPageSelected"),
                "normal notification opens its message page");
            foreach (string tool in new[] { "focus", "reminders" })
            {
                settings.HiddenTools = tool; Call(window, "ConfigureToolkit");
                window.SetExpanded(false, false);
                Call(window, "ShowPeek", Message(tool == "focus" ? "sideisland.timer" : "sideisland.reminders", "测试提醒"));
                Call(window, "OpenPeekTarget");
                Check((bool)Read(window, "expanded") && (bool)Read(window, "toolsPageSelected") && (string)Read(window, "selectedTool") == tool,
                    tool + " preview opens its corresponding module");
                Check(!(settings.HiddenTools ?? "").Split(',').Contains(tool), tool + " preview makes a hidden destination accessible");
            }
            settings.HideNotificationBody = false; settings.HiddenTools = ""; Call(window, "ConfigureToolkit");
            window.SetExpanded(false, false); StopTimers(window);
        }

        private static void ActiveEditorGuard(IslandWindow window)
        {
            Call(window, "SelectToolsPage"); Call(window, "SelectTool", "converter"); window.SetExpanded(true, false); window.UpdateLayout();
            var panels = (Dictionary<string, UIElement>)Read(window, "toolPanels");
            var input = (TextBox)panels["converter"].GetType().GetField("input", Private).GetValue(panels["converter"]);
            try
            {
                // Activate the test thread's own HWND. This does not request
                // foreground permission or activate any window from another app.
                var ownSource = PresentationSource.FromVisual(window) as HwndSource;
                SetActiveWindow(ownSource.Handle); input.Focus(); Keyboard.Focus(input); Pump(1);
                Check(window.IsActive && ReferenceEquals(Keyboard.FocusedElement, input), "keyboard focus belongs to this window's visible editor");
                Check(!(bool)Call(window, "HasPopupInteraction") && (bool)Call(window, "HasInteractionGuard"), "active text editing protects collapse independently of popup guards");
                Call(window, "OnCollapseElapsed");
                Check((bool)Read(window, "expanded"), "auto-collapse preserves keyboard editing");
                var source = PresentationSource.FromVisual(window);
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                Check(!(bool)Read(window, "expanded"), "Escape still explicitly collapses while editing");
            }
            finally { Keyboard.ClearFocus(); SetActiveWindow(IntPtr.Zero); StopTimers(window); }
        }

        private static IslandNotification Message(string appId, string title)
        {
            return new IslandNotification { Id = Guid.NewGuid().ToString("N"), AppId = appId, AppName = "交互测试", Title = title, Body = "不可泄露的正文", IsDemo = true, CreatedAt = DateTime.UtcNow };
        }
        private static void InitializeMouseSource(IslandWindow window)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            Check(source != null && !source.IsDisposed, "own input source is live");
            // ContextMenu fades out on its popup HWND after IsOpen becomes false.
            // Let that native capture unwind before beginning a separate gesture.
            var cleanup = Stopwatch.StartNew();
            while ((GetCapture() != IntPtr.Zero || Mouse.Captured != null) && cleanup.ElapsedMilliseconds < 500) Pump(10);
            Check(GetCapture() == IntPtr.Zero && Mouse.Captured == null, "popup cleanup released its capture (native=" + GetCapture() + ", managed=" + (Mouse.Captured == null ? "null" : Mouse.Captured.GetType().FullName) + ")");
            SetCapture(source.Handle);
            try { SendMessage(source.Handle, 0x0200, IntPtr.Zero, new IntPtr(40 | (40 << 16))); }
            finally { if (GetCapture() == source.Handle) ReleaseCapture(); }
            Check(ReferenceEquals(Mouse.PrimaryDevice.ActiveSource, source), "mouse report targets only the test window");
        }
        private static void StopTimers(IslandWindow window)
        {
            foreach (FieldInfo field in typeof(IslandWindow).GetFields(Private))
                if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop();
        }
        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        private static object Read(IslandWindow window, string name) { return typeof(IslandWindow).GetField(name, Private).GetValue(window); }
        private static void Write(IslandWindow window, string name, object value) { typeof(IslandWindow).GetField(name, Private).SetValue(window, value); }
        private static object Call(IslandWindow window, string name, params object[] values)
        {
            var method = typeof(IslandWindow).GetMethod(name, Private);
            if (method == null) throw new Exception("Missing interaction guard: " + name);
            try { return method.Invoke(window, values); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static Rect Bounds(Window window) { return new Rect(window.Left, window.Top, window.Width, window.Height); }
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 1; }
        private static bool NearlyExact(double a, double b) { return Math.Abs(a - b) < .001; }
        private static bool SameBounds(Rect a, Rect b) { return Near(a.Left, b.Left) && Near(a.Top, b.Top) && Near(a.Width, b.Width) && Near(a.Height, b.Height); }
        private static void Check(bool condition, string description) { if (!condition) throw new Exception("Interaction regression: " + description); }
    }
}
