using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class TextDiffPanel : StackPanel, IDisposable
    {
        private readonly TextBox left = ToolStyle.Input(), right = ToolStyle.Input();
        private readonly RichTextBox leftPreview = Preview(), rightPreview = Preview();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private CancellationTokenSource pending;
        private int revision;
        private bool disposed, synchronizingScroll;
        internal TextDiffResult LastResult { get; private set; }
        internal void AcceptExternalText(string text)
        {
            if (disposed) return;
            if ((text ?? "").Length > TextDiff.MaximumCharacters) throw new ArgumentException("文字超过对照工具的长度限制。");
            if (String.IsNullOrEmpty(left.Text)) left.Text = text ?? ""; else right.Text = text ?? "";
        }
        public TextDiffPanel(bool demo)
        {
            Children.Add(ToolStyle.Heading("文字对照"));
            Children.Add(ToolStyle.Hint("左边放原文，右边放新文本。自动按行对照：绿色新增、红色删除、橙色修改。文本仅在本次运行内存中处理。"));
            left.AcceptsReturn = right.AcceptsReturn = true; left.AcceptsTab = right.AcceptsTab = true;
            left.VerticalContentAlignment = right.VerticalContentAlignment = VerticalAlignment.Top;
            left.TextWrapping = right.TextWrapping = TextWrapping.NoWrap;
            left.Height = right.Height = 170; left.VerticalScrollBarVisibility = right.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            left.HorizontalScrollBarVisibility = right.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            left.MaxLength = right.MaxLength = TextDiff.MaximumCharacters + 1;
            left.FontFamily = right.FontFamily = new FontFamily("Consolas");
            Children.Add(Pair(Labeled("原文", left), Labeled("新文本", right)));
            var actions = PanelActionLayout.Create(2, IslandWindow.Button("交换左右", () => { string previous = left.Text; left.Text = right.Text; right.Text = previous; }), IslandWindow.Button("清空文本", () => { left.Clear(); right.Clear(); }));
            actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions); Children.Add(status);
            Children.Add(Pair(Labeled("原文差异", leftPreview), Labeled("新文本差异", rightPreview)));
            leftPreview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => SyncScroll(leftPreview, rightPreview, e)));
            rightPreview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => SyncScroll(rightPreview, leftPreview, e)));
            debounce.Tick += async (s, e) => { debounce.Stop(); await CompareNowAsync(); };
            left.TextChanged += (s, e) => Schedule(); right.TextChanged += (s, e) => Schedule();
            if (demo) { left.Text = "# 服务配置\nport=3000\nmode=dev\n"; right.Text = "# 服务配置\nport=8080\nmode=dev\nlog=true\n"; }
            else status.Text = "每侧最多 120,000 字符、1,600 行；预览前 600 行，每行最多 2,000 字符，摘要统计完整文本。";
        }
        private static FrameworkElement Labeled(string title, UIElement content) { var panel = new StackPanel(); panel.Children.Add(ToolStyle.Hint(title)); panel.Children.Add(content); return panel; }
        private static Grid Pair(UIElement a, UIElement b)
        {
            var grid = new Grid(); grid.Children.Add(a); grid.Children.Add(b);
            int columns = 0;
            Action<int> arrange = next => {
                if (columns == next) return; columns = next; grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                if (next == 2)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                    Grid.SetRow(b, 0); Grid.SetColumn(b, 2);
                }
                else
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(b, 2); Grid.SetColumn(b, 0);
                }
            };
            arrange(2); grid.SizeChanged += (s, e) => arrange(e.NewSize.Width < 480 ? 1 : 2); return grid;
        }
        private static RichTextBox Preview() { return new RichTextBox { IsReadOnly = true, IsDocumentEnabled = false, Height = 220, FontFamily = new FontFamily("Consolas"), FontSize = 11, Background = Display.Brush("#111A16"), Foreground = Display.Brush("#D4E6DC"), BorderBrush = Display.Brush("#3A5145"), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4), Document = Document() }; }
        private static FlowDocument Document() { return new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = 100000, PageWidth = 100000, FontFamily = new FontFamily("Consolas"), FontSize = 11, LineHeight = 17 }; }
        private void Schedule()
        {
            if (disposed) return; revision++; if (pending != null) pending.Cancel(); debounce.Stop(); LastResult = null;
            leftPreview.Document = Document(); rightPreview.Document = Document(); status.Text = "正在等待输入完成…"; debounce.Start();
        }
        internal async Task CompareNowAsync()
        {
            if (disposed) return; debounce.Stop(); int requested = ++revision;
            if (pending != null) pending.Cancel(); var cancellation = new CancellationTokenSource(); pending = cancellation;
            string a = left.Text, b = right.Text; status.Text = "正在对照…";
            try
            {
                TextDiffResult result = await Task.Run(() => TextDiff.Compare(a, b, cancellation.Token));
                if (disposed || revision != requested || cancellation.IsCancellationRequested) return;
                LastResult = result; Render(result);
                status.Text = result.Identical ? "两侧文本一致（忽略换行符格式）。" : String.Format("新增 {0} 行 · 删除 {1} 行 · 修改 {2} 行", result.Added, result.Removed, result.Modified);
                if (result.Rows.Count > 600) status.Text += " · 完整差异已统计，预览仅显示前 600 行。";
                if (result.Rows.Take(600).Any(row => (row.Left ?? "").Length > 2000 || (row.Right ?? "").Length > 2000)) status.Text += " · 过长行的预览已截短为 2,000 字符。";
            }
            catch (OperationCanceledException) { }
            catch (ArgumentException error) { if (!disposed && revision == requested) { status.Text = error.Message; leftPreview.Document = Document(); rightPreview.Document = Document(); } }
            catch (Exception) { if (!disposed && revision == requested) status.Text = "对照暂时失败，请减少文本后重试。"; }
            finally { if (Object.ReferenceEquals(pending, cancellation)) pending = null; cancellation.Dispose(); }
        }
        private void Render(TextDiffResult result)
        {
            var a = Document(); var b = Document();
            foreach (var row in result.Rows.Take(600)) { a.Blocks.Add(Line(row.LeftNumber, row.Left, row.Kind, true)); b.Blocks.Add(Line(row.RightNumber, row.Right, row.Kind, false)); }
            leftPreview.Document = a; rightPreview.Document = b;
        }
        private static Paragraph Line(int? number, string text, TextDiffKind kind, bool isLeft)
        {
            var line = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(3, 1, 3, 1), KeepTogether = true };
            if (kind == TextDiffKind.Modified) line.Background = Display.Brush("#493A23");
            else if (kind == TextDiffKind.Removed && isLeft) line.Background = Display.Brush("#4B292D");
            else if (kind == TextDiffKind.Added && !isLeft) line.Background = Display.Brush("#204738");
            line.Inlines.Add(new Run(number.HasValue ? number.Value.ToString().PadLeft(4) + "  " : "      ") { Foreground = Display.Brush("#899F93") });
            if (text != null && text.Length > 2000) text = text.Substring(0, Char.IsHighSurrogate(text[1999]) ? 1999 : 2000) + "…";
            line.Inlines.Add(new Run(text == null || text.Length == 0 ? " " : text)); return line;
        }
        private void SyncScroll(RichTextBox source, RichTextBox target, ScrollChangedEventArgs e)
        {
            if (synchronizingScroll || e.VerticalChange == 0) return;
            try { synchronizingScroll = true; target.ScrollToVerticalOffset(source.VerticalOffset); } finally { synchronizingScroll = false; }
        }
        public void Dispose() { if (disposed) return; disposed = true; revision++; debounce.Stop(); if (pending != null) pending.Cancel(); left.Clear(); right.Clear(); leftPreview.Document = Document(); rightPreview.Document = Document(); LastResult = null; }
    }
}
