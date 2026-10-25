namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void DesktopExperienceCatalog()
        {
            Map(@"桌面交互与快捷键|Desktop controls and hotkeys
全屏避让只影响灵动岛所在屏幕；退出全屏后恢复。截图、拖动和编辑期间不会自动隐藏。|Fullscreen avoidance affects only the island's display and restores it afterwards. Capture, dragging and editing keep it visible.
遇到全屏程序时|When a fullscreen app is active
保持显示|Keep visible
自动隐藏|Hide automatically
收为贴边细条|Show an edge strip
收起时左键单击|Left click while collapsed
收起时鼠标中键|Middle click while collapsed
收起时鼠标滚轮|Mouse wheel while collapsed
组件默认操作|Component default
不执行操作|No action
展开／收起|Expand / collapse
开始／暂停专注|Start / pause focus
输出静音切换|Toggle output mute
麦克风静音切换|Toggle microphone mute
搜索功能|Find a feature
音乐播放／暂停|Play / pause music
上一首|Previous track
下一首|Next track
调整输出音量|Adjust output volume
打开快捷启动|Open launcher
组件默认操作保留封面和组件快捷操作；自定义只在收起状态生效。拖动不会触发点击。滚轮音量每格调整 2%。|The default keeps cover and component actions. Custom gestures apply only while collapsed. Dragging does not trigger a click. Volume changes by 2% per wheel notch.
分别记住每个显示器的位置与停靠方向|Remember position and dock edge for each display
移动到所选显示器|Move to selected display
全局快捷键（留空禁用；例如 Ctrl+Alt+Space、Ctrl+Shift+F9）|Global hotkeys (leave blank to disable; e.g. Ctrl+Alt+Space or Ctrl+Shift+F9)
展开／收起快捷键|Expand / collapse hotkey
隐私模式快捷键|Privacy mode hotkey
搜索功能快捷键|Feature search hotkey
截图钉屏快捷键|Screenshot pinning hotkey
保存桌面交互设置|Save desktop settings
桌面交互设置已保存并生效。|Desktop settings saved and applied.
快捷键格式无效，请使用 Ctrl+Alt+Space 等组合。|Invalid hotkey. Use a combination such as Ctrl+Alt+Space.
多个操作不能使用相同快捷键。|Each action needs a different hotkey.
快捷键暂不可用。|Hotkeys are currently unavailable.
快捷键被其他应用占用，已保留原快捷键。|A hotkey is occupied; previous hotkeys have been restored.
快捷键被其他应用占用，原快捷键恢复失败；可继续使用托盘和按钮。|A hotkey is occupied and previous hotkeys could not be restored. Tray and buttons remain available.
桌面设置未能保存，已保留原设置。|Could not save desktop settings. Previous settings remain active.
桌面设置未能保存，快捷键也未能恢复；请重新设置快捷键。|Could not save settings or restore hotkeys. Please configure the hotkeys again.
点击恢复灵动岛|Click to restore the island
显示器位置未能保存，请检查数据目录权限。|Could not save display position. Check data folder permissions.
音频静音状态已切换。|Audio mute toggled.
音频设备暂不可用。|Audio device is currently unavailable.");
            Map(@"按应用设置全屏规则|Fullscreen rules by application
填写进程名称，例如 vlc.exe、chrome.exe。规则仅在该应用前台全屏时生效；未知或受保护进程使用上方全局规则。最多 64 项，修改后点击底部保存。|Enter a process name, such as vlc.exe or chrome.exe. Rules apply only when that app is foreground and fullscreen. Unknown or protected processes use the global rule. Up to 64 rules; save below to apply changes.
只填写进程名称，不需要完整路径|Enter a process name without a full path
跟随全局规则|Use global rule
添加／更新应用规则|Add / update app rule
请输入有效进程名称，例如 vlc.exe。|Enter a valid process name, such as vlc.exe.
最多保留 64 项应用全屏规则。|Up to 64 fullscreen app rules can be saved.
规则已修改，请点击底部保存桌面交互设置。|Rules changed. Save desktop settings below to apply them.
尚无应用规则，所有应用使用全局规则。|No app rules. All apps use the global rule.");
            Map(@"该进程已有规则，请编辑对应规则。|That process already has a rule. Edit its existing rule.");
            Map(@"拖动中按 Esc 撤销到原位置；按住 Alt 松手可临时取消吸附，不改变自动吸附偏好。|Press Esc while dragging to restore the original position. Hold Alt on release to skip snapping for that move without changing your preference.");
            Map(@"已禁用|Disabled");
            Pattern("展开／收起：{0} · 隐私：{1}", "Expand / collapse: {0} · Privacy: {1}");
        }
    }
}
