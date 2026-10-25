using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class KeepAwakePanel : StackPanel, IDisposable
    {
        private readonly KeepAwakeSession session;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly TextBlock clock, status;
        private readonly Button stop;
        private readonly bool demo;
        private string previousStatus;
        private bool disposed;
        private int quickMinutes = 30;
        public bool IsRunning { get { return session.IsRunning; } }
        internal int QuickMinutes { get { return quickMinutes; } }
        internal void ToggleQuick() { if (IsRunning) Stop(); else Begin(quickMinutes); }
        public string CompactStatus { get { return IsRunning ? "唤醒 " + Format(session.Remaining) : "未保持唤醒"; } }
        public event Action StatusChanged;
        public KeepAwakePanel(bool demoMode) : this(demoMode, KeepAwakeSession.Create(demoMode)) { }
        internal KeepAwakePanel(bool demoMode, KeepAwakeSession model)
        {
            demo = demoMode; session = model;
            Children.Add(ToolStyle.Heading("临时保持唤醒"));
            Children.Add(ToolStyle.Hint("下载、任务运行或演示时，临时防止电脑因空闲自动睡眠。到期或退出灵动岛时自动释放。"));
            var presetButtons = new Button[3];
            int column = 0;
            foreach (int minutes in new[] { 30, 60, 120 })
            {
                int selected = minutes;
                presetButtons[column++] = IslandWindow.Button(minutes + " 分钟", () => Begin(selected));
            }
            var presets = PanelActionLayout.Create(3, presetButtons); presets.Margin = new Thickness(0, 6, 0, 8);
            Children.Add(presets);
            var card = new StackPanel();
            clock = IslandWindow.Text("未开启", 34, "#A1EACF", true); clock.HorizontalAlignment = HorizontalAlignment.Center; clock.TextWrapping = TextWrapping.Wrap; clock.TextAlignment = TextAlignment.Center; clock.Margin = new Thickness(0, 4, 0, 8); card.Children.Add(clock);
            stop = IslandWindow.Button("停止保持唤醒", Stop); card.Children.Add(PanelActionLayout.Create(1, stop)); Children.Add(ToolStyle.Card(card));
            status = ToolStyle.Hint(demo ? "演示模式 · 倒计时可试用，不改变电脑唤醒状态。" : "尚未开启。再次选择时长会从现在重新计时。"); Children.Add(status);
            Children.Add(ToolStyle.Hint("允许屏幕按原设置关闭。手动睡眠、合盖和电量不足的系统操作仍由 Windows 决定。"));
            pulse.Interval = TimeSpan.FromSeconds(1); pulse.Tick += (s, e) => Tick(); Refresh();
        }
        private void Begin(int minutes)
        {
            if (disposed) return;
            try { session.Start(minutes); quickMinutes = minutes; status.Text = (demo ? "演示 · " : "") + "已开启 " + minutes + " 分钟，到期自动停止。"; pulse.Start(); Refresh(); }
            catch (Exception error) { if (!(error is Win32Exception) && !(error is ArgumentException)) throw; status.Text = error.Message; Refresh(); }
        }
        private void Stop()
        {
            if (disposed) return;
            try { session.Stop(); status.Text = "已停止，恢复 Windows 原来的自动睡眠行为。"; pulse.Stop(); Refresh(); }
            catch (Win32Exception error) { status.Text = error.Message; }
        }
        private void Tick()
        {
            if (disposed) return;
            try { if (session.Poll()) { status.Text = "时间已到，已恢复 Windows 原来的自动睡眠行为。"; pulse.Stop(); } }
            catch (Win32Exception error) { status.Text = error.Message; }
            Refresh();
        }
        private void Refresh()
        {
            clock.Text = IsRunning ? Format(session.Remaining) : "未开启"; stop.IsEnabled = IsRunning;
            string next = CompactStatus;
            if (next != previousStatus) { previousStatus = next; var changed = StatusChanged; if (changed != null) changed(); }
        }
        private static string Format(TimeSpan remaining) { int seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)); return seconds >= 3600 ? (seconds / 3600) + ":" + ((seconds / 60) % 60).ToString("00") + ":" + (seconds % 60).ToString("00") : (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00"); }
        public void Start() { if (!disposed && IsRunning) pulse.Start(); }
        public void Dispose()
        {
            Dispatcher.VerifyAccess(); if (disposed) return;
            pulse.Stop(); session.Dispose(); disposed = true;
        }
    }
}
