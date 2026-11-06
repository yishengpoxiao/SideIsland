using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Net.Codecrete.QrCodeGenerator;
using SideIsland.Core;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    internal Task LatestClipboardRead { get; private set; } = Task.CompletedTask;
    internal Task LatestClipboardWrite { get; private set; } = Task.CompletedTask;
    internal Task LatestQrSave { get; private set; } = Task.CompletedTask;

    private void RenderClipboard()
    {
        var content = new StackPanel { Spacing = 6 };
        var text = Input("clipboard", "", true); text.Name = "clipboard-text"; text.MinHeight = 200;
        var status = Hint(""); status.Name = "clipboard-status";
        long revision = 0; bool detached = false, applying = false, writing = false; CancellationTokenSource? pending = null;
        Button? read = null, copy = null;
        bool Available() => !closing && !detached;
        bool CanRead() => Available() && pending is null;
        bool CanCopy() => Available() && !writing;
        void UpdateActions() { if (read is not null) read.IsEnabled = CanRead(); if (copy is not null) copy.IsEnabled = CanCopy(); }
        void InvalidateRead()
        {
            revision++; pending?.Cancel(); pending = null;
            if (Available()) { status.Text = ""; UpdateActions(); }
        }
        async Task Read()
        {
            if (!CanRead()) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pending = cancellation;
            long request = ++revision; bool Current() => Available() && revision == request && ReferenceEquals(pending, cancellation);
            status.Text = T("正在读取…", "Reading…"); UpdateActions();
            try
            {
                string? value;
                if (integrations.ReadClipboardText is not null) value = await integrations.ReadClipboardText(cancellation.Token);
                else if (testMode) { status.Text = T("演示模式不读取剪贴板。", "Preview mode does not read the clipboard."); return; }
                else if (Clipboard is not null) value = await Clipboard.TryGetTextAsync();
                else throw new InvalidOperationException("Clipboard unavailable");
                if (!Current() || cancellation.IsCancellationRequested) return;
                applying = true; try { text.Text = value ?? ""; } finally { applying = false; }
                status.Text = value is null ? T("剪贴板中没有文字。", "The clipboard has no text.") : T("已读取文字。", "Text read.");
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (Current()) status.Text = T("无法读取剪贴板，请重试。", "Clipboard unavailable. Try again."); }
            finally { if (ReferenceEquals(pending, cancellation)) pending = null; if (Available()) UpdateActions(); }
        }
        async Task Copy()
        {
            if (!CanCopy()) return;
            long request = revision; string captured = text.Text ?? ""; writing = true; UpdateActions();
            bool Current() => Available() && request == revision;
            try
            {
                if (integrations.WriteClipboardText is not null) await integrations.WriteClipboardText(captured);
                else if (testMode) { if (Current()) status.Text = T("演示模式不修改剪贴板。", "Preview mode does not modify the clipboard."); return; }
                else if (Clipboard is not null) await Clipboard.SetTextAsync(captured);
                else throw new InvalidOperationException("Clipboard unavailable");
                if (Current()) status.Text = T("已复制文字。", "Text copied.");
            }
            catch (Exception) { if (Current()) status.Text = T("无法复制文字，请重试。", "Could not copy text. Try again."); }
            finally { writing = false; if (Available()) UpdateActions(); }
        }
        read = AsyncButton(T("读取文字", "Read text"), () => LatestClipboardRead = Read(), CanRead); read.Name = "clipboard-read";
        copy = AsyncButton(T("复制文字", "Copy text"), () => LatestClipboardWrite = Copy(), CanCopy); copy.Name = "clipboard-copy";
        text.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty && !applying) InvalidateRead(); };
        content.DetachedFromVisualTree += (_, _) => { detached = true; InvalidateRead(); };
        content.Children.Add(text); content.Children.Add(Actions(read, copy)); content.Children.Add(status);
        content.Children.Add(Hint(T("仅在点击时读写；文字草稿只留在本次运行内，不保存到磁盘。Wayland 读取取决于桌面权限。", "Reads or writes only when clicked. Drafts stay in memory for this session and are not saved to disk. Wayland access depends on desktop permissions.")));
        body.Children.Add(content);
    }

    private void RenderQr()
    {
        var content = new StackPanel { Spacing = 6 };
        var input = Input("qr", "", true); input.Name = "qr-input"; input.MaxLength = 10000;
        var qr = new QrCanvas { Name = "qr-preview", Width = 230, Height = 230, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false, Margin = new(0, 6) };
        var status = Hint(""); status.Name = "qr-status";
        bool detached = false, saving = false; long revision = 0; Button? save = null; CancellationTokenSource? pendingSave = null;
        bool Available() => !closing && !detached;
        bool CanSave() => Available() && !saving && qr.Code is not null;
        void UpdateActions() { if (save is not null) save.IsEnabled = CanSave(); }
        void Invalidate()
        {
            revision++; pendingSave?.Cancel(); qr.Code = null; qr.IsVisible = false;
            if (Available()) { status.Text = T("内容已更新，请重新生成二维码。", "Content changed. Generate a new QR code."); UpdateActions(); }
        }
        void Generate()
        {
            if (!Available()) return; revision++; pendingSave?.Cancel(); qr.Code = null; qr.IsVisible = false;
            try { qr.Code = QrCode.EncodeText(input.Text ?? "", QrCode.Ecc.Low); qr.IsVisible = true; status.Text = T("二维码已生成。", "QR code generated."); }
            catch (DataTooLongException) { status.Text = T("内容超过单张二维码容量，请缩短文字。", "This exceeds one QR code's capacity. Shorten the text."); }
            finally { UpdateActions(); }
        }
        async Task SaveQr()
        {
            if (!CanSave()) return; var snapshot = qr.Code!; long request = revision; saving = true; UpdateActions();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pendingSave = cancellation;
            bool Current() => Available() && revision == request && ReferenceEquals(snapshot, qr.Code);
            try
            {
                string? destination;
                if (integrations.ChooseQrDestination is not null) destination = await integrations.ChooseQrDestination("side-island-qr.png");
                else if (testMode) { status.Text = T("演示模式不保存文件。", "Preview mode does not save files."); return; }
                else
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = T("保存为新的二维码图片", "Save a new QR image"), SuggestedFileName = "side-island-qr.png", DefaultExtension = "png", ShowOverwritePrompt = false,
                        FileTypeChoices = [new("PNG") { Patterns = ["*.png"] }]
                    });
                    destination = file?.TryGetLocalPath();
                    if (file is not null && destination is null && Current()) status.Text = T("请选择本机文件位置。", "Choose a local file location.");
                }
                if (destination is null || !Current()) return;
                if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("An absolute output path is required.");
                // UI rendering happens before yielding again. The worker receives immutable PNG bytes.
                using var bitmap = qr.Bitmap(); using var png = new MemoryStream(); bitmap.Save(png, PngBitmapEncoderOptions.Default);
                byte[] bytes = png.ToArray(); string target = Path.GetFullPath(destination);
                status.Text = T("正在保存…", "Saving…");
                if (integrations.SaveQrPng is { } writer) await writer(target, bytes, cancellation.Token);
                else await Task.Run(() => ScreenPinImageFiles.SaveNew(bytes, target, cancellation.Token), cancellation.Token);
                if (Current()) status.Text = T("PNG 已另存为新文件。", "PNG saved as a new file.");
            }
            catch (OperationCanceledException) { }
            catch (FilePublicationException error) when (error.State == FilePublicationState.OutcomeUnknown)
            {
                if (!testMode) DiagnosticLog.Write("qr-export-outcome-unknown", error);
                if (Current()) status.Text = FilePublicationFeedback.Unknown(error, prefs.Language);
                else if (!closing) Notice(T("二维码保存结果需核对", "Check the QR save result"), FilePublicationFeedback.Unknown(error, prefs.Language));
            }
            catch (IOException) { if (Current()) status.Text = T("未保存：目标已存在、不可写或不支持安全保存。请选择新文件名或其他位置。", "Not saved: the destination exists, is unwritable or does not support safe saving. Choose a new name or location."); }
            catch (Exception) { if (Current()) status.Text = T("无法保存图片，请检查位置和权限。", "Could not save the image. Check its location and permissions."); }
            finally { if (ReferenceEquals(pendingSave, cancellation)) pendingSave = null; saving = false; if (Available()) UpdateActions(); }
        }
        var generate = Button(T("生成二维码", "Generate QR"), Generate); generate.Name = "qr-generate";
        save = AsyncButton(T("保存 PNG", "Save PNG"), () => LatestQrSave = SaveQr(), CanSave); save.Name = "qr-save";
        input.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Invalidate(); };
        content.DetachedFromVisualTree += (_, _) => { detached = true; revision++; pendingSave?.Cancel(); qr.Code = null; UpdateActions(); };
        content.Children.Add(input); content.Children.Add(Actions(generate, save)); content.Children.Add(qr); content.Children.Add(status);
        content.Children.Add(Hint(T("单张使用标准二维码最大容量；中文和 emoji 的 UTF-8 字节会占更多容量。保存只创建新文件。", "Uses the standard single-code maximum capacity. UTF-8 Chinese and emoji consume more bytes. Saving creates a new file only.")));
        body.Children.Add(content);
    }
}
