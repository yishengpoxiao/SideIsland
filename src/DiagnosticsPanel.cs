using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class DiagnosticsPanel : StackPanel
    {
        private readonly bool demo;
        private readonly Func<string> windowStatus;
        private readonly TextBox result = ToolStyle.Input();
        private bool reading;
        internal DiagnosticsPanel(bool demoMode, Func<string> status)
        {
            demo = demoMode; windowStatus = status;
            Children.Add(ToolStyle.Heading("启动与运行诊断"));
            Children.Add(ToolStyle.Hint("检查启动阶段、窗口状态和本地设置可读性。诊断不包含通知正文、剪贴板内容、歌词或账户凭据。"));
            var refresh = IslandWindow.Button("运行诊断", Refresh); var export = IslandWindow.Button("保存诊断报告", Export);
            var actions = PanelActionLayout.Create(2, refresh, export); actions.Margin = new Thickness(0, 0, 0, 6); Children.Add(actions);
            result.IsReadOnly = true; result.AcceptsReturn = true; result.TextWrapping = TextWrapping.Wrap; result.MinHeight = 160; result.MaxHeight = 420; result.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Children.Add(result);
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
        }
        private async void Refresh()
        {
            if (reading) return; reading = true;
            try
            {
                string state = windowStatus();
                var report = await Task.Run(() => {
                    var start = demo ? new StartupDiagnosticData { Stage = "演示启动", Ready = true, ElapsedMilliseconds = 120 } : StartupDiagnostics.Snapshot();
                    string settings = new SettingsStore().FilePath; bool exists = File.Exists(settings); bool readable = false;
                    if (exists) try
                    {
                        using (var stream = File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true)))
                        {
                            if (stream.Length < 1024 * 1024) readable = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 }.DeserializeObject(reader.ReadToEnd()) is System.Collections.Generic.Dictionary<string, object>;
                        }
                    } catch (Exception) { }
                    return "Side Island " + start.Version + "\nWindows: " + Environment.OSVersion.Version + "\n.NET: " + Environment.Version + "\n" + start.StartedUtc + "\n" + UiLocalization.Localize(start.Stage) + " · " + start.ElapsedMilliseconds + " ms\nReady: " + start.Ready + "\n" + state + "\nSettings exists/valid JSON: " + exists + "/" + readable + "\n" + String.Join("\n", start.Errors ?? new System.Collections.Generic.List<string>()) + "\nPrevious: " + UiLocalization.Localize(start.PreviousStage) + " · " + start.PreviousElapsedMilliseconds + " ms; ready: " + start.PreviousReady + "\n" + String.Join("\n", start.PreviousErrors ?? new System.Collections.Generic.List<string>());
                });
                result.Text = report;
            }
            catch (Exception) { result.Text = UiLocalization.Localize("诊断暂不可用，请稍后重试。"); }
            finally { reading = false; }
        }
        private void Export()
        {
            if (demo || String.IsNullOrEmpty(result.Text)) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = UiLocalization.Localize("导出启动诊断"), Filter = UiLocalization.FileFilter("文本文件|*.txt"), FileName = "SideIsland-diagnostics.txt" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { File.WriteAllText(dialog.FileName, result.Text, new System.Text.UTF8Encoding(false)); }
            catch (Exception) { result.Text += "\n" + UiLocalization.Localize("诊断报告保存失败。"); }
        }
    }
}
