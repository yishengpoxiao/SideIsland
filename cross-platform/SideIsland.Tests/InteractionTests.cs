using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SideIsland.Desktop;
using CodexIsland;
using SideIsland.Core;

namespace SideIsland.Tests;
internal static class InteractionTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "side-island-interaction-" + Guid.NewGuid().ToString("N"));
        var window = new IslandWindow(true, directory);
        try
        {
            window.Settings.HoverExpand = false; window.Settings.Slot1 = "focus";
            window.Show(); Flush(window);
            var compact = Field<Border>(window, "compactHost");
            var target = ((Grid)compact.Child!).Children[0];
            var point = Center(target, window);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush(window);
            Check(Field<FocusTimer>(window, "focus").IsRunning && !window.IsExpanded, "collapsed focus quick start");
            window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Vector(2, 1), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(2, 1), MouseButton.Left); Flush(window);
            Check(!Field<FocusTimer>(window, "focus").IsRunning, "sub-threshold movement remains a tap");
            window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Vector(-160, 50), RawInputModifiers.LeftMouseButton);
            Check(!window.IsExpanded, "drag remains collapsed");
            window.MouseUp(point + new Vector(-160, 50), MouseButton.Left); Flush(window);
            Check(!Field<bool>(window, "dragging") && Field<PixelPoint?>(window, "dragOrigin") is null, "drag release always clears capture state");
            Check(!Field<FocusTimer>(window, "focus").IsRunning, "drag does not trigger quick action");

            window.Expand(); Flush(window);
            for (int cycle = 0; cycle < 24; cycle++)
            {
                var language = FindButton(window, window.Settings.Language == "en" ? "中" : "EN");
                Click(window, language); Check(window.IsExpanded, "language button is not a header drag");
                Click(window, FindButton(window, window.Settings.LightTheme ? "☾" : "☼"));
                Check(window.IsExpanded, "theme button is not a header drag");
                window.SelectToolForTest(IslandWindow.Tools[cycle % IslandWindow.Tools.Length].Id); Flush(window);
                window.Collapse(); window.Expand(); Flush(window);
                Check(!Field<bool>(window, "dragging") && window.IsVisible, "rapid transitions retain a live window");
            }
            window.SelectToolForTest("notes"); Flush(window);
            var input = window.GetVisualDescendants().OfType<TextBox>().First(t => t.AcceptsReturn);
            input.Text = "中文 draft 😀";
            Click(window, FindButton(window, window.Settings.Language == "en" ? "中" : "EN"));
            Check(window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "中文 draft 😀"), "rebuild preserves input drafts");
            window.SelectToolForTest("notifications"); Flush(window);
            var center = Field<NotificationCenter>(window, "notificationCenter");
            center.Publish(new("test-ui", "UI fixture", "Read action", "Explicit headless test", DateTimeOffset.UtcNow));
            typeof(IslandWindow).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["messages"]); Flush(window);
            Click(window, FindButton(window, window.Settings.Language == "en" ? "Read all" : "全部已读"));
            Check(center.UnreadCount == 0, "notification read-all action updates the real center");
            Click(window, FindButton(window, window.Settings.Language == "en" ? "Clear" : "清空消息"));
            Check(center.Items.Count == 0 && center.Applications.Any(a => a.SourceId == "test-ui"), "notification clear retains app rules");
            Console.WriteLine("PASS: headless pointer taps/drag capture, 24 rapid language/theme/tool/collapse cycles, header action hit tests and input draft preservation. No native desktop input injected.");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Point Center(Control control, Window window) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    private static Button FindButton(Window window, string caption) => window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content is TextBlock t && t.Text == caption);
    private static void Click(Window window, Control control) { var p = Center(control, window); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Flush(window); }
    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Interaction: " + message); }
}
