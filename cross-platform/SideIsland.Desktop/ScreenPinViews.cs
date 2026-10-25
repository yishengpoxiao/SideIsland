using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private readonly List<ScreenPinWindow> screenPins = new();
    private CancellationTokenSource? captureCancellation;
    private bool screenPinLifetimeHooked;
    private void RenderScreenPin()
    {
        if (!screenPinLifetimeHooked)
        {
            screenPinLifetimeHooked = true;
            // Root Closing can cancel exit while a batch transaction finishes.
            // Pins belong to the app lifetime, so release them only after an
            // actual close, never merely after a cancellable close request.
            Closed += (_, _) => { captureCancellation?.Cancel(); foreach (var pin in screenPins.ToArray()) pin.Close(); };
        }
        body.Children.Add(Hint(T("选择屏幕区域并悬浮在桌面上，边看边操作。", "Capture a screen area and keep it visible while you work.")));
        // Two full rows stay readable even at 320-point island widths.
        body.Children.Add(AsyncButton(T("截取区域并钉屏", "Capture area and pin"), CaptureAndPinAsync));
        body.Children.Add(AsyncButton(T("打开 PNG 图片并钉屏", "Open a PNG and pin"), async () =>
        {
            if (testMode) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("打开图片", "Open an image"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }] });
            if (files.Count == 0 || closing) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) { Report(T("请选择本地 PNG 文件。", "Choose a local PNG file.")); return; }
            await ScreenCaptureService.ValidatePngAsync(path, lifetime.Token);
            await using var stream = await files[0].OpenReadAsync();
            var bitmap = await Task.Run(() => new Bitmap(stream), lifetime.Token);
            OpenScreenPin(bitmap);
        }));
        body.Children.Add(Actions(Button(T("取消截图", "Cancel capture"), () => captureCancellation?.Cancel()), Button(T("关闭全部钉屏", "Close all pins"), () => { foreach (var pin in screenPins.ToArray()) pin.Close(); })));
        body.Children.Add(Hint(T("图片可拖动、滚轮缩放、调整透明度，并另存 PNG。截图期间灵动岛会暂时隐藏；按 Esc 可取消系统选区。", "Drag the image, scroll to zoom, adjust opacity and save PNG. The island hides during capture; Esc cancels the system picker.")));
        body.Children.Add(Hint(T("Mac 遵守系统屏幕录制权限。Ubuntu 优先使用桌面 Screenshot Portal；只有接口不可用时才尝试已安装的 GNOME Screenshot 或 Spectacle。", "Mac uses system Screen Recording consent. Ubuntu prefers the desktop Screenshot portal, with installed GNOME Screenshot or Spectacle as fallbacks only when that interface is unavailable.")));
        body.Children.Add(Hint(T("Wayland 下置顶和窗口移动由桌面合成器决定。本次尚未在目标 Mac／Ubuntu 桌面上实测。", "On Wayland, the compositor controls window movement and stacking. Native Mac / Ubuntu capture has not yet been tested on target hardware.")));
    }

    private async Task CaptureAndPinAsync()
    {
        if (captureCancellation is not null || testMode || closing) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        captureCancellation = cancellation;
        bool wasVisible = IsVisible;
        ScreenCaptureResult? result = null;
        try
        {
            hover.Stop(); collapse.Stop();
            if (wasVisible) Hide();
            hover.Stop(); collapse.Stop();
            // Yield to the compositor instead of blocking the UI thread. The
            // system picker appears after the island's hide has been committed.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(180, cancellation.Token);
            result = await new ScreenCaptureService(store.DirectoryPath).CaptureRegionAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            captureCancellation = null;
            if (!closing && wasVisible) { Show(); Place(); hover.Stop(); collapse.Stop(); }
        }
        using var owned = result?.Capture;
        if (closing || result is null || result.Status == ScreenCaptureStatus.Cancelled) return;
        if (owned is null) { Report(T("截图未完成：", "Capture unavailable: ") + result.Error); return; }
        using var stream = File.OpenRead(owned.Path);
        var image = await Task.Run(() => new Bitmap(stream), lifetime.Token);
        OpenScreenPin(image);
    }
    private void OpenScreenPin(Bitmap bitmap)
    {
        if (closing) { bitmap.Dispose(); return; }
        ScreenPinWindow? pin = null;
        try
        {
            pin = new ScreenPinWindow(bitmap, prefs.Language, prefs.LightTheme, testMode);
            screenPins.Add(pin); var opened = pin; pin.Closed += (_, _) => screenPins.Remove(opened);
            pin.Show();
        }
        catch { if (pin is null) bitmap.Dispose(); else { screenPins.Remove(pin); pin.ReleaseImage(); pin.Close(); } throw; }
    }
}
