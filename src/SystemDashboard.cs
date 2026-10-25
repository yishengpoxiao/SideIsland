using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class DeviceSnapshot
    {
        public TimeSpan? Uptime;
        public int? ProcessCount;
        public string Drive = "系统盘";
        public long? DriveAvailableBytes;
        public long? DriveTotalBytes;
    }

    internal static class DeviceStatsReader
    {
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static DeviceSnapshot Read()
        {
            var result = new DeviceSnapshot();
            try { result.Uptime = TimeSpan.FromMilliseconds(GetTickCount64()); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            try
            {
                Process[] processes = Process.GetProcesses();
                result.ProcessCount = processes.Length;
                foreach (var process in processes) process.Dispose();
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory);
                var drive = new DriveInfo(root);
                result.Drive = root.TrimEnd('\\') + " 系统盘";
                if (drive.IsReady)
                {
                    result.DriveTotalBytes = drive.TotalSize;
                    result.DriveAvailableBytes = drive.AvailableFreeSpace;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return result;
        }
    }

    internal sealed class SystemDashboard : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly NetworkStatsReader networkReader = new NetworkStatsReader();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly TextBlock download, upload, connection, addresses, clock, uptime, processCount, disk, diskHint;
        private readonly ProgressBar diskBar;
        private readonly NetworkHistory graph;
        private bool running, busy, disposed, privacy;
        private int generation;
        private NetworkSnapshot latestNetwork;
        private DeviceSnapshot latestDevice;
        private double latestDeviceTime = -30;
        private bool resetBaseline = true;

        public SystemDashboard(bool demoMode)
        {
            demo = demoMode;
            Margin = new Thickness(0, 6, 0, 0);
            Children.Add(Label("网络与设备", 11, "#93A69B"));
            var networkCard = new StackPanel();
            connection = Label("正在读取网络状态…", 11, "#A1EACF");
            networkCard.Children.Add(connection);
            var rates = new Grid { Margin = new Thickness(0, 8, 0, 3) };
            rates.ColumnDefinitions.Add(new ColumnDefinition()); rates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); rates.ColumnDefinitions.Add(new ColumnDefinition());
            var downPanel = new StackPanel(); var upPanel = new StackPanel();
            downPanel.Children.Add(Label("↓ 下载", 10, "#95A69E"));
            upPanel.Children.Add(Label("↑ 上传", 10, "#95A69E"));
            download = Label("采样中", 18, "#A1EACF"); upload = Label("采样中", 18, "#C5B7F8");
            downPanel.Children.Add(download); upPanel.Children.Add(upload);
            rates.Children.Add(downPanel); Grid.SetColumn(upPanel, 2); rates.Children.Add(upPanel); networkCard.Children.Add(rates);
            graph = new NetworkHistory { Height = 42, Margin = new Thickness(0, 5, 0, 6) }; networkCard.Children.Add(graph);
            networkCard.Children.Add(Label("最近 30 次采样 · 物理网卡合计", 9, "#647A6E"));
            addresses = Label("本机 IPv4 · 读取中", 10, "#84998D"); addresses.Margin = new Thickness(0, 8, 0, 0); networkCard.Children.Add(addresses);
            Children.Add(Card(networkCard));

            var deviceCard = new StackPanel();
            clock = Label("", 13, "#E5EEE8"); clock.Margin = new Thickness(0, 0, 0, 8); deviceCard.Children.Add(clock);
            uptime = Row(deviceCard, "开机时长", "读取中");
            processCount = Row(deviceCard, "运行进程", "读取中");
            disk = Label("系统盘", 11, "#D6E3DA"); disk.Margin = new Thickness(0, 9, 0, 5); deviceCard.Children.Add(disk);
            diskBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, BorderThickness = new Thickness(0), Background = Display.Brush("#2E3C34"), Foreground = Display.Brush("#A1EACF") }; deviceCard.Children.Add(diskBar);
            diskHint = Label("读取中", 10, "#84998D"); diskHint.Margin = new Thickness(0, 6, 0, 0); deviceCard.Children.Add(diskHint);
            Children.Add(Card(deviceCard));
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += OnTick;
        }

        public void SetPrivacy(bool enabled)
        {
            privacy = enabled;
            UpdateAddresses();
        }

        public void Start()
        {
            if (disposed || running) return;
            running = true;
            generation++;
            resetBaseline = true;
            timer.Start();
            Refresh();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            generation++;
            timer.Stop();
        }

        private void OnTick(object sender, EventArgs e) { Refresh(); }

        private async void Refresh()
        {
            if (!running || disposed || busy) return;
            clock.Text = DateTime.Now.ToString(UiLocalization.IsEnglish ? "MMM dd, dddd   HH:mm:ss" : "MM 月 dd 日  dddd   HH:mm:ss", UiLocalization.Culture);
            busy = true;
            int request = generation;
            bool reset = resetBaseline;
            resetBaseline = false;
            try
            {
                var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                if (demo)
                {
                    latestNetwork = new NetworkSnapshot { Available = true, ConnectedAdapters = 1, AdapterNames = "演示 Wi-Fi", LocalAddresses = "192.168.1.8", Rates = new NetworkRates { DownloadBytesPerSecond = 2.4 * 1024 * 1024, UploadBytesPerSecond = 168 * 1024 } };
                    latestDevice = new DeviceSnapshot { Uptime = TimeSpan.FromHours(5.7), ProcessCount = 186, Drive = "C: 系统盘", DriveAvailableBytes = 186L * 1024 * 1024 * 1024, DriveTotalBytes = 512L * 1024 * 1024 * 1024 };
                }
                else
                {
                    bool refreshDevice = latestDevice == null || now - latestDeviceTime >= 10;
                    DeviceSnapshot device = null;
                    NetworkSnapshot network = await Task.Run(delegate
                    {
                        if (reset) networkReader.Reset();
                        NetworkSnapshot item = networkReader.Read((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
                        if (refreshDevice) device = DeviceStatsReader.Read();
                        return item;
                    });
                    if (disposed || !running || generation != request) return;
                    latestNetwork = network;
                    if (device != null) { latestDevice = device; latestDeviceTime = now; }
                }
                Apply();
            }
            catch (Exception)
            {
                if (!disposed && running && generation == request)
                {
                    connection.Text = "暂时无法读取设备信息";
                    download.Text = upload.Text = "—";
                }
            }
            finally
            {
                busy = false;
                if (!disposed && running && generation != request) Refresh();
            }
        }

        private void Apply()
        {
            if (latestNetwork != null)
            {
                connection.Text = !latestNetwork.Available ? "网络状态暂不可用" : latestNetwork.ConnectedAdapters == 0 ? (latestNetwork.Partial ? "部分网卡状态不可用" : "没有已连接的物理网卡") : "● 已连接网络" + (latestNetwork.ConnectedAdapters > 1 ? " · " + latestNetwork.ConnectedAdapters + " 个网卡" : "") + (latestNetwork.Partial ? " · 部分数据" : "");
                download.Text = SystemFormats.Rate(latestNetwork.Rates.DownloadBytesPerSecond);
                upload.Text = SystemFormats.Rate(latestNetwork.Rates.UploadBytesPerSecond);
                if (!latestNetwork.Available) download.Text = upload.Text = "—";
                graph.Add(latestNetwork.Rates.DownloadBytesPerSecond, latestNetwork.Rates.UploadBytesPerSecond);
                UpdateAddresses();
            }
            if (latestDevice != null)
            {
                uptime.Text = latestDevice.Uptime.HasValue ? SystemFormats.Uptime(latestDevice.Uptime.Value) : "—";
                processCount.Text = latestDevice.ProcessCount.HasValue ? latestDevice.ProcessCount.Value + " 个" : "—";
                disk.Text = latestDevice.Drive;
                bool available = latestDevice.DriveAvailableBytes.HasValue && latestDevice.DriveTotalBytes.HasValue && latestDevice.DriveTotalBytes > 0;
                double percent = available ? 100.0 * latestDevice.DriveAvailableBytes.Value / latestDevice.DriveTotalBytes.Value : 0;
                diskBar.Value = Math.Max(0, Math.Min(100, 100 - percent));
                diskBar.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                diskBar.Foreground = Display.Brush(percent < 10 ? "#FFBE83" : "#A1EACF");
                diskHint.Text = available ? "可用 " + SystemFormats.Bytes(latestDevice.DriveAvailableBytes.Value) + " / 共 " + SystemFormats.Bytes(latestDevice.DriveTotalBytes.Value) : "磁盘信息暂不可用";
            }
        }

        private void UpdateAddresses()
        {
            if (latestNetwork == null) return;
            addresses.Text = privacy ? "本机 IPv4 · 隐私模式已隐藏" : "本机 IPv4 · " + (String.IsNullOrEmpty(latestNetwork.LocalAddresses) ? "暂无" : latestNetwork.LocalAddresses);
            var tip = new StackPanel { MaxWidth = 320 };
            if (!privacy && !String.IsNullOrEmpty(latestNetwork.AdapterNames))
            {
                var names = IslandWindow.Text(latestNetwork.AdapterNames, 12, "#D6E3DA", false, false);
                names.TextWrapping = TextWrapping.Wrap; names.LineHeight = 16; names.Margin = new Thickness(0, 0, 0, 6); tip.Children.Add(names);
            }
            tip.Children.Add(Label("物理网卡链路状态；不测试互联网连接。", 11, "#84998D")); connection.ToolTip = tip;
        }

        private static Border Card(UIElement child) { var card = ToolStyle.Card(child); card.Margin = new Thickness(0, 8, 0, 0); return card; }
        private static TextBlock Label(string text, double size, string color) { return UiLocalization.Text(new TextBlock { Text = text, FontSize = size, Foreground = Display.Brush(color), TextWrapping = TextWrapping.Wrap }, text); }

        private static TextBlock Row(Panel parent, string caption, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Label(caption, 11, "#84998D"));
            var result = Label(value, 11, "#D6E3DA"); result.TextAlignment = TextAlignment.Right; Grid.SetColumn(result, 2); row.Children.Add(result); parent.Children.Add(row);
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop(); disposed = true;
            timer.Tick -= OnTick;
        }
    }

    internal sealed class NetworkHistory : FrameworkElement
    {
        private readonly List<double?> downloads = new List<double?>();
        private readonly List<double?> uploads = new List<double?>();

        public void Add(double? download, double? upload)
        {
            downloads.Add(download); uploads.Add(upload);
            if (downloads.Count > 30) { downloads.RemoveAt(0); uploads.RemoveAt(0); }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawLine(new Pen(Display.Brush("#2B3B32"), 1), new Point(0, ActualHeight - 1), new Point(ActualWidth, ActualHeight - 1));
            double peak = 1024;
            foreach (double? value in downloads) if (value.HasValue) peak = Math.Max(peak, value.Value);
            foreach (double? value in uploads) if (value.HasValue) peak = Math.Max(peak, value.Value);
            Draw(dc, downloads, peak, "#A1EACF"); Draw(dc, uploads, peak, "#C5B7F8");
        }

        private void Draw(DrawingContext dc, IList<double?> values, double peak, string color)
        {
            var pen = new Pen(Display.Brush(color), 1.5);
            for (int i = 1; i < values.Count; i++)
                if (values[i - 1].HasValue && values[i].HasValue)
                    dc.DrawLine(pen, new Point((30 - values.Count + i - 1) * ActualWidth / 29, ActualHeight - 2 - values[i - 1].Value / peak * (ActualHeight - 4)), new Point((30 - values.Count + i) * ActualWidth / 29, ActualHeight - 2 - values[i].Value / peak * (ActualHeight - 4)));
        }
    }
}
