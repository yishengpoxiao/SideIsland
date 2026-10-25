using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexIsland
{
    internal sealed class LogWatchPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly TextBox path, keywords, search, output;
        private readonly CheckBox follow;
        private readonly Button toggle;
        private readonly TextBlock status;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private LogWatchReader reader;
        private LogWatchSnapshot latest;
        private string activePath;
        private bool disposed, monitoring, reading, started, changing;
        private int generation;
        public event Action<string, string> OnNotify;
        internal LogWatchSnapshot Latest { get { return latest; } }
        public LogWatchPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("日志实时追踪"));
            Children.Add(ToolStyle.Hint("选择日志后点击开始。首次读取末尾 64 KB，随后每秒检查新增内容；收起后继续监测，退出应用即停止。"));
            var card = new StackPanel();
            card.Children.Add(ToolStyle.Hint("日志文件完整路径（可以尚不存在）")); path = ToolStyle.Input(); path.MaxLength = 32700; card.Children.Add(path);
            var browse = PanelActionLayout.Create(1, IslandWindow.Button("选择日志文件", Browse)); browse.Margin = new Thickness(0, 6, 0, 0); card.Children.Add(browse);
            card.Children.Add(ToolStyle.Hint("提醒关键词（逗号分隔，留空仅查看；每 10 秒最多提醒一次）")); keywords = ToolStyle.Input("error, exception, failed, 错误"); keywords.MaxLength = 2000; card.Children.Add(keywords);
            toggle = IslandWindow.Button("开始追踪", Toggle);
            var actions = PanelActionLayout.Create(2, toggle, IslandWindow.Button("更新提醒关键词", ApplyKeywords)); actions.Margin = new Thickness(0, 6, 0, 0); card.Children.Add(actions); Children.Add(ToolStyle.Card(card));
            Children.Add(ToolStyle.Hint("搜索当前日志（不影响关键词提醒）"));
            search = ToolStyle.Input(); search.MaxLength = 256; search.ToolTip = UiLocalization.Localize("搜索当前日志（不影响关键词提醒）"); search.TextChanged += (s, e) => Render(); Children.Add(search);
            follow = new CheckBox { Content = "自动滚动到最新", IsChecked = true, Foreground = Display.Brush("#E3EFE7"), Margin = new Thickness(0, 6, 0, 6) }; UiLocalization.Content(follow, "自动滚动到最新"); Children.Add(follow);
            output = ToolStyle.Input(); output.IsReadOnly = true; output.AcceptsReturn = true; output.TextWrapping = TextWrapping.NoWrap; output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; output.MinHeight = 140; output.MaxHeight = 320; output.FontFamily = new System.Windows.Media.FontFamily("Consolas"); Children.Add(output);
            var clear = PanelActionLayout.Create(1, IslandWindow.Button("清空当前视图", Clear)); clear.Margin = new Thickness(0, 6, 0, 0); Children.Add(clear);
            status = ToolStyle.Hint("尚未开始追踪，不会自动读取文件。"); Children.Add(status);
            Children.Add(ToolStyle.Hint("支持 UTF-8，以及带 BOM 的 UTF-16 / UTF-32。最多显示 1000 行、512 KB 字符，单行最多 4000 字。文件轮换、截断或暂时不可读时会自动恢复；不会修改日志原文件。"));
            timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += async (s, e) => await PollAsync();
        }
        private void Browse()
        {
            var dialog = new OpenFileDialog { Title = UiLocalization.Localize("选择日志文件"), Filter = UiLocalization.FileFilter("日志与文本|*.log;*.txt;*.out;*.jsonl|所有文件|*.*"), CheckFileExists = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) path.Text = dialog.FileName;
        }
        public void Start() { if (disposed) return; started = true; if (monitoring) timer.Start(); }
        private async void Toggle()
        {
            if (disposed || changing) return;
            if (monitoring) { monitoring = false; timer.Stop(); generation++; toggle.Content = "继续追踪"; status.Text = "追踪已暂停。"; return; }
            try
            {
                string target = path.Text.Trim().Trim('"'), terms = keywords.Text;
                if (reader == null || !String.Equals(activePath, target, StringComparison.OrdinalIgnoreCase))
                {
                    var next = new LogWatchReader(target); next.SetKeywords(terms);
                    LogWatchReader previous = reader; reader = next; activePath = target; latest = null; output.Text = "";
                    DisposeLater(previous);
                }
                else
                {
                    changing = true; var current = reader; await Task.Run(() => current.SetKeywords(terms)); if (disposed) return;
                }
                monitoring = true; generation++; toggle.Content = "暂停追踪"; if (started) timer.Start(); await PollAsync();
            }
            catch (Exception error) { if (!disposed) status.Text = "无法开始追踪：" + error.Message; }
            finally { changing = false; }
        }
        private async void ApplyKeywords()
        {
            if (disposed || changing) return;
            var current = reader; if (current == null) { status.Text = "开始追踪时会使用这些关键词。"; return; }
            changing = true; string terms = keywords.Text;
            try { await Task.Run(() => current.SetKeywords(terms)); if (!disposed && current == reader) status.Text = "提醒关键词已更新。"; }
            catch (Exception error) { if (!disposed) status.Text = error.Message; }
            finally { changing = false; }
        }
        internal async Task PollAsync()
        {
            if (disposed || !monitoring || !started || reading || reader == null) return;
            reading = true; int request = generation; var current = reader;
            try
            {
                var result = await Task.Run(() => current.Read());
                if (disposed || request != generation || !monitoring || current != reader) return;
                latest = result; status.Text = result.Status; Render();
                if (!demo && !String.IsNullOrEmpty(result.Alert)) { var notify = OnNotify; if (notify != null) notify("日志关键词提醒", System.IO.Path.GetFileName(result.Path) + "\n" + result.Alert); }
            }
            catch (Exception error) { if (!disposed && request == generation) status.Text = "读取日志失败：" + error.Message; }
            finally { reading = false; }
        }
        private async void Clear()
        {
            if (disposed || reader == null || changing) return;
            changing = true; generation++; var current = reader;
            try { await Task.Run(() => current.Clear()); if (!disposed && current == reader) { latest = null; output.Text = ""; status.Text = "当前视图已清空，原文件未修改。"; } }
            finally { changing = false; }
        }
        private void Render()
        {
            if (disposed || output == null || latest == null) return;
            string query = search.Text; string value = String.Join(Environment.NewLine, latest.Lines.Where(x => query.Length == 0 || x.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            if (output.Text != value) { output.Text = value; if (follow.IsChecked == true) output.ScrollToEnd(); }
        }
        public void Stop() { started = false; generation++; timer.Stop(); }
        private static void DisposeLater(LogWatchReader previous) { if (previous != null) Task.Run(() => previous.Dispose()); }
        public void Dispose()
        {
            if (disposed) return; Stop(); disposed = true; monitoring = false;
            var previous = reader; reader = null; if (previous != null) Task.Run(() => previous.Dispose()); latest = null; output.Text = "";
        }
    }
}
