using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexIsland
{
    internal sealed class ConverterPanel : StackPanel
    {
        private readonly bool demo;
        private readonly ComboBox category = Choice(), from = Choice(), to = Choice();
        private readonly TextBox input = ToolStyle.Input("1");
        private readonly TextBlock result = IslandWindow.Text("—", 25, "#A1EACF", true), status = ToolStyle.Hint("");
        private string copyValue;
        private bool updating;
        public ConverterPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("常用单位，一步换算"));
            Children.Add(ToolStyle.Hint("10 类固定单位 · 离线计算。KB 按 1000，KiB 按 1024。"));
            Children.Add(category); category.ItemsSource = ConversionTools.Categories;
            Children.Add(ToolStyle.Hint("输入数值与原单位")); input.MaxLength = 100; Children.Add(input); Children.Add(from);
            Children.Add(ToolStyle.Hint("换算为")); Children.Add(to);
            var actions = PanelActionLayout.Create(2, IslandWindow.Button("交换单位", Swap), IslandWindow.Button("复制结果", Copy));
            actions.Margin = new Thickness(0, 8, 0, 0); Children.Add(actions);
            result.TextWrapping = TextWrapping.Wrap; result.Margin = new Thickness(0, 8, 0, 2); Children.Add(result); Children.Add(status);
            category.SelectionChanged += (s, e) => SetCategory();
            from.SelectionChanged += (s, e) => Calculate(); to.SelectionChanged += (s, e) => Calculate(); input.TextChanged += (s, e) => Calculate();
            category.SelectedIndex = 0;
        }
        private static ComboBox Choice() { return new ComboBox { FontSize = 12, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch }; }
        private void SetCategory()
        {
            var selected = category.SelectedItem as ConversionCategory; if (selected == null) return;
            updating = true; from.ItemsSource = selected.Units; to.ItemsSource = selected.Units; from.SelectedIndex = 0; to.SelectedIndex = 1; updating = false; Calculate();
        }
        private void Calculate()
        {
            if (updating || category.SelectedItem == null || from.SelectedItem == null || to.SelectedItem == null) return;
            try
            {
                double value = ConversionTools.Convert((ConversionCategory)category.SelectedItem, (ConversionUnit)from.SelectedItem, (ConversionUnit)to.SelectedItem, ConversionTools.Parse(input.Text));
                copyValue = ConversionTools.Format(value); result.Text = copyValue + "\n" + to.SelectedItem; status.Text = "结果为近似值，显示最多 15 位有效数字。";
            }
            catch (ArgumentException error) { copyValue = null; result.Text = "—"; status.Text = error.Message; }
        }
        private void Swap()
        {
            updating = true; int previous = from.SelectedIndex; from.SelectedIndex = to.SelectedIndex; to.SelectedIndex = previous;
            if (copyValue != null) input.Text = copyValue;
            updating = false; Calculate();
        }
        private void Copy()
        {
            if (copyValue == null) { status.Text = "请先输入可换算的数值。"; return; }
            if (demo) { status.Text = "演示模式不写入剪贴板。"; return; }
            try { Clipboard.SetText(copyValue); status.Text = "已复制数值"; } catch { status.Text = "剪贴板正忙，请稍后重试。"; }
        }
    }
}
