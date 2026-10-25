using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class NotificationTasksPanel : StackPanel, IDisposable
    {
        internal readonly NotificationTaskBook Book;
        private readonly TextBox search, project;
        private readonly TextBlock status;
        private readonly StackPanel list = new StackPanel(), projectEditor = new StackPanel();
        private readonly Border projectCard;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private string selectedId;
        private bool privacy, disposed;
        public event Action<string, string> OnNotify;
        internal NotificationTasksPanel(bool demo)
        {
            Book = new NotificationTaskBook(demo);
            Children.Add(ToolStyle.Heading("通知待办"));
            Children.Add(ToolStyle.Hint("右键通知卡片可转为待办、固定或稍后提醒。通知被清空后，已保存的待办仍保留。"));
            search = ToolStyle.Input(); search.MaxLength = 160; UiLocalization.ToolTip(search, "搜索标题、来源或项目"); search.TextChanged += (s, e) => Refresh(); Children.Add(search);
            projectEditor.Children.Add(ToolStyle.Hint("为选中的通知待办设置项目"));
            project = ToolStyle.Input(); project.MaxLength = 60; projectEditor.Children.Add(project);
            projectEditor.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("保存项目", () => Change(() => Book.SetProject(selectedId, project.Text))), IslandWindow.Button("关闭编辑", () => projectCard.Visibility = Visibility.Collapsed)));
            projectCard = ToolStyle.Card(projectEditor); projectCard.Visibility = Visibility.Collapsed; Children.Add(projectCard);
            Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            status = ToolStyle.Hint(""); Children.Add(status);
            timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (s, e) => Poll(); Refresh();
        }
        internal void Start() { if (!disposed) timer.Start(); }
        internal void SetPrivacy(bool value) { if (privacy == value) return; privacy = value; projectCard.Visibility = Visibility.Collapsed; search.Clear(); Refresh(); }
        internal NotificationTask Add(IslandNotification message, int minutes)
        {
            var item = Book.Add(message, minutes, DateTime.UtcNow); Refresh(); status.Text = minutes > 0 ? "已保存待办并设置稍后提醒。" : "已转为通知待办。"; return item;
        }
        internal void EditProject(string id)
        {
            if (disposed || privacy) return;
            var item = Book.Snapshot().FirstOrDefault(x => x.Id == id); if (item == null) return;
            selectedId = id; project.Text = item.Project; projectCard.Visibility = Visibility.Visible; project.Focus(); project.SelectAll();
        }
        internal void Refresh()
        {
            if (disposed || list == null) return;
            list.Children.Clear(); string query = search == null ? "" : search.Text.Trim();
            var all = Book.Snapshot(); var visible = all.Where(x => query.Length == 0 || (!privacy && (x.Title + " " + x.Source + " " + x.Project).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            foreach (var item in visible)
            {
                var content = new StackPanel();
                var title = UiLocalization.Protect(IslandWindow.Text(privacy ? UiLocalization.Localize("通知待办内容已隐藏") : (item.Pinned ? "★ " : "") + item.Title, 12, "#D7E8DE", !item.Done)); title.TextWrapping = TextWrapping.Wrap; content.Children.Add(title);
                var detail = UiLocalization.Protect(ToolStyle.Hint(privacy ? "" : item.Source + (item.Project.Length > 0 ? " · " + item.Project : ""))); detail.TextWrapping = TextWrapping.Wrap; detail.Visibility = privacy ? Visibility.Collapsed : Visibility.Visible; content.Children.Add(detail);
                if (item.DueUtcTicks > 0 && !item.Done) content.Children.Add(ToolStyle.Hint(UiLocalization.Localize(item.ReminderDelivered ? "已提醒" : "提醒时间") + " · " + new DateTime(item.DueUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM/dd HH:mm")));
                string id = item.Id; bool done = item.Done, pinned = item.Pinned;
                var complete = IslandWindow.Button(done ? "恢复待办" : "标记完成", () => Change(() => Book.SetDone(id, !done)));
                var pin = IslandWindow.Button(pinned ? "取消固定" : "固定", () => Change(() => Book.SetPinned(id, !pinned)));
                var edit = IslandWindow.Button("设置项目", () => EditProject(id)); edit.IsEnabled = !privacy;
                content.Children.Add(PanelActionLayout.Create(3, complete, pin, edit));
                var menu = new ContextMenu(); var remove = UiLocalization.Header(new MenuItem(), "移除待办"); remove.Click += (s, e) => Change(() => Book.Remove(id)); menu.Items.Add(remove);
                var card = ToolStyle.Card(content); card.ContextMenu = menu; list.Children.Add(card);
            }
            if (visible.Count == 0) list.Children.Add(ToolStyle.Hint("还没有匹配的通知待办。"));
            if (status != null) status.Text = UiLocalization.Format("通知待办 {0} 项 · 未完成 {1} 项", all.Count, all.Count(x => !x.Done));
        }
        private void Change(Action action)
        {
            try { action(); Refresh(); status.Text = "通知待办已保存。"; }
            catch (Exception error) { status.Text = UiLocalization.Localize("无法保存通知待办：") + error.Message; }
        }
        private void Poll()
        {
            if (disposed) return;
            try
            {
                var due = Book.TakeDue(DateTime.UtcNow); if (due.Count == 0) return;
                Refresh(); var notify = OnNotify;
                if (notify != null) notify(privacy ? UiLocalization.Localize("通知待办到时间了") : due[0].Title, privacy ? UiLocalization.Localize("打开通知待办查看。") : due[0].Source + (due.Count > 1 ? " · " + due.Count : ""));
            }
            catch (Exception) { status.Text = "通知待办提醒暂时无法保存，请检查数据目录。"; }
        }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); }
    }
}
