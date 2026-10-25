namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void MusicV2Catalog()
        {
            Map(@"歌词显示|Lyrics display
歌词字号（9–16）|Lyric font size (9–16)
同步偏移（毫秒）|Sync offset (milliseconds)
应用歌词显示|Apply lyrics display
正数让歌词提前，负数让歌词延后；范围 ±10000 毫秒。收起卡片上滚轮调音量，点击封面播放 / 暂停。|Positive offsets advance lyrics; negative offsets delay them (±10000 ms). Scroll over the compact music card for system volume; click its artwork to play/pause.
请输入 9–16 的整数字号，以及 -10000 到 10000 的同步偏移。|Enter a whole-number font size from 9 to 16 and an offset from -10000 to 10000.
歌词显示设置已保存|Lyrics display settings saved
歌词显示设置保存失败，请重试|Could not save lyrics display settings. Try again.
播放 / 暂停|Play / pause
音乐与歌词设置…|Music and lyrics settings…
点击封面播放 / 暂停 · 滚轮调整系统音量\n两侧小按钮切歌 · 点击文字打开音乐设置 · 按住拖动|Click artwork to play/pause · Scroll for system volume\nUse small buttons to skip · Click text for music settings · Hold to drag
未找到可调节的音频输出设备|No adjustable audio output device found");
            Pattern("系统音量 {0}%", "System volume {0}%");
        }
    }
}
