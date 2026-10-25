using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class LanTransferPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly LanTransferService server = new LanTransferService();
        private readonly List<string> selected = new List<string>();
        private readonly TextBlock files = ToolStyle.Hint("尚未选择文件"), status = ToolStyle.Hint("分享未启动。"), progress = ToolStyle.Hint(""), networkHint = ToolStyle.Hint("" );
        private readonly ComboBox network = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 3, 0, 6), Padding = new Thickness(7, 5, 7, 5) };
        private readonly TextBox minutes = ToolStyle.Input("15"), link = ToolStyle.Input();
        private readonly PasswordBox passcode = new PasswordBox { MinHeight = 32, MaxLength = 32, Margin = new Thickness(0, 3, 0, 6), Padding = new Thickness(10, 6, 10, 6) };
        private readonly Image qr = new Image { MaxWidth = 236, MaxHeight = 236, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Border qrFrame;
        private readonly Button choose, clear, start, stop, copyLink, copyQr, saveQr, refreshNetworks;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private BitmapSource currentQr;
        private bool disposed, active;
        private bool previouslyRunning;
        public event Action<TaskActivityUpdate> ActivityChanged;
        internal LanTransferPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("局域网传文件"));
            Children.Add(ToolStyle.Hint("选文件后开始临时 HTTP 分享。手机与电脑连接同一网络，扫码打开；不会上传到外部服务器。"));
            choose = IslandWindow.Button("选择文件", Choose); clear = IslandWindow.Button("清空选择", Clear);
            Children.Add(PanelActionLayout.Create(2, choose, clear)); Children.Add(files); UiLocalization.Protect(files);
            Children.Add(ToolStyle.Hint("网络地址")); Children.Add(network);
            var networkCaption = new FrameworkElementFactory(typeof(TextBlock)); networkCaption.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); networkCaption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); network.ItemTemplate = new DataTemplate { VisualTree = networkCaption };
            refreshNetworks = IslandWindow.Button("刷新网络地址", RefreshNetworks); Children.Add(PanelActionLayout.Create(1, refreshNetworks)); Children.Add(networkHint);
            network.SelectionChanged += (s, e) => { var item = network.SelectedItem as LanTransferAddress; networkHint.Text = item != null && System.Net.IPAddress.IsLoopback(item.Address) ? "该地址仅供本机访问，手机请选局域网地址。" : "系统防火墙可能要求你允许此应用访问专用网络；应用不会自动修改防火墙。"; };
            Children.Add(ToolStyle.Hint("有效时长（1—120 分钟）")); minutes.MaxLength = 3; Children.Add(minutes);
            Children.Add(ToolStyle.Hint("访问口令（可选，最多 32 字符）")); Children.Add(passcode);
            Children.Add(ToolStyle.Hint("口令在手机页面输入，不放进二维码；分享停止或到期后，链接和当前下载均会结束。"));
            start = IslandWindow.Button("开始分享", Start); stop = IslandWindow.Button("停止分享", Stop);
            Children.Add(PanelActionLayout.Create(2, start, stop)); Children.Add(status); Children.Add(progress);
            link.IsReadOnly = true; link.TextWrapping = TextWrapping.Wrap; link.MinHeight = 32; link.MaxHeight = 84; link.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; link.Visibility = Visibility.Collapsed; Children.Add(link);
            qrFrame = new Border { Child = qr, CornerRadius = new CornerRadius(12), Background = Brushes.White, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            UiTheme.ProtectColor(qrFrame); UiLocalization.Protect(qrFrame); Children.Add(qrFrame);
            copyLink = IslandWindow.Button("复制链接", CopyLink); copyQr = IslandWindow.Button("复制二维码", CopyQr); saveQr = IslandWindow.Button("保存二维码", SaveQr);
            Children.Add(PanelActionLayout.Create(3, copyLink, copyQr, saveQr));
            Children.Add(ToolStyle.Hint("最多 20 个文件，每个不超过 20 GB。只提供所选文件的下载，不开放文件夹或上传入口。"));
            timer.Tick += (s, e) => RefreshStatus();
            server.ActivityChanged += OnActivity;
            AllowDrop = true;
            PreviewDragOver += (s, e) => { e.Handled = true; e.Effects = !demo && !disposed && !server.Snapshot().Running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            PreviewDrop += (s, e) => { e.Handled = true; if (demo || disposed || server.Snapshot().Running) return; try { var paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths != null) SetFiles(paths); } catch (Exception error) { status.Text = Friendly(error); } };
            if (demo)
            {
                network.Items.Add(new LanTransferAddress { Address = System.Net.IPAddress.Loopback, Name = "Wi-Fi · 192.168.1.20" }); network.SelectedIndex = 0;
                files.Text = "example-document.pdf · 2.4 MB\nimage-with-a-long-file-name.png · 640 KB"; status.Text = "演示模式不启动网络分享或读取本机文件。";
            }
            else RefreshNetworks();
            UpdateControls(false);
        }
        public void SetActive(bool value)
        {
            if (disposed) return; active = value;
            if (value) { timer.Start(); RefreshStatus(); } else timer.Stop();
        }
        private void OnActivity(LanTransferProgress update)
        {
            if (disposed || update == null) return;
            Action publish = delegate {
                if (disposed) return;
                var callback = ActivityChanged;
                if (callback != null) callback(new TaskActivityUpdate { Id = update.Id, Title = update.Title, State = update.State, Message = update.Message, Source = "局域网传输", Progress = update.Progress });
            };
            try { if (Dispatcher.CheckAccess()) publish(); else Dispatcher.BeginInvoke(publish); } catch (InvalidOperationException) { }
        }
        private void RefreshNetworks()
        {
            if (disposed || server.Snapshot().Running) return;
            var current = network.SelectedItem as LanTransferAddress; string previous = current == null ? "" : current.Address.ToString();
            network.Items.Clear(); foreach (var item in LanTransferService.Addresses()) network.Items.Add(item);
            network.SelectedItem = network.Items.Cast<LanTransferAddress>().FirstOrDefault(item => item.Address.ToString() == previous) ?? network.Items.Cast<LanTransferAddress>().FirstOrDefault();
        }
        private void Choose()
        {
            if (demo || server.Snapshot().Running) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLocalization.Localize("选择要分享的文件"), Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) try { SetFiles(dialog.FileNames); } catch (Exception error) { status.Text = Friendly(error); }
        }
        internal void SetFiles(IEnumerable<string> paths)
        {
            if (disposed || demo || server.Snapshot().Running) return;
            var prepared = LanTransferService.PrepareFiles(paths);
            selected.Clear(); selected.AddRange(prepared.Select(item => item.Path));
            files.Text = String.Join("\n", prepared.Select(item => item.Name + " · " + LanTransferService.FormatBytes(item.Length)));
            status.Text = "文件已选择，点击开始后才允许下载。"; UpdateControls(false);
        }
        private void Clear()
        {
            if (server.Snapshot().Running) return; selected.Clear(); files.Text = UiLocalization.Localize("尚未选择文件"); status.Text = "分享未启动。"; UpdateControls(false);
        }
        private void Start()
        {
            if (disposed || demo || server.Snapshot().Running) return;
            int duration; if (!Int32.TryParse(minutes.Text, NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration < 1 || duration > 120) { status.Text = "分享时长为 1—120 分钟。"; return; }
            var address = network.SelectedItem as LanTransferAddress; if (address == null) { status.Text = "请选择有效的本机 IPv4 地址。"; return; }
            try
            {
                string url = server.Start(selected, address.Address, duration, passcode.Password);
                currentQr = QrTransferTools.Render(QrTransferTools.Encode(url)); qr.Source = currentQr; qrFrame.Visibility = Visibility.Visible; link.Text = url; link.Visibility = Visibility.Visible;
                previouslyRunning = true; status.Text = "分享已启动，手机扫码后可下载。"; UpdateControls(true); if (active) timer.Start(); RefreshStatus();
            }
            catch (Exception error) { server.Stop(); ClearLink(); status.Text = Friendly(error); UpdateControls(false); }
        }
        private void Stop()
        {
            server.Stop(); previouslyRunning = false; ClearLink(); status.Text = "分享已停止，链接已失效。"; progress.Text = ""; UpdateControls(false);
        }
        private void RefreshStatus()
        {
            if (disposed || demo) return;
            var state = server.Snapshot();
            if (!state.Running)
            {
                if (previouslyRunning) { status.Text = "分享已到期，链接已失效。"; previouslyRunning = false; ClearLink(); }
                UpdateControls(false); return;
            }
            TimeSpan remaining = state.ExpiresUtc - DateTime.UtcNow;
            progress.Text = "剩余：" + Math.Max(0, (int)remaining.TotalMinutes).ToString("D2", CultureInfo.InvariantCulture) + ":" + Math.Max(0, remaining.Seconds).ToString("D2", CultureInfo.InvariantCulture) + " · 已发送：" + LanTransferService.FormatBytes(state.SentBytes) + " · 完成下载：" + state.CompletedDownloads;
            UpdateControls(true);
        }
        private void UpdateControls(bool running)
        {
            choose.IsEnabled = clear.IsEnabled = refreshNetworks.IsEnabled = network.IsEnabled = minutes.IsEnabled = passcode.IsEnabled = !running && !demo;
            start.IsEnabled = !demo && !running && selected.Count > 0; stop.IsEnabled = !demo && running;
            copyLink.IsEnabled = copyQr.IsEnabled = saveQr.IsEnabled = !demo && running && currentQr != null;
        }
        private void CopyLink() { try { if (server.Snapshot().Running) { System.Windows.Clipboard.SetText(link.Text); status.Text = "链接已复制。"; } } catch { status.Text = "剪贴板暂时被占用，请重试。"; } }
        private void CopyQr() { try { if (server.Snapshot().Running && currentQr != null) { System.Windows.Clipboard.SetImage(currentQr); status.Text = "二维码已复制。"; } } catch { status.Text = "剪贴板暂时被占用，请重试。"; } }
        private void SaveQr()
        {
            if (!server.Snapshot().Running || currentQr == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("保存分享二维码"), Filter = UiLocalization.FileFilter("PNG 图片|*.png"), DefaultExt = ".png", AddExtension = true, FileName = "share-qr.png" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) try { QrTransferTools.Save(currentQr, dialog.FileName); status.Text = "二维码已保存。"; } catch { status.Text = "二维码保存失败，请选择可写的位置。"; }
        }
        private void ClearLink() { currentQr = null; qr.Source = null; qrFrame.Visibility = Visibility.Collapsed; link.Text = ""; link.Visibility = Visibility.Collapsed; }
        private static string Friendly(Exception error)
        {
            return error is ArgumentException ? error.Message : error is System.Net.Sockets.SocketException ? "无法监听所选地址，网络可能已断开或端口被占用，请刷新地址后重试。" : error is UnauthorizedAccessException ? "无法读取所选文件，请选择可访问的本机文件。" : "分享未能启动，请检查文件和网络地址。";
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; timer.Stop(); server.ActivityChanged -= OnActivity; server.Dispose(); selected.Clear(); ClearLink();
        }
    }
}
