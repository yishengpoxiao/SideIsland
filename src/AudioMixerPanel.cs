using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class AudioMixerPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly ComboBox devices = new ComboBox { Margin = new Thickness(0, 3, 0, 6), Padding = new Thickness(7, 5, 7, 5), MinHeight = 32 };
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint(""), defaultLabel = ToolStyle.Hint(""), empty = ToolStyle.Hint("播放音乐或视频后，对应应用会出现在这里。" );
        private readonly Button refresh, switchOutput;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly Dictionary<string, SessionRow> controls = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
        private bool disposed, active, reading, controlling, rendering;
        private int generation;
        private string selectedDevice = "", deviceKey = "";
        private sealed class SessionRow
        {
            internal AudioMixerSession Model;
            internal Slider Volume;
            internal TextBlock Level;
            internal Button Apply, Mute;
            internal bool Dirty, Rendering;
        }
        internal AudioMixerPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("分应用音量"));
            Children.Add(ToolStyle.Hint("分别控制所选输出设备上的应用会话。只有点击应用或静音按钮才会改变声音。"));
            Children.Add(ToolStyle.Hint("输出设备")); Children.Add(devices); Children.Add(defaultLabel);
            var deviceCaption = new FrameworkElementFactory(typeof(TextBlock)); deviceCaption.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding()); deviceCaption.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); devices.ItemTemplate = new DataTemplate { VisualTree = deviceCaption };
            refresh = IslandWindow.Button("刷新会话", async () => await RefreshAsync());
            switchOutput = IslandWindow.Button("设为默认输出", async () => await SetDefaultAsync());
            Children.Add(PanelActionLayout.Create(2, refresh, switchOutput));
            Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("Windows 声音设置", OpenSoundSettings)));
            Children.Add(status); Children.Add(empty); Children.Add(rows);
            devices.SelectionChanged += async (s, e) => { if (rendering || disposed) return; var device = devices.SelectedItem as AudioMixerDevice; selectedDevice = device == null ? "" : device.Id; generation++; await RefreshAsync(); };
            timer.Tick += async (s, e) => await RefreshAsync();
            if (demo) RenderSnapshot(DemoSnapshot());
        }
        public void SetActive(bool value)
        {
            if (disposed || active == value) return; active = value; generation++;
            if (value) { timer.Start(); var ignored = RefreshAsync(); } else timer.Stop();
        }
        private async Task RefreshAsync()
        {
            if (disposed || reading || controlling) return;
            if (demo) { RenderSnapshot(DemoSnapshot()); return; }
            reading = true; int revision = generation; string selected = selectedDevice;
            try
            {
                var snapshot = await Task.Run(() => AudioMixerService.Read(selected));
                if (!disposed && revision == generation) RenderSnapshot(snapshot);
            }
            catch (Exception) { if (!disposed && revision == generation) status.Text = "音量会话读取失败，请稍后刷新。"; }
            finally { reading = false; }
        }
        internal void RenderSnapshot(AudioMixerSnapshot snapshot)
        {
            if (disposed || snapshot == null) return;
            rendering = true;
            try
            {
                string nextDeviceKey = String.Join("\n", snapshot.Devices.Select(item => item.Id + "\t" + item.Name));
                if (deviceKey != nextDeviceKey)
                {
                    devices.Items.Clear(); foreach (var device in snapshot.Devices) devices.Items.Add(device); deviceKey = nextDeviceKey;
                }
                selectedDevice = snapshot.SelectedDeviceId;
                devices.SelectedItem = devices.Items.Cast<AudioMixerDevice>().FirstOrDefault(item => item.Id == selectedDevice);
                var current = snapshot.Devices.FirstOrDefault(item => item.Id == snapshot.DefaultDeviceId);
                defaultLabel.Text = current == null ? "未发现可用的输出设备。" : "系统默认：" + current.Name;
                switchOutput.IsEnabled = !demo && !controlling && devices.SelectedItem != null && selectedDevice != snapshot.DefaultDeviceId;
                if (demo) status.Text = "演示模式不修改系统音量或默认设备。";
                else if (!String.IsNullOrEmpty(snapshot.Error)) status.Text = snapshot.Error;
                var keys = new HashSet<string>(snapshot.Sessions.Select(item => item.Id), StringComparer.Ordinal);
                foreach (var key in controls.Keys.Where(key => !keys.Contains(key)).ToArray()) controls.Remove(key);
                if (rows.Children.Count != snapshot.Sessions.Length || snapshot.Sessions.Where((item, index) => index >= rows.Children.Count || !Object.Equals(rows.Children[index].GetValue(FrameworkElement.TagProperty), item.Id)).Any())
                {
                    rows.Children.Clear();
                    foreach (var session in snapshot.Sessions)
                    {
                        SessionRow row;
                        if (!controls.TryGetValue(session.Id, out row)) controls[session.Id] = row = BuildRow(session);
                        var element = (FrameworkElement)row.Volume.Tag; element.Tag = session.Id; rows.Children.Add(element);
                    }
                }
                foreach (var session in snapshot.Sessions)
                {
                    SessionRow row = controls[session.Id]; row.Model = session;
                    row.Rendering = true; if (!row.Dirty) row.Volume.Value = session.Volume;
                    row.Level.Text = Math.Round(row.Volume.Value) + "%";
                    row.Mute.Content = session.Muted ? "取消静音" : "静音";
                    row.Apply.IsEnabled = !demo && row.Dirty && !controlling; row.Mute.IsEnabled = !demo && !controlling;
                    row.Rendering = false;
                }
                empty.Visibility = snapshot.Sessions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { rendering = false; }
        }
        private SessionRow BuildRow(AudioMixerSession session)
        {
            var row = new SessionRow { Model = session };
            var content = new StackPanel();
            var name = ToolStyle.Hint(session.SystemSounds ? "系统声音" : session.Name, session.SystemSounds); name.FontWeight = FontWeights.SemiBold; content.Children.Add(name);
            content.Children.Add(ToolStyle.Hint("PID " + session.ProcessId, false));
            var line = new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            row.Volume = new Slider { Minimum = 0, Maximum = 100, Value = session.Volume, Margin = new Thickness(0, 3, 8, 6), IsSnapToTickEnabled = true, TickFrequency = 1 };
            row.Level = ToolStyle.Hint(Math.Round(session.Volume) + "%", false); row.Level.TextAlignment = TextAlignment.Right; Grid.SetColumn(row.Level, 1);
            line.Children.Add(row.Volume); line.Children.Add(row.Level); content.Children.Add(line);
            row.Apply = IslandWindow.Button("应用", async () => await ChangeAsync(row, false)); row.Mute = IslandWindow.Button("静音", async () => await ChangeAsync(row, true));
            content.Children.Add(PanelActionLayout.Create(2, row.Apply, row.Mute));
            var card = ToolStyle.Card(content); card.Padding = new Thickness(10); row.Volume.Tag = card;
            row.Volume.ValueChanged += (s, e) => { row.Level.Text = Math.Round(row.Volume.Value) + "%"; if (!row.Rendering) row.Dirty = true; row.Apply.IsEnabled = !demo && row.Dirty && !controlling; };
            return row;
        }
        private async Task ChangeAsync(SessionRow row, bool mute)
        {
            if (disposed || demo || controlling || row.Model == null) return;
            controlling = true; generation++; var model = row.Model; double requested = row.Volume.Value; status.Text = "正在应用声音设置…";
            try
            {
                await Task.Run(() => AudioMixerService.ChangeSession(model.DeviceId, model.Id, mute ? (double?)null : requested, mute ? (bool?)!model.Muted : null));
                if (!disposed) { row.Dirty = false; status.Text = "声音设置已应用。"; }
            }
            catch (Exception error) { if (!disposed) status.Text = error is InvalidOperationException ? error.Message : "声音设置未能应用，原会话可能已结束，请刷新。"; }
            finally { controlling = false; }
            if (!disposed) await RefreshAfterCommand();
        }
        private async Task SetDefaultAsync()
        {
            var device = devices.SelectedItem as AudioMixerDevice;
            if (disposed || demo || controlling || device == null) return;
            controlling = true; generation++; switchOutput.IsEnabled = false; status.Text = "正在切换默认输出…";
            try { await Task.Run(() => AudioMixerService.SwitchDefaultOutput(device.Id)); if (!disposed) status.Text = "默认输出已切换。已有应用可能需要重新开始播放。"; }
            catch (Exception error) { if (!disposed) status.Text = error.Message; selectedDevice = ""; }
            finally { controlling = false; }
            if (!disposed) await RefreshAfterCommand();
        }
        private async Task RefreshAfterCommand() { for (int attempt = 0; reading && !disposed && attempt < 40; attempt++) await Task.Delay(25); if (!disposed) await RefreshAsync(); }
        private void OpenSoundSettings()
        {
            if (demo) { status.Text = "演示模式不打开系统设置。"; return; }
            try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { status.Text = "无法打开 Windows 声音设置。"; }
        }
        internal static AudioMixerSnapshot DemoSnapshot()
        {
            return new AudioMixerSnapshot { Devices = new[] { new AudioMixerDevice { Id = "demo-speakers", Name = "Speakers / 扬声器", IsDefault = true }, new AudioMixerDevice { Id = "demo-headphones", Name = "Bluetooth headphones / 蓝牙耳机" } }, DefaultDeviceId = "demo-speakers", SelectedDeviceId = "demo-speakers", Sessions = new[] { new AudioMixerSession { Id = "demo-music", DeviceId = "demo-speakers", Name = "Music player / 音乐播放器", ProcessId = 1234, Volume = 45, Active = true }, new AudioMixerSession { Id = "demo-browser", DeviceId = "demo-speakers", Name = "Browser with a longer application name / 浏览器", ProcessId = 5678, Volume = 70 } } };
        }
        public void Dispose() { if (disposed) return; disposed = true; generation++; timer.Stop(); rows.Children.Clear(); controls.Clear(); }
    }
}
