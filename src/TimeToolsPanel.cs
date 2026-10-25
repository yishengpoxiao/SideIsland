using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class TimeToolsPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly ComboBox mode = Choice("世界时钟", "日期间隔", "日期加减", "Unix 时间戳");
        private readonly StackPanel clockPage = new StackPanel(), differencePage = new StackPanel(), addPage = new StackPanel(), unixPage = new StackPanel();
        private readonly Dictionary<TextBlock, TimeZoneInfo> clocks = new Dictionary<TextBlock, TimeZoneInfo>();
        private readonly Grid clockGrid = new Grid();
        private readonly List<Border> clockCards = new List<Border>();
        private int clockColumns;
        private readonly TextBox startDate = ToolStyle.Input(), endDate = ToolStyle.Input(), baseDate = ToolStyle.Input(), addAmount = ToolStyle.Input("7");
        private readonly CheckBox includeStart = Check("两端都计入（含起始日和结束日）"), differenceWeekdays = Check("只计周一至周五"), addWeekdays = Check("只计周一至周五");
        private readonly ComboBox unixDirection = Choice("时间戳 → 时间", "时间 → 时间戳"), unixUnit = Choice("秒（s）", "毫秒（ms）"), sourceZone = Choice("UTC", "本地时区");
        private readonly TextBox unixInput = ToolStyle.Input("0"), timeInput = ToolStyle.Input("1970-01-01 00:00:00");
        private readonly StackPanel timestampInputGroup = new StackPanel(), dateTimeInputGroup = new StackPanel();
        private readonly TextBlock result = IslandWindow.Text("", 18, "#A1EACF", true), status = ToolStyle.Hint("");
        private readonly Button copy;
        private readonly DispatcherTimer timer;
        private string copyValue;
        private bool disposed;
        public TimeToolsPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("跨时区，也算得明白"));
            Children.Add(mode);
            BuildClocks(); BuildDifference(); BuildAddition(); BuildUnix();
            Children.Add(clockPage); Children.Add(differencePage); Children.Add(addPage); Children.Add(unixPage);
            result.TextWrapping = TextWrapping.Wrap; result.Margin = new Thickness(0, 8, 0, 6); Children.Add(result);
            copy = PanelActionLayout.Caption(IslandWindow.Button("复制结果", Copy)); Children.Add(copy); Children.Add(status);
            var today = demo ? new DateTime(2026, 10, 22) : DateTime.Today;
            startDate.Text = baseDate.Text = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); endDate.Text = today.AddDays(today < DateTime.MaxValue.AddDays(-7) ? 7 : 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var input in new[] { startDate, endDate, baseDate, addAmount, unixInput, timeInput }) { input.MaxLength = 32; input.TextChanged += (s, e) => Calculate(); }
            includeStart.Checked += (s, e) => Calculate(); includeStart.Unchecked += (s, e) => Calculate();
            differenceWeekdays.Checked += (s, e) => Calculate(); differenceWeekdays.Unchecked += (s, e) => Calculate();
            addWeekdays.Checked += (s, e) => Calculate(); addWeekdays.Unchecked += (s, e) => Calculate();
            unixDirection.SelectionChanged += (s, e) => { UpdateUnixDirection(); Calculate(); };
            unixUnit.SelectionChanged += (s, e) => Calculate(); sourceZone.SelectionChanged += (s, e) => Calculate();
            mode.SelectionChanged += (s, e) => SelectMode();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += Tick;
            SelectMode();
        }
        private static ComboBox Choice(params string[] values)
        {
            var control = new ComboBox { FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string value in values) control.Items.Add(value); control.SelectedIndex = 0; return control;
        }
        private static CheckBox Check(string text)
        {
            var label = ToolStyle.Hint(text); label.Margin = new Thickness(0); return new CheckBox { Content = label, Margin = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
        }
        private void BuildClocks()
        {
            clockPage.Children.Add(ToolStyle.Hint("按 Windows 时区规则自动处理夏令时；仅面板可见时刷新。"));
            AddClock("本地", TimeZoneInfo.Local); AddClock("北京", TimeTools.FindZone("China Standard Time")); AddClock("东京", TimeTools.FindZone("Tokyo Standard Time"));
            AddClock("伦敦", TimeTools.FindZone("GMT Standard Time")); AddClock("纽约", TimeTools.FindZone("Eastern Standard Time")); AddClock("UTC", TimeZoneInfo.Utc);
            clockPage.Children.Add(clockGrid); ArrangeClocks(1);
            clockGrid.SizeChanged += (s, e) => ArrangeClocks(e.NewSize.Width >= 760 ? 3 : e.NewSize.Width >= 480 ? 2 : 1);
        }
        private void AddClock(string name, TimeZoneInfo zone)
        {
            var row = new StackPanel(); row.Children.Add(IslandWindow.Text(name, 12, "#C1D8C9", true));
            var value = IslandWindow.Text("", 13, "#E3EFE7"); value.TextWrapping = TextWrapping.Wrap; value.Margin = new Thickness(0, 2, 0, 0); row.Children.Add(value);
            var border = ToolStyle.Card(row); border.Padding = new Thickness(10, 7, 10, 7); border.Margin = new Thickness(0); clockGrid.Children.Add(border); clockCards.Add(border); clocks.Add(value, zone);
        }
        private void ArrangeClocks(int columns)
        {
            if (clockColumns == columns) return; clockColumns = columns; clockGrid.ColumnDefinitions.Clear(); clockGrid.RowDefinitions.Clear();
            for (int column = 0; column < columns; column++) { if (column > 0) clockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); clockGrid.ColumnDefinitions.Add(new ColumnDefinition()); }
            int rows = (clockCards.Count + columns - 1) / columns;
            for (int row = 0; row < rows; row++) { if (row > 0) clockGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); clockGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            for (int i = 0; i < clockCards.Count; i++) { Grid.SetColumn(clockCards[i], (i % columns) * 2); Grid.SetRow(clockCards[i], (i / columns) * 2); }
        }
        private void BuildDifference()
        {
            differencePage.Children.Add(ToolStyle.Hint("开始日期 · yyyy-MM-dd")); differencePage.Children.Add(startDate);
            differencePage.Children.Add(ToolStyle.Hint("结束日期 · yyyy-MM-dd")); differencePage.Children.Add(endDate);
            var swap = PanelActionLayout.Caption(IslandWindow.Button("交换日期", () => { string previous = startDate.Text; startDate.Text = endDate.Text; endDate.Text = previous; })); swap.Margin = new Thickness(0, 6, 0, 0); differencePage.Children.Add(swap);
            differencePage.Children.Add(includeStart); differencePage.Children.Add(differenceWeekdays);
            differencePage.Children.Add(ToolStyle.Hint("默认不计起始日、计入结束日；同一天默认算 0 天。周一至周五计算不扣除法定节假日，也不计周末调休。"));
        }
        private void BuildAddition()
        {
            addPage.Children.Add(ToolStyle.Hint("起始日期 · yyyy-MM-dd")); addPage.Children.Add(baseDate);
            addPage.Children.Add(ToolStyle.Hint("加减天数 · 正数向后，负数向前")); addPage.Children.Add(addAmount); addPage.Children.Add(addWeekdays);
            addPage.Children.Add(ToolStyle.Hint("起始日不计为第 1 天；输入 0 保持原日期。只计周一至周五时，不扣除法定节假日，也不计周末调休。"));
        }
        private void BuildUnix()
        {
            unixPage.Children.Add(unixDirection); unixPage.Children.Add(ToolStyle.Hint("时间戳单位（不会自动猜测）")); unixPage.Children.Add(unixUnit);
            timestampInputGroup.Children.Add(ToolStyle.Hint("Unix 时间戳 · 整数，可为负数")); timestampInputGroup.Children.Add(unixInput); unixPage.Children.Add(timestampInputGroup);
            dateTimeInputGroup.Children.Add(ToolStyle.Hint("日期时间 · yyyy-MM-dd HH:mm:ss[.fff]")); dateTimeInputGroup.Children.Add(timeInput);
            dateTimeInputGroup.Children.Add(ToolStyle.Hint("以上输入使用的时区")); dateTimeInputGroup.Children.Add(sourceZone); unixPage.Children.Add(dateTimeInputGroup);
            var now = PanelActionLayout.Caption(IslandWindow.Button("填入当前时间", FillNow)); now.Margin = new Thickness(0, 6, 0, 0); unixPage.Children.Add(now);
            unixPage.Children.Add(ToolStyle.Hint("Unix 起点为 1970-01-01 00:00:00 UTC。秒结果向下取整；本地夏令时不存在或重复的时间会提示错误。")); UpdateUnixDirection();
        }
        private void FillNow()
        {
            var now = demo ? new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero) : DateTimeOffset.UtcNow;
            if (unixDirection.SelectedIndex == 0) unixInput.Text = TimeTools.ToUnix(now, unixUnit.SelectedIndex == 1).ToString(CultureInfo.InvariantCulture);
            else timeInput.Text = TimeZoneInfo.ConvertTime(now, sourceZone.SelectedIndex == 0 ? TimeZoneInfo.Utc : TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        private void UpdateUnixDirection()
        {
            timestampInputGroup.Visibility = unixDirection.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            dateTimeInputGroup.Visibility = unixDirection.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        private void SelectMode()
        {
            clockPage.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            differencePage.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            addPage.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            unixPage.Visibility = mode.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            result.Visibility = copy.Visibility = mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateClocks(); Calculate();
        }
        private void Calculate()
        {
            copyValue = null; result.Text = ""; status.Text = ""; if (copy != null) copy.IsEnabled = false;
            try
            {
                switch (mode.SelectedIndex)
                {
                    case 1:
                        int days = TimeTools.DaysBetween(TimeTools.ParseDate(startDate.Text), TimeTools.ParseDate(endDate.Text), includeStart.IsChecked == true, differenceWeekdays.IsChecked == true);
                        copyValue = days.ToString(CultureInfo.InvariantCulture) + " 天";
                        status.Text = includeStart.IsChecked == true ? "两端都计入" : "不计起始日、计入结束日";
                        if (differenceWeekdays.IsChecked == true) status.Text += " · 只计周一至周五";
                        break;
                    case 2:
                        var date = TimeTools.AddDays(TimeTools.ParseDate(baseDate.Text), TimeTools.ParseDays(addAmount.Text), addWeekdays.IsChecked == true);
                        copyValue = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); status.Text = date.ToString("dddd", UiLocalization.Culture); break;
                    case 3:
                        bool ms = unixUnit.SelectedIndex == 1;
                        if (unixDirection.SelectedIndex == 0)
                        {
                            var instant = TimeTools.FromUnix(unixInput.Text, ms);
                            copyValue = "UTC：" + TimeTools.FormatInstant(instant, TimeZoneInfo.Utc) + "\n本地：" + TimeTools.FormatInstant(instant, TimeZoneInfo.Local);
                            status.Text = "本地时区：" + TimeZoneInfo.Local.DisplayName;
                        }
                        else
                        {
                            var instant = TimeTools.ToUtc(TimeTools.ParseTime(timeInput.Text), sourceZone.SelectedIndex == 0 ? TimeZoneInfo.Utc : TimeZoneInfo.Local);
                            copyValue = TimeTools.ToUnix(instant, ms).ToString(CultureInfo.InvariantCulture);
                            status.Text = (ms ? "毫秒 · " : "秒 · ") + TimeTools.FormatInstant(instant, TimeZoneInfo.Utc);
                        }
                        break;
                }
                result.Text = copyValue ?? ""; if (copy != null) copy.IsEnabled = copyValue != null;
            }
            catch (ArgumentException error) { result.Text = "—"; status.Text = error.Message; }
        }
        private void Copy()
        {
            if (copyValue == null) { status.Text = "请先输入有效内容。"; return; }
            if (demo) { status.Text = "演示模式不写入剪贴板。"; return; }
            try { Clipboard.SetText(copyValue); status.Text = "已复制结果"; } catch { status.Text = "剪贴板正忙，请稍后重试。"; }
        }
        private void Tick(object sender, EventArgs args) { if (mode.SelectedIndex == 0) UpdateClocks(); }
        private void UpdateClocks()
        {
            var now = demo ? new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero) : DateTimeOffset.UtcNow;
            foreach (var entry in clocks)
            {
                entry.Key.Text = entry.Value == null ? "此设备未提供该时区" : TimeZoneInfo.ConvertTime(now, entry.Value).ToString("MM-dd  HH:mm:ss", CultureInfo.InvariantCulture) + "  " + TimeTools.FormatOffset(entry.Value.GetUtcOffset(now));
            }
        }
        public void Start() { if (disposed) return; UpdateClocks(); if (!timer.IsEnabled) timer.Start(); }
        public void Stop() { timer.Stop(); }
        public void Dispose() { if (disposed) return; disposed = true; Stop(); timer.Tick -= Tick; }
    }
}
