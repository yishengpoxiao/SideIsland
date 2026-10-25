using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexIsland
{
    internal sealed class ToolDefinition
    {
        public readonly string Key, Label, SettingsLabel;
        public ToolDefinition(string key, string label, string settingsLabel) { Key = key; Label = label; SettingsLabel = settingsLabel; }
    }
    internal static class ToolCatalog
    {
        internal const int MaximumFavorites = 6;
        public static readonly ToolDefinition[] All = {
            new ToolDefinition("media", "♫ 媒体与声音", "媒体控制与音量"),
            new ToolDefinition("focus", "◷ 专注计时", "番茄钟 / 倒计时 / 秒表"),
            new ToolDefinition("notes", "☷ 待办便签", "待办与便签"),
            new ToolDefinition("reminders", "◴ 提醒闹钟", "提醒与闹钟"),
            new ToolDefinition("time", "◷ 日期与时区", "世界时钟 / 日期 / 时间戳"),
            new ToolDefinition("launch", "↗ 快捷启动", "快捷启动"),
            new ToolDefinition("files", "▱ 文件收纳", "文件拖放收纳"),
            new ToolDefinition("hash", "# 文件校验", "文件 SHA-256 / SHA-512 校验"),
            new ToolDefinition("clipboard", "▤ 文字收纳", "手动剪贴板收纳"),
            new ToolDefinition("text", "≡ 文字工具", "文字处理工具"),
            new ToolDefinition("calculator", "＋ 计算器", "计算器"),
            new ToolDefinition("converter", "⇄ 单位换算", "单位换算"),
            new ToolDefinition("random", "⚄ 随机与密码", "密码生成 / 随机数 / 名单抽签"),
            new ToolDefinition("colors", "◈ 屏幕取色", "屏幕取色"),
            new ToolDefinition("system", "↕ 系统详情", "网络与系统详情"),
            new ToolDefinition("ports", "⌁ 端口占用", "端口占用 / 程序与 PID"),
            new ToolDefinition("screenpin", "▣ 截图钉屏", "区域截图与悬浮钉屏"),
            new ToolDefinition("image", "▧ 图片处理", "图片压缩 / 分辨率 / 格式转换"),
            new ToolDefinition("diff", "≠ 文字对照", "文字差异对照"),
            new ToolDefinition("watch", "◉ 任务守望", "文件生成 / 进程退出提醒"),
            new ToolDefinition("awake", "☕ 保持唤醒", "临时保持唤醒"),
            new ToolDefinition("qr", "▦ 二维码传话", "网址与文字二维码"),
            new ToolDefinition("scenes", "↗ 工作场景", "组合快捷入口批量启动"),
            new ToolDefinition("clipboardhistory", "▤ 剪贴板历史", "剪贴板历史 / 搜索与收藏"),
            new ToolDefinition("logwatch", "≡ 日志追踪", "实时日志 / 关键词提醒"),
            new ToolDefinition("desktop", "◈ 桌面交互", "全屏避让 / 手势 / 多屏 / 快捷键"),
            new ToolDefinition("profiles", "☷ 场景布局", "组件布局与工作场景联动"),
            new ToolDefinition("diagnostics", "⌕ 启动诊断", "启动与运行故障诊断"),
            new ToolDefinition("ocr", "▣ 截图提字", "截图 OCR / 图片文字识别"),
            new ToolDefinition("templates", "≡ 文本模板", "常用文本 / 日期占位符"),
            new ToolDefinition("ready", "◉ 服务就绪", "端口 / 网页 / 文件稳定提醒"),
            new ToolDefinition("backup", "▱ 设置迁移", "偏好设置备份 / 导入"),
            new ToolDefinition("autorules", "⇌ 自动场景", "按应用 / 时间 / 显示器切换场景"),
            new ToolDefinition("widgets", "▦ 组件编辑", "组件顺序 / 尺寸 / 优先级"),
            new ToolDefinition("audio-mixer", "♫ 应用音量", "分应用音量 / 输出设备"),
            new ToolDefinition("lan-transfer", "↔ 局域网传输", "扫码传文件到手机"),
            new ToolDefinition("batch", "▱ 批量文件", "批量重命名 / 图片处理"),
            new ToolDefinition("activities", "◉ 任务动态", "任务进度 / 本地接入")
        };
        public static bool Contains(string key) { return All.Any(item => item.Key == key); }
        public static ToolDefinition Find(string key) { return All.First(item => item.Key == key); }
        public static bool Matches(string key, string query)
        {
            if (!Contains(key)) return false;
            var item = Find(key); string aliases;
            SearchAliases.TryGetValue(key, out aliases);
            string haystack = key + " " + item.Label + " " + item.SettingsLabel + " " + UiLocalization.Localize(item.Label) + " " + UiLocalization.Localize(item.SettingsLabel) + " " + aliases;
            return (query ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Take(8).All(word => haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private static readonly Dictionary<string, string> SearchAliases = new Dictionary<string, string> {
            {"media","music song lyrics volume microphone mute speaker 音乐 歌词 音量 麦克风 静音 上一首 下一首 播放"},
            {"clipboardhistory","clipboard history copy paste image favorites 剪贴板 历史 复制 粘贴 收藏 jianqieban"},
            {"clipboard","text copy manual 文字 复制 粘贴"}, {"logwatch","log tail error warning trace 日志 报错 错误 追踪 关键词 rizi"},
            {"desktop","fullscreen game presentation hide gestures middle wheel keyboard shortcut monitor display 全屏 游戏 演示 隐藏 手势 滚轮 中键 快捷键 多屏 显示器"},
            {"profiles","layout profile coding music focus 场景 布局 写代码 听歌 专注"}, {"diagnostics","startup slow error debug failure 启动 慢 不出现 故障 诊断 错误 失败"},
            {"ports","port pid process occupied tcp udp 端口 占用 程序 进程 3000 8080"}, {"screenpin","screenshot capture pin 截图 截屏 钉屏 jietu"},
            {"awake","sleep wake presentation download 防休眠 保持唤醒 下载 演示"}, {"scenes","workspace project batch launch 工作 场景 项目 批量 启动"},
            {"focus","timer pomodoro stopwatch countdown 计时 倒计时 秒表 专注 番茄"}, {"image","image compress resize convert 图片 压缩 尺寸 格式"},
            {"qr","qr code phone transfer 二维码 手机 传话 erweima"}, {"diff","diff compare config prompt 比较 对照 差异 配置 提示词"},
            {"system","network bandwidth memory cpu 网速 内存 系统"}, {"watch","watch file process completion 守望 文件 进程 完成"},
            {"ocr","ocr screenshot image text extract 截图 提字 识别 图片 文字"}, {"templates","snippet template prompt code date 文本 模板 提示词 代码 日期"},
            {"ready","ready service server http tcp stable port wait 服务 端口 网页 文件 稳定 就绪 等待"}, {"backup","backup export import migrate restore settings 备份 迁移 导入 导出 设置 恢复"},
            {"autorules","automatic rules foreground app time display scene 自动 规则 前台 应用 时间 显示器 场景"},
            {"widgets","widget layout reorder size priority 组件 编辑 布局 顺序 大小 优先级"},
            {"audio-mixer","audio mixer session volume output headphone speaker 应用 音量 混音 输出 耳机 音箱"},
            {"lan-transfer","lan wifi file phone transfer qr 局域网 文件 手机 传输 二维码"},
            {"batch","batch rename compress resize convert undo 批量 重命名 压缩 图片 撤销"},
            {"activities","task activity progress build script api 任务 进度 构建 脚本 接口 动态"}
        };
        public static string NormalizeFavorites(string value)
        {
            return String.Join(",", (value ?? "").Split(',').Select(item => item.Trim()).Where(Contains).Distinct().Take(MaximumFavorites));
        }
    }
}
