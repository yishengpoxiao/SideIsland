using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class AdditionalToolIntegrationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR");
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.Integration." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false, IsHitTestVisible = false };
                window.Loaded += (s, e) => StopTimers(window); window.Show(); window.UpdateLayout(); StopTimers(window);
                window.SetExpanded(true, false); window.UpdateLayout();
                var layer = Read<Grid>(window, "resizeLayer");
                Check(layer.Children.OfType<Thumb>().Count() == 8, "all eight resize hit targets remain");
                foreach (var grip in layer.Children.OfType<Thumb>())
                {
                    grip.ApplyTemplate();
                    Check(VisualTreeHelper.GetChildrenCount(grip) == 1, "resize target has its own visual template");
                    var border = (Border)VisualTreeHelper.GetChild(grip, 0);
                    Check(border.Child == null, "corner markers are removed from the actual template");
                    grip.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                    Check(((SolidColorBrush)grip.Background).Color.A == 0, "hover does not highlight resize regions");
                    grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    Check(Read<bool>(window, "resizing") && ((SolidColorBrush)grip.Background).Color.A == 0, "resizing remains active with an invisible hit target");
                    grip.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
                }
                Call(window, "OpenDroppedImage", @"C:\Fixture\picture.png");
                Check(Read<string>(window, "selectedTool") == "image" && Read<bool>(window, "expanded"), "dropping a picture routes to image processing and expands");
                var filePanel = Read<FileShelfPanel>(window, "fileShelfPanel");
                Check((bool)Call(window, "IsFileShelfDrop", filePanel.Children[0]), "file shelf retains its own file-drop behavior");
                Check(!(bool)Call(window, "IsFileShelfDrop", Read<ImageQuickPanel>(window, "imageQuickPanel")), "image processing accepts the global image route");
                var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\Fixture\photo.JPG" });
                object[] args = { data, null };
                Check((bool)typeof(IslandWindow).GetMethod("TryDroppedImage", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args) && (string)args[1] == @"C:\Fixture\photo.JPG", "image file detection accepts upper-case extension");
                args = new object[] { new DataObject(DataFormats.FileDrop, new[] { "a.png", "b.png" }), null };
                Check(!(bool)typeof(IslandWindow).GetMethod("TryDroppedImage", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args), "multi-file drops are not partially consumed");
                Call(window, "OnCaptureStateChanged", true);
                Check(!window.IsVisible && Read<bool>(window, "dialogOpen"), "region selection hides the island and guards automatic interaction");
                Call(window, "OnCaptureStateChanged", false);
                Check(window.IsVisible && !Read<bool>(window, "dialogOpen"), "selection completion and cancellation restore the island");
                var awake = Read<KeepAwakePanel>(window, "keepAwakePanel");
                Call(awake, "Begin", 30); Call(window, "UpdateToolkitCompact");
                Check(awake.IsRunning && Read<TextBlock>(window, "miniStatus").Text.StartsWith("唤醒 "), "remaining awake time is visible on the compact island using a demo adapter");
                window.Hide(); Check(awake.IsRunning, "hiding the island preserves its temporary awake session"); window.Show();
                window.SetExpanded(false, false);
                Call(window, "ShowPeek", new IslandNotification { Id = "watch-test", AppId = "sideisland.watch", AppName = "任务守望", Title = "完成", Body = "合成测试", IsDemo = true });
                Call(window, "OpenPeekTarget");
                Check(Read<string>(window, "selectedTool") == "watch", "task completion preview opens task watch");
                window.Close(); Check(!awake.IsRunning, "application exit releases the demo awake session");
                Console.WriteLine("PASS: new tool integration, invisible resize targets, image-drop routing, capture recovery, compact awake timer and watch preview.");
            }
            finally
            {
                if (window != null && !Read<bool>(window, "shuttingDown")) window.Close();
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Private)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
        private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, Private).Invoke(value, args); }
        private static void Check(bool success, string message) { if (!success) throw new Exception("Additional tools: " + message); }
    }
}
