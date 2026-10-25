namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void HoverCatalog()
        {
            Map(@"悬停时自动展开|Expand on hover
悬停等待时间（毫秒）|Hover delay (milliseconds)
100—5000 毫秒（1000 毫秒 = 1 秒）；关闭后，点击星标或空白处展开。|100–5000 ms (1000 ms = 1 second). When disabled, click the star or blank area to expand.
请输入 100—5000 之间的整数毫秒值。|Enter a whole number from 100 to 5000 milliseconds.
点击组件快捷操作 · 按住拖动\n点击星标或空白处展开（悬停已关闭）|Click a widget for its quick action · Hold to drag\nClick the star or blank area to expand (hover disabled)");
            Pattern("岛内未读 {0} 条 · 点击或悬停查看", "Unread here {0} · Click or hover to view");
            Pattern("岛内未读 {0} 条 · 点击查看（悬停已关闭）", "Unread here {0} · Click to view (hover disabled)");
        }
    }
}
