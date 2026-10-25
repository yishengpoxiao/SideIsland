using Avalonia.Markup.Xaml;
using Avalonia.Styling;
namespace SideIsland.Desktop;
internal static class IslandStyle
{
    public static Styles Create() => AvaloniaRuntimeXamlLoader.Parse<Styles>("""
    <Styles xmlns="https://github.com/avaloniaui">
      <Style Selector="Window"><Setter Property="FontFamily" Value="Inter, Segoe UI, Noto Sans CJK SC, PingFang SC"/><Setter Property="FontSize" Value="13"/></Style>
      <Style Selector="Button"><Setter Property="CornerRadius" Value="10"/><Setter Property="Padding" Value="10,7"/><Setter Property="MinHeight" Value="32"/><Setter Property="HorizontalContentAlignment" Value="Center"/></Style>
      <Style Selector="TextBox"><Setter Property="CornerRadius" Value="10"/><Setter Property="Padding" Value="10,7"/><Setter Property="MinHeight" Value="34"/></Style>
      <Style Selector="ComboBox"><Setter Property="CornerRadius" Value="10"/><Setter Property="MinHeight" Value="34"/><Setter Property="HorizontalAlignment" Value="Stretch"/></Style>
      <Style Selector="NumericUpDown"><Setter Property="CornerRadius" Value="10"/><Setter Property="MinHeight" Value="34"/></Style>
      <Style Selector="MenuItem"><Setter Property="MinHeight" Value="30"/><Setter Property="FontSize" Value="12"/><Setter Property="Padding" Value="10,5"/></Style>
      <Style Selector="TextBlock.hint"><Setter Property="Opacity" Value="0.65"/><Setter Property="FontSize" Value="11"/><Setter Property="TextWrapping" Value="Wrap"/></Style>
      <Style Selector="TextBlock.heading"><Setter Property="FontSize" Value="18"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="TextWrapping" Value="Wrap"/></Style>
      <Style Selector="Border.card"><Setter Property="CornerRadius" Value="14"/><Setter Property="Padding" Value="12"/><Setter Property="Margin" Value="0,0,0,8"/></Style>
    </Styles>
    """);
}
