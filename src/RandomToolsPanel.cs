using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace CodexIsland
{
    internal sealed class RandomToolsPanel : StackPanel
    {
        private readonly bool demo;
        private readonly ComboBox mode = new ComboBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 12, Margin = new Thickness(0, 4, 0, 6) };
        private readonly StackPanel passwordSettings = new StackPanel(), integerSettings = new StackPanel(), listSettings = new StackPanel();
        private readonly TextBox length = ToolStyle.Input("20"), minimum = ToolStyle.Input("1"), maximum = ToolStyle.Input("100"), listInput = ToolStyle.Input(), drawCount = ToolStyle.Input("1"), output = ToolStyle.Input();
        private readonly CheckBox uppercase = Check("大写 A–Z", true), lowercase = Check("小写 a–z", true), digits = Check("数字 0–9", true), symbols = Check("符号", true), exclude = Check("排除易混字符 I / l / 1 / O / 0", true), deduplicate = Check("合并完全一致的重复行", true);
        private readonly TextBlock status = ToolStyle.Hint(""), listCount = ToolStyle.Hint("");
        private readonly Button generate, copy, shuffle;
        private bool hasResult;

        public RandomToolsPanel(bool demoMode)
        {
            demo = demoMode;
            Children.Add(ToolStyle.Heading("随机与密码"));
            Children.Add(ToolStyle.Hint(demo ? "演示模式 · 下方为示例，不写入剪贴板。" : "点击后使用系统加密随机数生成。内容不保存、不上传，也不自动读取剪贴板。"));
            foreach (string name in new[] { "生成密码", "随机整数", "名单抽签 / 打乱" }) mode.Items.Add(name);
            Children.Add(mode);

            passwordSettings.Children.Add(ToolStyle.Hint("密码长度 · 8 至 128")); length.MaxLength = 3; passwordSettings.Children.Add(length);
            var groups = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            groups.ColumnDefinitions.Add(new ColumnDefinition()); groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); groups.ColumnDefinitions.Add(new ColumnDefinition());
            groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int optionIndex = 0;
            foreach (CheckBox option in new[] { uppercase, lowercase, digits, symbols }) { Grid.SetColumn(option, (optionIndex % 2) * 2); Grid.SetRow(option, optionIndex / 2); groups.Children.Add(option); optionIndex++; }
            passwordSettings.Children.Add(groups); passwordSettings.Children.Add(exclude);
            passwordSettings.Children.Add(ToolStyle.Hint("每种勾选类型至少出现一次。生成后请妥善保存；关闭本应用后无法找回。")); Children.Add(passwordSettings);

            integerSettings.Children.Add(ToolStyle.Hint("最小值与最大值 · 均包含在结果范围内"));
            var range = new Grid(); range.ColumnDefinitions.Add(new ColumnDefinition()); range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); range.ColumnDefinitions.Add(new ColumnDefinition());
            minimum.MaxLength = maximum.MaxLength = 11; minimum.ToolTip = "最小值"; maximum.ToolTip = "最大值";
            range.Children.Add(minimum); Grid.SetColumn(maximum, 2); range.Children.Add(maximum); integerSettings.Children.Add(range);
            integerSettings.Children.Add(ToolStyle.Hint("支持 −2,147,483,648 至 2,147,483,647。每次生成一个整数；范围相等时返回该值。")); Children.Add(integerSettings);

            listSettings.Children.Add(ToolStyle.Hint("每行一项 · 忽略空白行，保留每行原文与空格"));
            listInput.AcceptsReturn = true; listInput.Height = 130; listInput.MaxLength = RandomTools.MaximumListLength; listInput.VerticalContentAlignment = VerticalAlignment.Top; listInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; listInput.TextWrapping = TextWrapping.Wrap;
            listSettings.Children.Add(listInput); listSettings.Children.Add(deduplicate); listSettings.Children.Add(listCount);
            listSettings.Children.Add(ToolStyle.Hint("抽取数量 · 同一行在本次抽取中最多出现一次")); drawCount.MaxLength = 3; listSettings.Children.Add(drawCount);
            listSettings.Children.Add(ToolStyle.Hint("最多 500 个非空行、32,768 个文本单元。不合并重复行时，同名内容可能多次出现；再次抽取会重新使用完整名单。")); Children.Add(listSettings);

            generate = IslandWindow.Button("生成密码", () => Run(false)); shuffle = IslandWindow.Button("打乱全部", () => Run(true));
            var actions = PanelActionLayout.Create(3, generate, shuffle, IslandWindow.Button("清空结果", () => Invalidate("结果已清空。")));
            actions.Margin = new Thickness(0, 6, 0, 0); Children.Add(actions);
            output.IsReadOnly = true; output.IsUndoEnabled = false; output.AcceptsReturn = true; output.TextWrapping = TextWrapping.Wrap; output.VerticalContentAlignment = VerticalAlignment.Top; output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; output.MinHeight = 42; output.MaxHeight = 190; output.Margin = new Thickness(0, 6, 0, 6); Children.Add(output);
            copy = PanelActionLayout.Caption(IslandWindow.Button("复制结果", Copy)); copy.IsEnabled = false; Children.Add(copy); Children.Add(status);

            foreach (TextBox input in new[] { length, minimum, maximum, listInput, drawCount }) input.TextChanged += (s, e) => { Invalidate("输入已更改，请重新生成。"); UpdateListCount(); };
            foreach (CheckBox option in new[] { uppercase, lowercase, digits, symbols, exclude, deduplicate }) { option.Checked += Changed; option.Unchecked += Changed; }
            mode.SelectionChanged += (s, e) => SelectMode(); mode.SelectedIndex = 0; UpdateListCount();
            if (demo) { output.Text = "示例：J7x!qN4#rT8@vZ2%"; hasResult = true; copy.IsEnabled = true; status.Text = "演示密码示例，请勿用于实际账户。"; }
        }

        private static CheckBox Check(string label, bool value) { var caption = ToolStyle.Hint(label); caption.Margin = new Thickness(0); caption.FontSize = 12; return new CheckBox { Content = caption, IsChecked = value, Foreground = Display.Brush("#C7DECF"), FontSize = 12, Margin = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Center }; }
        private void Changed(object sender, RoutedEventArgs e) { Invalidate("选项已更改，请重新生成。"); UpdateListCount(); }
        private void SelectMode()
        {
            passwordSettings.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            integerSettings.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            listSettings.Visibility = shuffle.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            generate.Content = mode.SelectedIndex == 0 ? "生成密码" : mode.SelectedIndex == 1 ? "生成整数" : "抽取名单";
            Invalidate("设置参数后点击生成；更换模式会清空旧结果。");
        }
        private void Invalidate(string message) { hasResult = false; output.Clear(); copy.IsEnabled = false; status.Text = message; }
        private void UpdateListCount()
        {
            try { var parsed = RandomTools.ParseList(listInput.Text, deduplicate.IsChecked == true); listCount.Text = parsed.OriginalCount + " 个非空行 · " + parsed.Items.Count + " 个可选项" + (deduplicate.IsChecked == true ? " · 已合并 " + parsed.DuplicatesRemoved + " 个重复行" : " · 保留重复行"); }
            catch (ArgumentException error) { listCount.Text = error.Message; }
        }
        private static int ParseInteger(string text, string label)
        {
            int value; if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) throw new ArgumentException(label + "须为 −2,147,483,648 至 2,147,483,647 的整数。"); return value;
        }
        private void Run(bool all)
        {
            Invalidate("");
            try
            {
                string result, description;
                if (mode.SelectedIndex == 0)
                {
                    result = RandomTools.Password(new PasswordOptions { Length = ParseInteger(length.Text, "密码长度"), Uppercase = uppercase.IsChecked == true, Lowercase = lowercase.IsChecked == true, Digits = digits.IsChecked == true, Symbols = symbols.IsChecked == true, ExcludeAmbiguous = exclude.IsChecked == true });
                    description = "已生成 " + result.Length + " 位密码 · 不会自动保存";
                }
                else if (mode.SelectedIndex == 1) { result = RandomTools.Integer(ParseInteger(minimum.Text, "最小值"), ParseInteger(maximum.Text, "最大值")).ToString(CultureInfo.InvariantCulture); description = "已生成范围内整数（含两端）"; }
                else if (mode.SelectedIndex == 2)
                {
                    var parsed = RandomTools.ParseList(listInput.Text, deduplicate.IsChecked == true);
                    int count = all ? parsed.Items.Count : ParseInteger(drawCount.Text, "抽取数量");
                    result = String.Join(Environment.NewLine, RandomTools.Draw(parsed, count)); description = all ? "已打乱全部 " + count + " 项 · 原名单保留" : "已从 " + parsed.Items.Count + " 项中抽取 " + count + " 项 · 原名单保留";
                }
                else throw new ArgumentException("请先选择一个模式。");
                output.Text = result; hasResult = true; copy.IsEnabled = true; status.Text = description;
            }
            catch (ArgumentException error) { Invalidate(error.Message); }
            catch (CryptographicException) { Invalidate("系统随机数暂不可用，请稍后重试。"); }
        }
        private void Copy()
        {
            if (!hasResult || output.Text.Length == 0) { status.Text = "请先生成结果。"; return; }
            if (demo) { status.Text = "演示模式不写入剪贴板。"; return; }
            try { Clipboard.SetText(output.Text); status.Text = "已复制结果到系统剪贴板"; } catch { status.Text = "剪贴板正忙，请稍后重试。"; }
        }
    }
}
