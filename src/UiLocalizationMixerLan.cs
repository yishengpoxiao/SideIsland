namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void MixerLanCatalog()
        {
            Map(@"♫ 分应用音量|♫ App volume mixer
分应用音量|App volume mixer
应用会话音量 / 输出设备切换|App session volume / Output switching
分别控制所选输出设备上的应用会话。只有点击应用或静音按钮才会改变声音。|Control app sessions on the selected output. Sound changes only when you click Apply or Mute.
输出设备|Output device
刷新会话|Refresh sessions
设为默认输出|Use as default output
Windows 声音设置|Windows sound settings
播放音乐或视频后，对应应用会出现在这里。|Apps appear here after they start playing audio.
系统声音|System sounds
音量会话暂不可用，请确认 Windows 音频服务和输出设备正常。|Audio sessions are unavailable. Check the Windows audio service and output device.
未发现可用的输出设备。|No available output device was found.
音量会话读取失败，请稍后刷新。|Could not read audio sessions. Refresh to retry.
音量请输入 0—100。|Volume must be between 0 and 100.
音频会话已失效，请刷新。|The audio session is unavailable. Refresh the list.
音频会话已结束，请刷新列表。|The audio session has ended. Refresh the list.
输出设备已断开，请刷新。|The output device disconnected. Refresh the list.
当前系统不支持切换，或设备已失效；已保留原默认输出。可打开 Windows 声音设置。|Switching is unavailable or the device disconnected. The original output was retained. You can open Windows sound settings.
切换失败，部分默认角色未能恢复，请在 Windows 声音设置中检查输出设备。|Switching failed and some default roles could not be restored. Check the output in Windows sound settings.
演示模式不修改系统音量或默认设备。|Demo mode does not change volume or default devices.
正在应用声音设置…|Applying sound settings…
声音设置已应用。|Sound settings applied.
声音设置未能应用，原会话可能已结束，请刷新。|Could not apply sound settings. The session may have ended; refresh the list.
正在切换默认输出…|Switching default output…
默认输出已切换。已有应用可能需要重新开始播放。|Default output switched. Existing apps may need to restart playback.
演示模式不打开系统设置。|Demo mode does not open system settings.
无法打开 Windows 声音设置。|Could not open Windows sound settings.
取消静音|Unmute
▦ 局域网传文件|▦ LAN file transfer
局域网传文件|LAN file transfer
手机扫码下载 / 临时文件分享|Phone QR download / Temporary file sharing
选文件后开始临时 HTTP 分享。手机与电脑连接同一网络，扫码打开；不会上传到外部服务器。|Start a temporary HTTP share for selected files. Connect your phone to the same network and scan the QR. Nothing is uploaded to an external server.
分享未启动。|Sharing is off.
选择文件|Choose files
清空选择|Clear selection
网络地址|Network address
刷新网络地址|Refresh addresses
该地址仅供本机访问，手机请选局域网地址。|This address works only on this PC. Choose a LAN address for your phone.
系统防火墙可能要求你允许此应用访问专用网络；应用不会自动修改防火墙。|Windows may ask you to allow this app on private networks. The app does not modify firewall rules.
有效时长（1—120 分钟）|Lifetime (1–120 minutes)
访问口令（可选，最多 32 字符）|Access code (optional, up to 32 characters)
口令在手机页面输入，不放进二维码；分享停止或到期后，链接和当前下载均会结束。|Enter the access code on the phone page; it is not in the QR. Stopping or expiry ends the link and active downloads.
开始分享|Start sharing
停止分享|Stop sharing
复制链接|Copy link
复制二维码|Copy QR
保存二维码|Save QR
最多 20 个文件，每个不超过 20 GB。只提供所选文件的下载，不开放文件夹或上传入口。|Up to 20 files, 20 GB each. Only selected files can be downloaded; no folder browsing or uploads.
演示模式不启动网络分享或读取本机文件。|Demo mode does not start sharing or read local files.
选择要分享的文件|Choose files to share
请选择 1—20 个文件。|Choose 1–20 files.
单个分享文件不能超过 20 GB。|Each shared file must be at most 20 GB.
请选择本机普通文件，不支持网络路径或符号链接。|Choose a regular local file. Network paths and symbolic links are unsupported.
所选文件不存在或不是普通文件。|The selected file is missing or is not a regular file.
请选择有效的本机 IPv4 地址。|Choose a valid local IPv4 address.
分享时长为 1—120 分钟。|Share duration must be 1–120 minutes.
访问口令最多 32 个字符，不能包含控制字符。|The access code must be at most 32 characters without control characters.
端口无效。|Invalid port.
文件已选择，点击开始后才允许下载。|Files selected. Downloads are allowed only after you start sharing.
分享已启动，手机扫码后可下载。|Sharing started. Scan the QR on your phone to download.
分享已停止，链接已失效。|Sharing stopped. The link is no longer valid.
分享已到期，链接已失效。|Sharing expired. The link is no longer valid.
链接已复制。|Link copied.
二维码已复制。|QR copied.
二维码已保存。|QR saved.
保存分享二维码|Save share QR
二维码保存失败，请选择可写的位置。|Could not save the QR. Choose a writable location.
无法监听所选地址，网络可能已断开或端口被占用，请刷新地址后重试。|Could not listen on this address. The network may be disconnected or the port busy. Refresh and retry.
无法读取所选文件，请选择可访问的本机文件。|Could not read the file. Choose an accessible local file.
分享未能启动，请检查文件和网络地址。|Could not start sharing. Check the files and network address.
局域网传输|LAN transfer
正在发送文件。|Sending file.
文件已发送。|File sent.
分享已停止或到期。|Sharing stopped or expired.
传输中断，请重新下载。|Transfer interrupted. Download the file again.");
            Pattern("系统默认：{0}", "System default: {0}");
            Pattern("音频进程 {0}", "Audio process {0}");
            Pattern("剩余：{0} · 已发送：{1} · 完成下载：{2}", "Remaining: {0} · Sent: {1} · Downloads: {2}");
        }
    }
}
