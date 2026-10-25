using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace CodexIsland
{
    internal sealed class ScreenPinPanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly Func<Int32Rect, CancellationToken, BitmapSource> capture;
        private readonly Func<CancellationToken, Task<Int32Rect?>> select;
        private readonly List<ScreenPinWindow> pins = new List<ScreenPinWindow>();
        private readonly TextBlock status = ToolStyle.Hint(""), count = ToolStyle.Hint("当前没有钉屏截图");
        private readonly Button begin, closeAll;
        private CancellationTokenSource pending;
        private bool disposed;
        public bool IsCapturing { get; private set; }
        public event Action<bool> CaptureStateChanged;
        internal int PinCount { get { return pins.Count; } }

        public ScreenPinPanel(bool demoMode) : this(demoMode, ScreenPinTools.Capture, ScreenPinSelection.Select) { }
        internal ScreenPinPanel(bool demoMode, Func<Int32Rect, CancellationToken, BitmapSource> captureImage, Func<CancellationToken, Task<Int32Rect?>> selectRegion)
        {
            demo = demoMode; capture = captureImage; select = selectRegion;
            Children.Add(ToolStyle.Heading("截图钉在桌面，边看边操作"));
            Children.Add(ToolStyle.Hint("点击后拖动框选屏幕区域，Esc 取消。截图会悬浮在桌面上；可拖动、滚轮缩放，并调整透明度。"));
            begin = IslandWindow.Button("框选截图并钉屏", async () => await BeginCaptureAsync());
            closeAll = IslandWindow.Button("关闭全部钉屏", CloseAll); Children.Add(PanelActionLayout.Create(2, begin, closeAll)); Children.Add(count); Children.Add(status);
            Children.Add(ToolStyle.Hint(demo ? "演示模式不读取屏幕。" : "只有点击并完成框选才会截取该区域。截图仅保留在内存，关闭即移除；最多同时保留 8 张。受保护的视频画面可能无法截取。")); UpdateControls();
        }
        internal async Task BeginCaptureAsync()
        {
            if (disposed || IsCapturing) return;
            if (demo) { status.Text = "演示模式不截取真实屏幕。"; return; }
            if (pins.Count >= 8) { status.Text = "最多保留 8 张钉屏，请先关闭一张。"; return; }
            IsCapturing = true; var work = new CancellationTokenSource(); pending = work; UpdateControls();
            try
            {
                var changed = CaptureStateChanged; if (changed != null) changed(true);
                // Allow the hidden island to leave the composed desktop before selection.
                await Task.Delay(100, work.Token);
                Int32Rect? area = await select(work.Token);
                if (!area.HasValue) { if (!disposed) status.Text = "已取消截图。"; return; }
                ScreenPinTools.ValidateCapture(area.Value);
                // The overlay closes before the captured pixels are read.
                await Task.Delay(100, work.Token); Int32Rect selected = area.Value;
                BitmapSource snapshot = await Task.Run(() => capture(selected, work.Token)); work.Token.ThrowIfCancellationRequested();
                if (disposed) return;
                if (snapshot == null || !snapshot.IsFrozen) throw new InvalidOperationException("截图读取失败。");
                PinImage(snapshot);
            }
            catch (OperationCanceledException) { if (!disposed) status.Text = "已取消截图。"; }
            catch (Exception error) { if (!disposed) status.Text = error is ArgumentException ? error.Message : "截图未完成，请重新框选；受保护画面可能不可读取。"; }
            finally
            {
                work.Dispose(); if (ReferenceEquals(pending, work)) pending = null;
                IsCapturing = false; var changed = CaptureStateChanged; if (changed != null) changed(false); if (!disposed) UpdateControls();
            }
        }
        internal bool PinImage(BitmapSource snapshot)
        {
            if (disposed || demo) return false;
            if (snapshot == null || !snapshot.IsFrozen) throw new ArgumentException("图片尚未加载完成。");
            if (pins.Count >= 8) { status.Text = "最多保留 8 张钉屏，请先关闭一张。"; return false; }
            var pin = new ScreenPinWindow(snapshot); pins.Add(pin); pin.Closed += (s, e) => { pins.Remove(pin); if (!disposed) UpdateControls(); };
            pin.Show(); status.Text = "截图已钉屏，可拖动、滚轮缩放或调整透明度。"; UpdateControls(); return true;
        }
        internal void ReportExternalError(string message) { if (!disposed) status.Text = message; }
        private void UpdateControls() { begin.IsEnabled = !IsCapturing && !disposed; closeAll.IsEnabled = pins.Count > 0; count.Text = pins.Count == 0 ? "当前没有钉屏截图" : "当前钉屏 " + pins.Count + " 张 · 退出灵动岛时自动关闭"; }
        private void CloseAll() { foreach (var pin in pins.ToArray()) pin.Close(); }
        public void Dispose() { if (disposed) return; disposed = true; if (pending != null) pending.Cancel(); CloseAll(); }
    }
}
