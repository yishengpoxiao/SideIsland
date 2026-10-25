using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class FocusPanel : StackPanel, IDisposable
    {
        private readonly FocusTimer timer;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly TextBlock clock, state, summary, mode;
        private readonly TextBox customMinutes;
        private readonly Button toggle, lapButton, focusPreset, applyButton;
        private readonly StackPanel laps;
        private int lapCount;
        private bool started, presentationActive, disposed;
        private string previousStatus, validationError;
        public event Action<string, string> OnNotify;
        public event Action StatusChanged;
        public event Action<int> FocusMinutesChanged;
        public event Action FocusCompleted;
        public int FocusMinutes { get; private set; }
        public bool IsRunning { get { return timer.IsRunning; } }
        public string CompactStatus { get { return timer.Label + " " + FocusTimer.Format(timer.Value, false); } }
        public string ActiveTimerLabel { get { return timer.Label; } }
        public string QuickActionText { get { return timer.IsRunning ? "暂停" : timer.IsCompleted ? "再来一次" : "开始 / 继续"; } }

        public FocusPanel(bool demo) : this(demo, 25) { }
        public FocusPanel(bool demo, int preferredFocusMinutes) : this(demo, preferredFocusMinutes, new FocusTimer()) { }
        internal FocusPanel(bool demo, int preferredFocusMinutes, FocusTimer timer)
        {
            if (timer == null) throw new ArgumentNullException("timer");
            this.timer = timer;
            FocusMinutes = preferredFocusMinutes >= 1 && preferredFocusMinutes <= 1440 ? preferredFocusMinutes : 25;
            timer.SetCountdown(TimeSpan.FromMinutes(FocusMinutes), "专注", true);
            Children.Add(IslandWindow.Text("专注与计时", 16, "#EEF7F3", true));
            var description = IslandWindow.Text("收起灵动岛或切换页面，计时仍会继续。", 10, "#91A29B");
            description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0, 6, 0, 8); Children.Add(description);
            focusPreset = IslandWindow.Button("专注 " + FocusMinutes + " 分", () => SelectCountdown(FocusMinutes, "专注", true));
            focusPreset.FontSize = 10;
            var presets = PanelActionLayout.Create(3, focusPreset, Preset("休息 5 分", 5, "休息", false), Preset("长休 15 分", 15, "长休", false));
            Children.Add(presets);
            var customLabel = IslandWindow.Text("专注时长（分钟）", 11, "#BBCCC2", true);
            customLabel.Margin = new Thickness(0, 8, 0, 6); Children.Add(customLabel);
            var custom = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            custom.ColumnDefinitions.Add(new ColumnDefinition());
            customMinutes = new TextBox { Text = FocusMinutes.ToString(CultureInfo.InvariantCulture), MaxLength = 4, FontSize = 12, Padding = new Thickness(7, 5, 7, 5), Background = Display.Brush("#1D2827"), Foreground = Display.Brush("#E0ECE5"), BorderBrush = Display.Brush("#384843"), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinHeight = 32 };
            UiLocalization.ToolTip(customMinutes, "1—1440 分钟，按 Enter 保存并重置为专注");
            custom.Children.Add(customMinutes);
            applyButton = IslandWindow.Button("保存并重置", ApplyCustom);
            var stopwatch = IslandWindow.Button("秒表", () => { validationError = null; timer.SetStopwatch(); ClearLaps(); Refresh(); });
            var customActions = PanelActionLayout.Create(2, applyButton, stopwatch); Grid.SetColumn(customActions, 2); custom.Children.Add(customActions);
            customMinutes.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplyCustom(); e.Handled = true; } };
            Children.Add(custom);
            var customHint = IslandWindow.Text("1—1440 分钟；保存会重置当前计时，不会自动开始。", 10, "#91A29B");
            customHint.TextWrapping = TextWrapping.Wrap; customHint.Margin = new Thickness(0, 0, 0, 8); Children.Add(customHint);
            var card = new StackPanel();
            mode = IslandWindow.Text("专注", 12, "#A1EACF", true); mode.HorizontalAlignment = HorizontalAlignment.Center; card.Children.Add(mode);
            clock = IslandWindow.Text("25:00", 45, "#EFF8F3", true); clock.HorizontalAlignment = HorizontalAlignment.Center; clock.Margin = new Thickness(0, 5, 0, 4); card.Children.Add(clock);
            state = IslandWindow.Text("准备开始", 11, "#91A29B"); state.HorizontalAlignment = HorizontalAlignment.Center; state.TextWrapping = TextWrapping.Wrap; state.TextAlignment = TextAlignment.Center; card.Children.Add(state);
            toggle = IslandWindow.Button("开始", ToggleQuickFocus); toggle.Background = Display.Brush("#31594B");
            var reset = IslandWindow.Button("重置", () => { validationError = null; timer.Reset(); ClearLaps(); Refresh(); });
            lapButton = IslandWindow.Button("计次", AddLap);
            var controls = PanelActionLayout.Create(3, toggle, reset, lapButton); controls.Margin = new Thickness(0, 10, 0, 0);
            card.Children.Add(controls);
            var timerCard = ToolStyle.Card(card); timerCard.Padding = new Thickness(12); timerCard.Margin = new Thickness(0); Children.Add(timerCard);
            summary = IslandWindow.Text("本次运行已完成 0 个专注", 11, "#91A29B"); summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0, 8, 0, 6); Children.Add(summary);
            laps = new StackPanel(); Children.Add(laps);
            var tip = IslandWindow.Text("收起后点击计时组件，可开始、暂停或继续当前计时。\n倒计时包含电脑休眠时间；到点后提醒一次。\n切换预设会重置当前计时。退出应用会结束计时。", 10, "#6F837A"); tip.TextWrapping = TextWrapping.Wrap; tip.Margin = new Thickness(0, 10, 0, 0); Children.Add(tip);
            if (demo) { var marker = IslandWindow.Text("演示模式 · 可试用计时", 10, "#CFB1F7"); marker.Margin = new Thickness(0, 10, 0, 0); Children.Add(marker); }
            timer.Completed += () => { if (timer.IsFocusSession) { var completed = FocusCompleted; if (completed != null) completed(); } var notify = OnNotify; if (notify != null) notify(timer.Label + "结束", timer.Label == "专注" ? "这一轮专注已完成，起来活动一下吧。" : "时间到了，准备开始下一件事吧。"); };
            pulse.Interval = TimeSpan.FromMilliseconds(1000);
            pulse.Tick += (s, e) => { timer.Poll(); Refresh(); };
            Refresh();
        }

        private Button Preset(string text, int minutes, string label, bool focus)
        {
            var button = IslandWindow.Button(text, () => SelectCountdown(minutes, label, focus));
            button.FontSize = 10; return button;
        }

        private void SelectCountdown(int minutes, string label, bool focus)
        {
            validationError = null; timer.SetCountdown(TimeSpan.FromMinutes(minutes), label, focus); ClearLaps(); Refresh();
        }

        public void ToggleQuickFocus()
        {
            validationError = null;
            timer.Poll();
            if (timer.IsRunning) timer.Pause(); else timer.Start();
            Refresh();
        }
        internal void StartFocus(int minutes)
        {
            if (minutes < 1 || minutes > 1440) throw new ArgumentOutOfRangeException("minutes");
            customMinutes.Text = minutes.ToString(CultureInfo.InvariantCulture); ApplyCustom(); timer.Start(); Refresh();
        }

        private void ApplyCustom()
        {
            int minutes;
            if (!Int32.TryParse(customMinutes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) || minutes < 1 || minutes > 1440)
            {
                validationError = "请输入 1—1440 分钟"; Refresh(); customMinutes.Focus(); return;
            }
            bool changed = FocusMinutes != minutes;
            FocusMinutes = minutes;
            customMinutes.Text = minutes.ToString(CultureInfo.InvariantCulture);
            UiLocalization.Content(focusPreset, "专注 " + minutes + " 分");
            SelectCountdown(minutes, "专注", true);
            if (changed) { var handler = FocusMinutesChanged; if (handler != null) handler(minutes); }
        }

        private void ClearLaps() { lapCount = 0; laps.Children.Clear(); }
        private void AddLap()
        {
            if (!timer.IsStopwatch || !timer.IsRunning) return;
            lapCount++;
            var item = IslandWindow.Text("第 " + lapCount + " 次        " + FocusTimer.Format(timer.Value, true), 12, "#BBCCC2"); item.Margin = new Thickness(0, 0, 0, 6); laps.Children.Insert(0, item);
            if (laps.Children.Count > 10) laps.Children.RemoveAt(10);
        }

        private void Refresh()
        {
            clock.Text = FocusTimer.Format(timer.Value, timer.IsStopwatch);
            clock.FontSize = clock.Text.Length > 7 ? 34 : 45;
            mode.Text = timer.Label;
            toggle.Content = QuickActionText;
            lapButton.IsEnabled = timer.IsStopwatch && timer.IsRunning; lapButton.Opacity = lapButton.IsEnabled ? 1 : .4;
            state.Text = validationError ?? (timer.IsCompleted ? "已完成" : timer.IsRunning ? "进行中" : "就绪 / 已暂停");
            state.Foreground = Display.Brush(validationError != null ? "#E9BA83" : timer.IsCompleted ? "#A1EACF" : "#91A29B");
            summary.Text = "本次运行已完成 " + timer.CompletedFocusSessions + " 个专注" + (timer.IsStopwatch ? " · 保留最近 10 次计次" : "");
            string status = CompactStatus + ":" + timer.IsRunning;
            if (status != previousStatus) { previousStatus = status; var changed = StatusChanged; if (changed != null) changed(); }
            UpdatePulse();
        }

        internal void SetPresentationActive(bool active) { presentationActive = active; UpdatePulse(); }
        private void UpdatePulse()
        {
            pulse.Interval = TimeSpan.FromMilliseconds(presentationActive ? 200 : 1000);
            if (started && !disposed && timer.IsRunning) pulse.Start(); else pulse.Stop();
        }
        public void Start() { if (disposed) return; started = true; UpdatePulse(); }
        public void Stop() { started = false; pulse.Stop(); }
        public void Dispose() { disposed = true; Stop(); }
    }
}
