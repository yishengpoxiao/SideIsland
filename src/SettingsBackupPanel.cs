using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CodexIsland
{
    internal sealed class SettingsBackupPanel : StackPanel
    {
        private readonly bool demo;
        private readonly SettingsBackupService service;
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly Button stage, cancel;
        private SettingsBackupBundle selected;
        private bool busy;
        internal SettingsBackupPanel(bool demoMode)
        {
            demo = demoMode; service = new SettingsBackupService(Path.GetDirectoryName(new SettingsStore().FilePath));
            Children.Add(ToolStyle.Heading("设置备份与迁移")); Children.Add(ToolStyle.Hint("导出布局、手势、快捷键、通知规则及音乐偏好。备份不包含账户路径、剪贴板、便签和文本模板；导入保留当前账户路径和开机启动设置。"));
            var export = IslandWindow.Button("导出偏好设置", Export); var preview = IslandWindow.Button("选择并预览备份", Preview);
            stage = IslandWindow.Button("安排下次启动时导入", Stage); stage.IsEnabled = false;
            cancel = IslandWindow.Button("取消待导入设置", Cancel); Children.Add(PanelActionLayout.Create(2, export, preview, stage, cancel)); Children.Add(status);
            Children.Add(ToolStyle.Hint("导入在退出后再次启动时应用，避免当前运行的旧设置覆盖导入结果。原偏好设置会保留一份本地恢复副本。")); RefreshPending();
        }
        private void RefreshPending()
        {
            if (demo) { cancel.IsEnabled = false; status.Text = "演示模式不会读写本机偏好设置。"; return; }
            bool pending = service.HasPending; cancel.IsEnabled = pending;
            if (pending) status.Text = "已安排导入，退出后重新打开灵动岛生效。";
        }
        private async void Export()
        {
            if (busy || demo) return; var dialog = new SaveFileDialog { Title = UiLocalization.Localize("导出偏好设置"), Filter = UiLocalization.FileFilter("Side Island 偏好设置|*.json"), FileName = "SideIsland-settings.json", DefaultExt = ".json", AddExtension = true }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            SetBusy(true); try { string target = dialog.FileName; await Task.Run(() => service.ExportTo(target)); status.Text = "偏好设置已导出。"; } catch (Exception error) { ShowError(error); } finally { SetBusy(false); }
        }
        private async void Preview()
        {
            if (busy || demo) return; var dialog = new OpenFileDialog { Title = UiLocalization.Localize("选择并预览备份"), Filter = UiLocalization.FileFilter("Side Island 偏好设置|*.json"), CheckFileExists = true }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            SetBusy(true); selected = null; stage.IsEnabled = false;
            try { string path = dialog.FileName; selected = await Task.Run(() => SettingsBackupService.Read(path)); status.Text = UiLocalization.Localize("将导入以下偏好文件：") + "\n" + String.Join("\n", selected.Files.Keys.OrderBy(x => x)); stage.IsEnabled = true; } catch (Exception error) { ShowError(error); } finally { SetBusy(false); }
        }
        private async void Stage()
        {
            if (busy || demo || selected == null) return; SetBusy(true);
            try { var value = selected; await Task.Run(() => service.Stage(value)); RefreshPending(); } catch (Exception error) { ShowError(error); } finally { SetBusy(false); }
        }
        private void Cancel() { if (demo || busy) return; try { service.Cancel(); status.Text = "待导入已取消。"; RefreshPending(); } catch (Exception error) { ShowError(error); } }
        private void SetBusy(bool value) { busy = value; IsEnabled = !value; if (!value) { stage.IsEnabled = !demo && selected != null; cancel.IsEnabled = !demo && service.HasPending; } }
        private void ShowError(Exception error) { status.Text = error is ArgumentException || UiLocalization.HasTranslation(error.Message) ? error.Message : "偏好设置操作失败，请检查文件与目录权限。"; }
    }
}
