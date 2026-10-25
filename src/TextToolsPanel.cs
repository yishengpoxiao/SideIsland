using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class TextToolsPanel : StackPanel
    {
        private readonly bool demo;
        private readonly TextBox input = ToolStyle.Input(), output = ToolStyle.Input();
        private readonly TextBlock statistics = ToolStyle.Hint(""), status = ToolStyle.Hint("");
        private readonly ComboBox operation = new ComboBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
        private readonly string[] operations = { "upper", "lower", "deduplicate", "nonempty", "trim", "json-pretty", "json-compact", "url-encode", "url-decode", "base64-encode", "base64-decode", "sha256" };
        private bool hasResult;
        public TextToolsPanel(bool demoMode)
        {
            demo = demoMode; Children.Add(ToolStyle.Heading("文字处理工作台")); Children.Add(ToolStyle.Hint("输入只留在本次运行内存中，不自动读取剪贴板。每次最多 65,536 个文本单元。"));
            input.AcceptsReturn = true; input.TextWrapping = TextWrapping.Wrap; input.VerticalContentAlignment = VerticalAlignment.Top; input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; input.Height = 128; input.MaxLength = TextTools.MaximumInputLength;
            if (demo) input.Text = "{\"message\":\"你好 Side Island\",\"count\":3}";
            input.TextChanged += (s, e) => { UpdateStatistics(); hasResult = false; output.Clear(); status.Text = "输入已更改，请重新处理。"; }; Children.Add(input); Children.Add(statistics);
            foreach (string label in new[] { "转换为大写", "转换为小写", "去重行（保留首次出现）", "去除空白行", "清理每行首尾空格", "JSON 格式化", "JSON 压缩", "URL 参数编码", "URL 参数解码", "Base64 编码（UTF-8）", "Base64 解码（UTF-8）", "SHA-256 摘要" }) operation.Items.Add(label);
            operation.SelectedIndex = demo ? 5 : 0; Children.Add(operation);
            Children.Add(PanelActionLayout.Create(3, IslandWindow.Button("处理", Run), IslandWindow.Button("生成 GUID", () => { output.Text = Guid.NewGuid().ToString(); hasResult = true; status.Text = "已生成新的 GUID"; }), IslandWindow.Button("清空", () => { input.Clear(); output.Clear(); hasResult = false; status.Text = "已清空本面板文字"; })));
            output.AcceptsReturn = true; output.TextWrapping = TextWrapping.Wrap; output.VerticalContentAlignment = VerticalAlignment.Top; output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; output.Height = 128; output.IsReadOnly = true; output.Margin = new Thickness(0, 6, 0, 6); Children.Add(output);
            Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("复制结果", Copy), IslandWindow.Button("结果作为输入", Apply))); Children.Add(status);
            Children.Add(ToolStyle.Hint("URL 按单个参数值处理，+ 保留为加号。JSON 保留原数字与转义文本，拒绝重复字段名；不做代码执行。"));
            UpdateStatistics(); if (demo) Run();
        }
        private void UpdateStatistics()
        {
            var value = TextTools.Statistics(input.Text); statistics.Text = value.Characters + " 字符 · " + value.Words + " 词段 · " + value.Lines + " 行\n词段按连续字母、数字分组；字符按文本元素计数。";
        }
        private void Run()
        {
            try { string transformed = TextTools.Transform(operations[operation.SelectedIndex], input.Text); output.Text = transformed; hasResult = true; status.Text = "处理完成 · 原文保留在上方"; }
            catch (ArgumentException error) { hasResult = false; output.Clear(); status.Text = error.Message; }
        }
        private void Copy()
        {
            if (!hasResult || output.Text.Length == 0) { status.Text = "还没有可复制的结果。"; return; }
            if (demo) { status.Text = "演示模式不写入剪贴板。"; return; }
            try { Clipboard.SetText(output.Text); status.Text = "已复制结果"; } catch { status.Text = "剪贴板正忙，请稍后重试。"; }
        }
        private void Apply()
        {
            if (!hasResult) { status.Text = "请先完成文字处理。"; return; }
            if (output.Text.Length > TextTools.MaximumInputLength) { status.Text = "结果超过输入上限，请复制后分段处理。"; return; }
            input.Text = output.Text; status.Text = "结果已放入输入框";
        }
    }
}
