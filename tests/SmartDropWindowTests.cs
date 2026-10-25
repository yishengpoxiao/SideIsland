using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    internal static class SmartDropWindowTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private sealed class Ocr : IOcrAdapter
        {
            internal int Calls;
            public OcrLanguageChoice[] Languages() { return new[] { new OcrLanguageChoice { Name = "Fixture language", Tag = "en-US" } }; }
            public Task<string> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellation) { Calls++; cancellation.ThrowIfCancellationRequested(); return Task.FromResult("Synthetic OCR 8080 配置"); }
        }
        internal static void Run()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"); string language = UiLocalization.Language, theme = UiTheme.Name;
            var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string folder = Path.Combine(Path.GetTempPath(), "SideIsland.SmartDropWindow." + Guid.NewGuid().ToString("N")); IslandWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder); new SettingsStore().Save(new Settings { AutoMusic = false, NotificationsEnabled = false, AlwaysOnTop = false, AutoCollapse = false, HoverExpandEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false }; window.Show(); window.UpdateLayout(); StopTimers(window);
                Check((bool)Call(window, "SmartDropNativeTarget", new TextBox()) && (bool)Call(window, "SmartDropNativeTarget", Read<FileShelfPanel>(window, "fileShelfPanel")) && (bool)Call(window, "SmartDropNativeTarget", Read<FileHashPanel>(window, "fileHashPanel")), "editors, file shelf and hash panel retain their native drop handling");
                Check((bool)Call(window, "SmartDropNativeTarget", Read<BatchFilesPanel>(window, "batchFilesPanel")) && (bool)Call(window, "SmartDropNativeTarget", Read<LanTransferPanel>(window, "lanTransferPanel")), "batch and LAN panels retain their own validated drop handling");
                Check(!(bool)Call(window, "SmartDropNativeTarget", Read<Border>(window, "shell")), "blank island body accepts the contextual drop menu");
                var payload = SmartDropParser.Read(new DataObject(DataFormats.FileDrop, new[] { @"C:\Fixture\image.png" }));
                Call(window, "ShowSmartDropMenu", payload); var menu = Read<ContextMenu>(window, "smartDropMenu");
                Check(menu != null && menu.IsOpen && menu.Items.Count == 8, "image drops offer compression, pinning, OCR, hashing, collection, batch and LAN without opening a blocking dialog");
                Close(window, menu); Check(!(bool)Call(window, "HasPopupInteraction"), "cancelled smart drop releases the interaction guard");
                Call(window, "ShowSmartDropMenu", payload); menu = Read<ContextMenu>(window, "smartDropMenu"); typeof(MenuItem).GetMethod("OnClick", Fields).Invoke(menu.Items[5], null); Close(window, menu);
                Check(Read<string>(window, "selectedTool") == "batch", "batch drop routes to the batch tool");
                Call(window, "ShowSmartDropMenu", payload); menu = Read<ContextMenu>(window, "smartDropMenu"); typeof(MenuItem).GetMethod("OnClick", Fields).Invoke(menu.Items[6], null); Close(window, menu);
                Check(Read<string>(window, "selectedTool") == "lan-transfer", "LAN drop routes to the share tool without starting a server in demo");
                Call(window, "AcceptDroppedFiles", new Action(() => { throw new ArgumentException("Missing fixture file"); }));
                Check(Read<TextBlock>(window, "toolkitHint").Text.Contains("Missing fixture file"), "invalid dropped files report an error without escaping the UI event");
                Read<TextBox>(Read<TextDiffPanel>(window, "textDiffPanel"), "left").Clear(); Read<TextBox>(Read<TextDiffPanel>(window, "textDiffPanel"), "right").Clear();
                string text = "port=8080\n配置😀"; payload = SmartDropParser.Read(new DataObject(DataFormats.UnicodeText, text)); Call(window, "ShowSmartDropMenu", payload); menu = Read<ContextMenu>(window, "smartDropMenu");
                Check(menu.Items.Count == 3, "text drop menu contains compare, QR and cancel");
                typeof(MenuItem).GetMethod("OnClick", Fields).Invoke(menu.Items[0], null); Close(window, menu);
                Check(Read<string>(window, "selectedTool") == "diff" && Read<TextBox>(Read<TextDiffPanel>(window, "textDiffPanel"), "left").Text == text, "text drop comparison preserves exact Unicode content; tool=" + Read<string>(window, "selectedTool") + "; text=" + Read<TextBox>(Read<TextDiffPanel>(window, "textDiffPanel"), "left").Text.Replace("\r", "[CR]").Replace("\n", "[LF]"));
                Call(window, "ShowSmartDropMenu", payload); menu = Read<ContextMenu>(window, "smartDropMenu"); typeof(MenuItem).GetMethod("OnClick", Fields).Invoke(menu.Items[1], null); Close(window, menu);
                Check(Read<string>(window, "selectedTool") == "qr" && Read<TextBox>(Read<QrTransferPanel>(window, "qrTransferPanel"), "input").Text == text, "text drop sends exact data to QR");
                Capture(window);
                Console.WriteLine("PASS: own-window smart drop menu lifecycle, editor/collector bypass, Unicode compare/QR actions and synthetic OCR capture cancellation/success/restoration.");
            }
            finally { if (window != null) { var menu = Read<ContextMenu>(window, "smartDropMenu"); if (menu != null) menu.IsOpen = false; StopTimers(window); window.Close(); } SynchronizationContext.SetSynchronizationContext(context); AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        private static void Capture(IslandWindow window)
        {
            window.SetExpanded(false, false); StopTimers(window); var engine = new Ocr();
            Func<Int32Rect, CancellationToken, BitmapSource> synthetic = (area, token) => { token.ThrowIfCancellationRequested(); var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 16 * 4], 32 * 4); bitmap.Freeze(); return bitmap; };
            using (var cancelled = new OcrPanel(false, engine, synthetic, token => Task.FromResult<Int32Rect?>(null)))
            {
                cancelled.CaptureStateChanged += state => Call(window, "OnCaptureStateChanged", state); Pump(cancelled.BeginCaptureAsync());
                Check(window.IsVisible && engine.Calls == 0 && !cancelled.IsCapturing && !Read<bool>(window, "dialogOpen"), "cancelled synthetic selection restores the owner without invoking OCR");
            }
            using (var success = new OcrPanel(false, engine, synthetic, token => Task.FromResult<Int32Rect?>(new Int32Rect(10, 10, 32, 16))))
            {
                success.CaptureStateChanged += state => Call(window, "OnCaptureStateChanged", state);
                success.CompareRequested += value => Read<TextDiffPanel>(window, "textDiffPanel").AcceptExternalText(value);
                Pump(success.BeginCaptureAsync());
                Check(window.IsVisible && engine.Calls == 1 && !success.IsCapturing && Read<TextBox>(success, "result").Text == "Synthetic OCR 8080 配置", "generated pixels are recognized on the fake adapter and the island is restored");
                Read<Button>(success, "compareButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Read<TextBox>(Read<TextDiffPanel>(window, "textDiffPanel"), "right").Text == "Synthetic OCR 8080 配置", "OCR compare preserves the existing left side");
            }
        }
        private static void Close(IslandWindow window, ContextMenu menu)
        {
            menu.IsOpen = false; PumpDelay(); DateTime end = DateTime.UtcNow.AddSeconds(2);
            while (Read<ContextMenu>(window, "smartDropMenu") != null && DateTime.UtcNow < end) PumpDelay(); StopTimers(window);
            PumpDelay(); Check(Read<ContextMenu>(window, "smartDropMenu") == null, "smart drop closes its native menu");
        }
        private static void Pump(Task task) { DateTime end = DateTime.UtcNow.AddSeconds(8); while (!task.IsCompleted && DateTime.UtcNow < end) PumpDelay(); if (task.IsFaulted) throw task.Exception.GetBaseException(); Check(task.IsCompleted, "synthetic OCR completes"); }
        private static void PumpDelay() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(2); }
        private static void StopTimers(IslandWindow window) { foreach (var field in typeof(IslandWindow).GetFields(Fields)) if (field.FieldType == typeof(DispatcherTimer)) ((DispatcherTimer)field.GetValue(window)).Stop(); }
        private static T Read<T>(object target, string name) { return (T)target.GetType().GetField(name, Fields).GetValue(target); }
        private static object Call(object target, string name, params object[] args) { try { return target.GetType().GetMethod(name, Fields).Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static void Check(bool value, string message) { if (!value) throw new Exception("Smart drop UI: " + message); }
    }
}
