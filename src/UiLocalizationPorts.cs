namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void PortsCatalog()
        {
            Map(@"复制完整报告|Copy full report
已复制完整端口报告。|Full port report copied.
报告超过 1 Mi 字符，请减少查询端口后重试。|The report exceeds 1 Mi characters. Query fewer ports and try again.
查询失败，请重试。|Query failed. Please try again.
演示数据|Demo data");
            Pattern("演示数据 · {0} · {1} 条绑定 / 连接，合并为 {2} 个占用项", "Demo data · {0} · {1} bindings / connections, grouped into {2} owners");
            Pattern("{0} 条绑定 / 连接，合并为 {1} 个占用项", "{0} bindings / connections, grouped into {1} owners");
            Pattern("{0} · 已绑定 · {1} 条绑定 / 连接", "{0} · Bound · {1} bindings / connections");
            Pattern("{0} · 未知 · {1} 条绑定 / 连接", "{0} · Unknown · {1} bindings / connections");
            Pattern("{0} 条绑定 / 连接", "{0} bindings / connections");
            Pattern("另有 {0} 项未显示，完整报告包含全部记录。", "{0} more rows are hidden; the full report includes all records.");
            Pattern("状态 {0}", "State {0}");
        }
    }
}
