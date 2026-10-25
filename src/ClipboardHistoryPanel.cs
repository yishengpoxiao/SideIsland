using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class ClipboardHistoryPanel : StackPanel, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr window);
        private readonly bool demo;
        private readonly ClipboardHistoryBook book = new ClipboardHistoryBook();
        private readonly ClipboardHistoryStore store;
        private readonly IClipboardHistoryAdapter adapter;
        private readonly TextBox search, exclusions;
        private readonly CheckBox favoritesOnly;
        private readonly Button pause;
        private readonly TextBlock status;
        private readonly StackPanel list = new StackPanel();
        private HwndSource source;
        private ClipboardHistoryWorker clipboardWorker;
        private volatile string[] excludedSnapshot = new string[0];
        private bool started, disposed, loaded, loading, reading, captureQueued, captureAgain;
        private int generation;
        private Task saveTask = Task.FromResult(0);
        internal ClipboardHistoryBook Book { get { return book; } }
        internal Task PendingSave { get { return saveTask; } }
        public ClipboardHistoryPanel(bool demoMode, string appDataDirectory)
            : this(demoMode, appDataDirectory, null) { }
        internal ClipboardHistoryPanel(bool demoMode, string appDataDirectory, IClipboardHistoryAdapter injectedAdapter)
        {
            demo = demoMode;
            store = new ClipboardHistoryStore(Path.Combine(appDataDirectory, "clipboard-favorites.json"));
            adapter = injectedAdapter ?? new WindowsClipboardHistoryAdapter(name => excludedSnapshot.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name, StringComparer.OrdinalIgnoreCase));
            Children.Add(ToolStyle.Heading("剪贴板历史"));
            Children.Add(ToolStyle.Hint("默认暂停；开启后记录新复制的文字和图片。最近 50 项仅保存在内存，收藏会保存在本机。退出后再次开启才会继续记录。"));
            var options = new StackPanel();
            pause = IslandWindow.Button("开始记录", TogglePause);
            var clear = IslandWindow.Button("清空未收藏记录", () => { book.Clear(false); Rebuild(); }); options.Children.Add(PanelActionLayout.Create(2, pause, clear));
            options.Children.Add(ToolStyle.Hint("搜索文字或来源应用"));
            search = ToolStyle.Input(); search.MaxLength = 256; search.ToolTip = UiLocalization.Localize("搜索文字或来源应用"); search.TextChanged += (s, e) => Rebuild(); options.Children.Add(search);
            favoritesOnly = new CheckBox { Content = "仅显示收藏", Foreground = Display.Brush("#E3EFE7"), Margin = new Thickness(0, 6, 0, 0) }; UiLocalization.Content(favoritesOnly, "仅显示收藏"); favoritesOnly.Checked += (s, e) => Rebuild(); favoritesOnly.Unchecked += (s, e) => Rebuild(); options.Children.Add(favoritesOnly);
            options.Children.Add(ToolStyle.Hint("排除应用（进程名，用逗号分隔，例如 KeePass.exe, 1Password.exe）"));
            exclusions = ToolStyle.Input(); exclusions.MaxLength = 4000; options.Children.Add(exclusions);
            var save = PanelActionLayout.Create(1, IslandWindow.Button("保存排除列表", () => { if (!loaded && !demo) { status.Text = "正在读取收藏，请稍后重试。"; return; } try { book.SetExcludedApps(exclusions.Text); excludedSnapshot = book.ExcludedApps.ToArray(); Save(); status.Text = "排除列表已保存。"; } catch (Exception error) { status.Text = error.Message; } })); save.Margin = new Thickness(0, 6, 0, 0); options.Children.Add(save); Children.Add(ToolStyle.Card(options));
            status = ToolStyle.Hint("记录已暂停。不会读取当前剪贴板。"); Children.Add(status); Children.Add(list);
            Children.Add(ToolStyle.Hint("单条文字最多 65536 字，图片缩至最长边 1600 像素、最多 1 MB；总计最多 8 MB，最多收藏 20 项。系统标记为不允许历史记录的内容会略过。排除应用依据剪贴板来源进程，来源无法识别时请暂停记录。"));
            Rebuild();
        }
        public void Start()
        {
            if (disposed || started) return; started = true;
            LoadAsync();
            if (demo || !(adapter is WindowsClipboardHistoryAdapter)) return;
            try
            {
                source = new HwndSource(new HwndSourceParameters("SideIsland.ClipboardHistory") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
                source.AddHook(Hook);
                if (!AddClipboardFormatListener(source.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error) { if (source != null) { source.Dispose(); source = null; } status.Text = "剪贴板监听不可用：" + error.Message; }
        }
        private async void LoadAsync()
        {
            if (loaded || loading || demo) { if (demo) loaded = true; return; }
            loading = true; pause.IsEnabled = false;
            try
            {
                var data = await Task.Run(() => store.Load()); if (disposed) return;
                book.Restore(data); excludedSnapshot = book.ExcludedApps.ToArray(); exclusions.Text = String.Join(", ", book.ExcludedApps); loaded = true;
                if (store.LoadFailed) status.Text = "收藏文件无法读取，已保留原文件。新保存时会创建备份。";
                Rebuild();
            }
            catch (Exception error) { if (!disposed) status.Text = "读取收藏失败：" + error.Message; }
            finally { loading = false; if (!disposed) pause.IsEnabled = loaded; }
        }
        private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x031D && !book.Paused && !disposed && !captureQueued)
                QueueCapture();
            return IntPtr.Zero;
        }
        private void QueueCapture()
        {
            if (captureQueued) return; captureQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => { captureQueued = false; await CaptureNowAsync(); }));
        }
        private void TogglePause()
        {
            if (disposed || (!loaded && !demo)) return;
            if (!demo && adapter is WindowsClipboardHistoryAdapter && source == null) { status.Text = "剪贴板监听不可用，请重启应用后重试。"; return; }
            book.Paused = !book.Paused; generation++;
            if (book.Paused) captureAgain = false;
            pause.Content = book.Paused ? "开始记录" : "暂停记录";
            status.Text = book.Paused ? "记录已暂停。不会读取当前剪贴板。" : "正在记录新复制的文字和图片。";
        }
        internal async Task CaptureNowAsync()
        {
            if (disposed || !started || book.Paused || demo) return;
            if (reading) { captureAgain = true; return; }
            reading = true; captureAgain = false; int request = generation;
            try
            {
                ClipboardHistoryPayload payload;
                if (adapter is WindowsClipboardHistoryAdapter)
                { if (clipboardWorker == null) clipboardWorker = new ClipboardHistoryWorker(adapter); payload = await clipboardWorker.ReadAsync(); }
                else payload = adapter.Read();
                if (payload == null || payload.Protected || book.IsExcluded(payload.Source)) return;
                byte[] png = payload.Image == null ? null : await Task.Run(() => ClipboardHistoryImage.Encode(payload.Image));
                if (disposed || request != generation || book.Paused) return;
                if (book.Add(payload.Text, png, payload.Source, payload.Protected)) { status.Text = "已记录 " + book.Count + " 项。"; Rebuild(); }
            }
            catch (Exception error) { if (!disposed) status.Text = "本次内容已略过：" + error.Message; }
            finally
            {
                reading = false;
                if (captureAgain && !disposed && started && !book.Paused)
                    QueueCapture();
            }
        }
        private void Save()
        {
            if (demo || !loaded || disposed) return;
            ClipboardHistoryData data = book.Export();
            saveTask = saveTask.ContinueWith(previous => { try { store.Save(data); } catch (Exception error) { if (!disposed) Dispatcher.BeginInvoke(new Action(() => { if (!disposed) status.Text = "保存收藏失败：" + error.Message; })); } }, TaskScheduler.Default);
        }
        private void Rebuild()
        {
            if (search == null || favoritesOnly == null || disposed) return;
            list.Children.Clear(); var items = book.Search(search.Text, favoritesOnly.IsChecked == true).ToList();
            if (items.Count == 0) { list.Children.Add(ToolStyle.Hint("没有匹配的剪贴板记录。")); return; }
            foreach (var item in items)
            {
                ClipboardHistoryItem selected = item;
                var card = new StackPanel();
                if (item.IsImage)
                {
                    try { var image = new Image { Source = ClipboardHistoryImage.Decode(item.Png, 180), MaxHeight = 150, MaxWidth = 260, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }; card.Children.Add(image); }
                    catch { card.Children.Add(ToolStyle.Hint("图片无法显示。")); }
                }
                else
                {
                    string value = item.Text.Length > 500 ? item.Text.Substring(0, 500) + "…" : item.Text;
                    var text = IslandWindow.Text(value, 12, "#E3EFE7", false, false); text.TextWrapping = TextWrapping.Wrap; text.MaxHeight = 120; text.TextTrimming = TextTrimming.CharacterEllipsis; card.Children.Add(text);
                }
                card.Children.Add(ToolStyle.Hint(item.CreatedUtc.ToLocalTime().ToString("HH:mm:ss") + (String.IsNullOrEmpty(item.Source) ? "" : " · " + item.Source), false));
                var copy = IslandWindow.Button("复制", () => { if (demo) { status.Text = "演示模式不修改剪贴板。"; return; } try { adapter.Write(selected); status.Text = "已复制"; } catch { status.Text = "剪贴板正忙，请重试。"; } });
                var favorite = IslandWindow.Button(item.Favorite ? "取消收藏" : "收藏", () => { try { book.ToggleFavorite(selected.Id); Save(); Rebuild(); } catch (Exception error) { status.Text = error.Message; } });
                card.Children.Add(PanelActionLayout.Create(3, copy, favorite, IslandWindow.Button("删除", () => { book.Remove(selected.Id); Save(); Rebuild(); }))); list.Children.Add(ToolStyle.Card(card));
            }
        }
        public void Stop()
        {
            started = false; generation++; book.Paused = true; captureAgain = false;
            if (source != null) { RemoveClipboardFormatListener(source.Handle); source.RemoveHook(Hook); source.Dispose(); source = null; }
            pause.Content = "开始记录";
        }
        public void Dispose() { if (disposed) return; Stop(); disposed = true; if (clipboardWorker != null) clipboardWorker.Dispose(); book.Clear(true); list.Children.Clear(); }
    }
}


