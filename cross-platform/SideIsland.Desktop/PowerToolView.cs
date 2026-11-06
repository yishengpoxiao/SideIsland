using Avalonia.Controls;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Desktop;

public sealed partial class IslandWindow
{
    private CancellationTokenSource? awakeRequest;
    private long awakeRevision;
    private string? lastAwakeError;
    private bool volumeApplying;
    private Button? volumeApplyButton;
    private void RenderAwake()
    {
        body.Children.Add(Hint(T("只在所选时长内阻止系统自动休眠，退出岛后结束。", "Temporarily inhibits automatic sleep; quitting the island releases it.")));
        body.Children.Add(Actions(new[] { 30, 60, 120 }.Select(minutes => AsyncButton(minutes + T(" 分钟", " min"), () => ChangeAwakeAsync(true, TimeSpan.FromMinutes(minutes)))).Cast<Control>().ToArray()));
        body.Children.Add(AsyncButton(T("结束保持唤醒", "Stop keeping awake"), () => ChangeAwakeAsync(false, TimeSpan.Zero)));
    }
    internal async Task ChangeAwakeAsync(bool enabled, TimeSpan duration)
    {
        if (closing || (testMode && integrations.SetAwake is null)) return;
        long request = ++awakeRevision; awakeRequest?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); awakeRequest = source;
        Report(T("正在更新保持唤醒…", "Updating keep awake…"));
        try
        {
            var error = await (integrations.SetAwake ?? platform.SetAwakeAsync)(enabled, duration, source.Token);
            if (closing || request != awakeRevision || source.IsCancellationRequested) return;
            var state = ReadAwakeState(); awakeUntil = state.Active ? state.UntilUtc : default; RefreshCompact();
            if (error is not null) Report(error);
            else if (state.Error is not null) { lastAwakeError = state.Error; Report(state.Error); }
            else Report(state.Active ? T("已开始保持唤醒。", "Keeping awake.") : T("保持唤醒已结束。", "Keep awake stopped."));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing && request == awakeRevision) Report(error.Message); }
        finally { if (ReferenceEquals(awakeRequest, source)) awakeRequest = null; }
    }
    private AwakeSnapshot ReadAwakeState() => integrations.ReadAwake?.Invoke() ?? platform.AwakeState;
    private void PollAwakeState()
    {
        if (closing || (testMode && integrations.ReadAwake is null)) return;
        var state = ReadAwakeState(); awakeUntil = state.Active ? state.UntilUtc : default;
        if (state.Error != lastAwakeError) { lastAwakeError = state.Error; if (state.Error is not null) Report(state.Error); }
    }
    private void RenderVolume()
    {
        var content = new StackPanel();
        var level = new Slider { Name = "volume-level", Minimum = 0, Maximum = 100, Value = 50, Margin = new(0, 12) };
        var muted = new CheckBox { Name = "volume-muted", Content = T("静音", "Mute") };
        var state = Hint(T("点击读取后查看当前音量。", "Read to inspect current volume.")); state.Name = "volume-state";
        content.Children.Add(level); content.Children.Add(muted); content.Children.Add(state);
        CancellationTokenSource? reading = null; long revision = 0; bool detached = false, assigning = false;
        double lastLevel = level.Value; bool lastMuted = muted.IsChecked == true;
        void InvalidateRead()
        {
            revision++; reading?.Cancel();
        }
        void Edited()
        {
            if (lastLevel == level.Value && lastMuted == (muted.IsChecked == true)) return;
            lastLevel = level.Value; lastMuted = muted.IsChecked == true;
            if (assigning || detached || closing) return;
            InvalidateRead(); state.Text = T("音量选择已更改，点击应用生效。", "Selection changed. Apply to update volume.");
        }
        level.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty) Edited(); };
        muted.PropertyChanged += (_, change) => { if (change.Property == CheckBox.IsCheckedProperty) Edited(); };
        content.DetachedFromVisualTree += (_, _) => { detached = true; InvalidateRead(); };
        async Task Read()
        {
            if (closing || detached || (testMode && integrations.ReadVolume is null)) return;
            InvalidateRead(); long request = revision;
            using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); reading = source;
            state.Text = T("正在读取音量…", "Reading volume…");
            try
            {
                var value = await (integrations.ReadVolume ?? platform.ReadVolumeAsync)(source.Token);
                if (closing || detached || request != revision || source.IsCancellationRequested) return;
                if (!value.Available) { state.Text = value.Error ?? T("无法读取音量。", "Volume is unavailable."); return; }
                assigning = true;
                try { level.Value = value.Percent; muted.IsChecked = value.Muted; }
                finally { assigning = false; }
                state.Text = value.Percent + "%";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closing && !detached && request == revision) state.Text = error.Message; }
            finally { if (ReferenceEquals(reading, source)) reading = null; }
        }
        async Task Apply()
        {
            if (closing || detached || volumeApplying || (testMode && integrations.SetVolume is null)) return;
            InvalidateRead(); long request = revision; int percent = (int)level.Value; bool mute = muted.IsChecked == true;
            volumeApplying = true; state.Text = T("正在应用音量…", "Applying volume…");
            try
            {
                // A requested write finishes across page changes, while window
                // close cancels it. One window-wide write guard prevents queued
                // obsolete writes from accumulating through page reconstruction.
                var error = await (integrations.SetVolume ?? platform.SetVolumeAsync)(percent, mute, lifetime.Token);
                if (!closing && !detached && request == revision) state.Text = error ?? T("音量设置已应用。", "Volume settings applied.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closing && !detached && request == revision) state.Text = error.Message; }
            finally { volumeApplying = false; if (volumeApplyButton is not null) volumeApplyButton.IsEnabled = !closing; }
        }
        var read = AsyncButton(T("读取音量", "Read volume"), Read); read.Name = "volume-read";
        volumeApplyButton = AsyncButton(T("应用", "Apply"), Apply, () => !volumeApplying && !closing && !detached); volumeApplyButton.Name = "volume-apply";
        content.Children.Add(Actions(read, volumeApplyButton));
        content.Children.Add(Hint(T("此预览版提供系统音量。Mac 通用分应用音量不能直接照搬 Windows Core Audio；没有安装虚拟音频驱动。", "This preview controls system volume. Mac per-app volume needs separate support; no virtual audio driver is installed.")));
        body.Children.Add(content);
    }
}
