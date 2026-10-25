using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class ReminderPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly ReminderStore store;
        private readonly ReminderBook book;
        private readonly DispatcherTimer pulse = new DispatcherTimer();
        private readonly TextBox title, date, time;
        private readonly CheckBox daily;
        private readonly TextBlock status, count;
        private readonly StackPanel list = new StackPanel();
        private string lastError;
        public event Action<string, string> OnNotify;
        public ReminderPanel(bool demoMode)
        {
            demo = demoMode;
            store = new ReminderStore();
            var initial = demo ? new List<ReminderItem>() : store.Load();
            book = new ReminderBook(initial, demo ? (Action<List<ReminderItem>>)(items => { }) : store.Save);
            Children.Add(ToolStyle.Heading("到点提醒，不必一直记着"));
            Children.Add(ToolStyle.Hint("应用运行时提醒；休眠或重启后，过期提醒只补一次。每日提醒按本地时间重复。"));
            var editor = new StackPanel();
            title = ToolStyle.Input(); title.MaxLength = ReminderStore.MaxTitleLength; title.ToolTip = "提醒标题（最多 80 字）"; editor.Children.Add(title);
            editor.Children.Add(ToolStyle.Hint("写好标题，选择多久以后提醒"));
            var quickButtons = new List<Button>();
            foreach (int minutes in new[] { 10, 30, 60 })
            {
                int value = minutes;
                var button = IslandWindow.Button(minutes + " 分钟后", () => Change(() => book.AddMinutes(title.Text, value), "已添加提醒"));
                quickButtons.Add(button);
            }
            editor.Children.Add(PanelActionLayout.Create(3, quickButtons.ToArray()));
            editor.Children.Add(ToolStyle.Hint("或指定日期与时间（24 小时制）"));
            var specific = new Grid(); specific.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) }); specific.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); specific.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 65 });
            DateTime suggested = DateTime.Now.AddMinutes(30);
            date = ToolStyle.Input(suggested.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); date.MaxLength = 10; date.ToolTip = "日期：yyyy-MM-dd"; specific.Children.Add(date);
            time = ToolStyle.Input(suggested.ToString("HH:mm", CultureInfo.InvariantCulture)); time.MaxLength = 5; time.ToolTip = "时间：HH:mm"; Grid.SetColumn(time, 2); specific.Children.Add(time); editor.Children.Add(specific);
            daily = new CheckBox { Content = "每天重复（从上述日期开始）", Foreground = Display.Brush("#B4CABB"), FontSize = 11, Margin = new Thickness(0, 6, 0, 6) }; editor.Children.Add(daily);
            editor.Children.Add(PanelActionLayout.Create(1, IslandWindow.Button("添加指定时间提醒", AddScheduled)));
            Children.Add(ToolStyle.Card(editor));
            status = ToolStyle.Hint(demo ? "演示模式 · 提醒不写入文件，也不弹出系统通知" : store.LoadFailed ? "部分提醒无法读取，原文件已保留。请先检查 reminders.json；新增内容会保存当前列表。" : "提醒保存在本机，关闭应用后不会触发。"); Children.Add(status);
            count = IslandWindow.Text("", 12, "#C1D8C9", true); count.TextWrapping = TextWrapping.Wrap; count.Margin = new Thickness(0, 0, 0, 6); Children.Add(count); Children.Add(list);
            Children.Add(ToolStyle.Hint("暂停后恢复：过期单次提醒改为 10 分钟后；每日提醒从下一个时刻继续。夏令时跳过的时刻会顺延，重复的时刻只提醒一次。"));
            if (demo)
            {
                book.AddMinutes("站起来活动一下", 10);
                book.AddLocal("结束今天的工作", DateTime.Now.AddDays(1).Date.AddHours(18), true);
            }
            pulse.Interval = TimeSpan.FromSeconds(1); pulse.Tick += (s, e) => Poll();
            Rebuild();
        }
        private void AddScheduled()
        {
            DateTime local;
            if (!DateTime.TryParseExact(date.Text.Trim() + " " + time.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) { SetStatus("请按 yyyy-MM-dd 和 HH:mm 输入日期时间，例如 2026-10-23、09:30。", true); return; }
            Change(() => book.AddLocal(title.Text, local, daily.IsChecked == true), "已添加提醒");
        }
        private void Change(Func<bool> action, string success)
        {
            try
            {
                bool saved = action();
                SetStatus(saved ? (demo ? "演示操作 · " : "") + success : book.LastError, !saved);
                Rebuild();
            }
            catch (ArgumentException error) { SetStatus(error.Message, true); }
        }
        private void Rebuild()
        {
            var entries = book.Items;
            count.Text = "提醒 " + entries.Count + " / 30 · 待提醒 " + entries.Count(x => x.Enabled && !x.Completed);
            list.Children.Clear();
            if (entries.Count == 0) { list.Children.Add(ToolStyle.Hint("还没有提醒。可以先为喝水、会议或休息设一个。")); return; }
            foreach (var item in entries.OrderBy(x => x.Completed ? 2 : x.Enabled ? 0 : 1).ThenBy(x => x.DueUtcTicks))
            {
                var row = new StackPanel();
                var heading = IslandWindow.Text(item.Title, 13, item.Enabled ? "#E2EFE6" : "#95A69C", true, false); heading.TextWrapping = TextWrapping.Wrap; row.Children.Add(heading);
                DateTime local = item.DueUtc.ToLocalTime();
                row.Children.Add(ToolStyle.Hint((item.Completed ? "已提醒" : item.Enabled ? "待提醒" : "已暂停") + " · " + (item.Daily ? "每天 " + item.Hour.ToString("00") + ":" + item.Minute.ToString("00") + "\n下次：" : "") + local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                var toggle = IslandWindow.Button(item.Completed ? "10 分钟后再提醒" : item.Enabled ? "暂停" : "恢复", () =>
                {
                    try { string explanation; bool saved = book.SetEnabled(item.Id, !item.Enabled, out explanation); SetStatus(saved ? explanation : book.LastError, !saved); Rebuild(); }
                    catch (ArgumentException error) { SetStatus(error.Message, true); }
                });
                var remove = IslandWindow.Button("删除", () => Change(() => book.Remove(item.Id), "已删除提醒")); row.Children.Add(PanelActionLayout.Create(2, toggle, remove)); list.Children.Add(ToolStyle.Card(row));
            }
        }
        private void Poll()
        {
            var due = book.Poll();
            if (due.Count > 0)
            {
                Rebuild(); SetStatus(demo ? "演示提醒已到点：" + due[0].Title : "提醒已到点；已更新下一次提醒状态。", false);
                if (!demo)
                {
                    var notify = OnNotify;
                    // One batch after a long absence avoids a burst of up to 30 separate popups.
                    if (notify != null) notify(due.Count == 1 ? "提醒到点" : due.Count + " 条提醒到点", String.Join("\n", due.Take(3).Select(x => x.Title)) + (due.Count > 3 ? "\n还有 " + (due.Count - 3) + " 条，请在提醒页查看。" : ""));
                }
            }
            if (book.LastError != lastError)
            {
                if (book.LastError != null) SetStatus(book.LastError, true);
                else if (lastError != null) SetStatus("已恢复保存。", false);
                lastError = book.LastError;
            }
        }
        private void SetStatus(string text, bool error) { status.Text = text ?? ""; status.Foreground = Display.Brush(error ? "#E9BA83" : "#8FA89A"); }
        public bool TryFlush() { bool success = book.TryFlush(); if (!success) SetStatus(book.LastError, true); return success; }
        public void Start() { pulse.Start(); Poll(); }
        public void Stop() { pulse.Stop(); }
        public void Dispose() { Stop(); TryFlush(); }
    }
}
