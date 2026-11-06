using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

internal sealed record ScreenPinSources
{
    public Func<CancellationToken, Task<ScreenCaptureResult>>? Capture { get; init; }
    public Func<CancellationToken, Task<string?>>? ChoosePng { get; init; }
    public Func<string, CancellationToken, Task<Bitmap>>? DecodePng { get; init; }
    public ScreenPinSaveSources? Save { get; init; }
}

public sealed partial class IslandWindow
{
    private readonly List<ScreenPinWindow> screenPins = new();
    private CancellationTokenSource? captureCancellation;
    private bool screenPinLifetimeHooked;
    private Button? capturePinButton, openPinButton, cancelPinButton;
    internal Task LatestScreenPinOperation { get; private set; } = Task.CompletedTask;
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
        capturePinButton = AsyncButton(T("截取区域并钉屏", "Capture area and pin"), () => LatestScreenPinOperation = PinImageAsync(true), CanStartPin); capturePinButton.Name = "pin-capture";
        openPinButton = AsyncButton(T("打开 PNG 图片并钉屏", "Open a PNG and pin"), () => LatestScreenPinOperation = PinImageAsync(false), CanStartPin); openPinButton.Name = "pin-open";
        body.Children.Add(capturePinButton); body.Children.Add(openPinButton);
        cancelPinButton = Button(T("取消截图", "Cancel capture"), CancelPinOperation); cancelPinButton.Name = "pin-cancel";
        var closeAll = Button(T("关闭全部钉屏", "Close all pins"), () => { CancelPinOperation(); foreach (var pin in screenPins.ToArray()) pin.Close(); }); closeAll.Name = "pin-close-all";
        body.Children.Add(Actions(cancelPinButton, closeAll));
        body.Children.Add(Hint(T("图片可拖动、滚轮缩放、调整透明度，并另存 PNG。截图期间灵动岛会暂时隐藏；按 Esc 可取消系统选区。", "Drag the image, scroll to zoom, adjust opacity and save PNG. The island hides during capture; Esc cancels the system picker.")));
        body.Children.Add(Hint(T("Mac 遵守系统屏幕录制权限。Ubuntu 优先使用桌面 Screenshot Portal；只有接口不可用时才尝试已安装的 GNOME Screenshot 或 Spectacle。", "Mac uses system Screen Recording consent. Ubuntu prefers the desktop Screenshot portal, with installed GNOME Screenshot or Spectacle as fallbacks only when that interface is unavailable.")));
        body.Children.Add(Hint(T("Wayland 下置顶和窗口移动由桌面合成器决定。本次尚未在目标 Mac／Ubuntu 桌面上实测。", "On Wayland, the compositor controls window movement and stacking. Native Mac / Ubuntu capture has not yet been tested on target hardware.")));
        UpdatePinActions();
    }

    private bool CanStartPin() => captureCancellation is null && !closing;
    private void UpdatePinActions()
    {
        if (capturePinButton is not null) capturePinButton.IsEnabled = CanStartPin();
        if (openPinButton is not null) openPinButton.IsEnabled = CanStartPin();
        if (cancelPinButton is not null) cancelPinButton.IsEnabled = !closing && captureCancellation is { IsCancellationRequested: false };
    }
    private void CancelPinOperation() { captureCancellation?.Cancel(); UpdatePinActions(); }
    private async Task<string?> ChoosePinPngAsync(CancellationToken token)
    {
        if (integrations.ScreenPins?.ChoosePng is { } choose) return await choose(token);
        if (testMode) return null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("打开图片", "Open an image"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }] });
        try
        {
            token.ThrowIfCancellationRequested();
            if (files.Count == 0) return null;
            return files[0].TryGetLocalPath() ?? throw new IOException(T("请选择本地 PNG 文件。", "Choose a local PNG file."));
        }
        finally { foreach (var file in files) file.Dispose(); }
    }
    private async Task PinImageAsync(bool capture)
    {
        if (!CanStartPin() || (testMode && (capture ? integrations.ScreenPins?.Capture is null : integrations.ScreenPins?.ChoosePng is null))) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        captureCancellation = cancellation; UpdatePinActions();
        ScreenCaptureFile? owned = null; Bitmap? image = null;
        try
        {
            string? path;
            if (capture)
            {
                bool wasVisible = IsVisible;
                ScreenCaptureResult result;
                try
                {
                    hover.Stop(); collapse.Stop(); if (wasVisible) Hide(); hover.Stop(); collapse.Stop();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Delay(180, cancellation.Token);
                    result = integrations.ScreenPins?.Capture is { } provider ? await provider(cancellation.Token) :
                        await Task.Run(() => new ScreenCaptureService(store.DirectoryPath).CaptureRegionAsync(cancellation.Token));
                    owned = result.Capture;
                }
                finally { if (!closing && wasVisible) { Show(); Place(); hover.Stop(); collapse.Stop(); } }
                cancellation.Token.ThrowIfCancellationRequested();
                if (result.Status == ScreenCaptureStatus.Cancelled) return;
                if (result.Status != ScreenCaptureStatus.Completed || owned is null) { Report(T("截图未完成：", "Capture unavailable: ") + result.Error); return; }
                path = owned.Path;
            }
            else path = await ChoosePinPngAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (path is null || closing) return;
            image = integrations.ScreenPins?.DecodePng is { } decode ? await decode(path, cancellation.Token) : await ScreenPinImageFiles.DecodeAsync(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing) return;
            // OpenScreenPin consumes the bitmap even if window construction fails.
            var transferred = image; image = null; OpenScreenPin(transferred, capture ? null : Path.GetFullPath(path));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing && !cancellation.IsCancellationRequested) Report(T("钉屏未完成：", "Could not pin image: ") + error.Message); }
        finally
        {
            image?.Dispose(); owned?.Dispose();
            if (ReferenceEquals(captureCancellation, cancellation)) captureCancellation = null;
            UpdatePinActions();
        }
    }
    private void OpenScreenPin(Bitmap bitmap, string? sourcePath = null)
    {
        if (closing) { bitmap.Dispose(); return; }
        ScreenPinWindow? pin = null;
        try
        {
            pin = new ScreenPinWindow(bitmap, prefs.Language, prefs.LightTheme, testMode, integrations.ScreenPins?.Save, sourcePath);
            screenPins.Add(pin); var opened = pin; pin.Closed += (_, _) => screenPins.Remove(opened);
            pin.Show();
        }
        catch { if (pin is null) bitmap.Dispose(); else { screenPins.Remove(pin); pin.ReleaseImage(); pin.Close(); } throw; }
    }
}
