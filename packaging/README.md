# Windows 通知权限与应用身份

Windows 的 `UserNotificationListener` 需要清单中的 `userNotificationListener` 能力和用户亲自授予的通知访问权限。注册应用不等于授予权限；此接口只能取得进入 Windows 通知中心的内容。微信／QQ 的自绘悬浮弹窗不一定进入通知中心。

便携版 `CodexIsland.exe` 始终可以直接启动。通知版使用相同程序的独立副本，位于 `packaging/app`，通过 Windows 注册的应用入口启动。这里不会修改 Codex，也不读取微信／QQ 的聊天数据库。

## 检查与本地安装

在项目目录的 Windows PowerShell 5.1 中运行：

```powershell
.\install-notifications.ps1 -CheckOnly
```

检查不修改系统设置。若开发者模式已经启用，先从托盘菜单退出 Codex Island，再双击 `install-notifications.cmd`，或运行：

```powershell
.\install-notifications.ps1
```

脚本将 EXE、配置和图标复制到 `packaging/app`，然后使用 `Add-AppxPackage -Register` 注册 `CodexIsland.Notifications` 并启动。安装成功后请从开始菜单的 Side Island 启动通知版；直接运行原始 EXE 仍然是便携版，没有包身份。

需要 64 位 Windows 10 2004 或更新版本，以及已经启用的开发者模式。脚本不自动开启开发者模式；缺少前提会停止并给出原因。开发者模式属于系统开发设置，正式分发应使用签名包。不要在注册期间删除或移动 `packaging/app`，否则已注册入口会失效。

完成注册后，在应用中点击启用通知，并亲自处理 Windows 权限提示。拒绝过权限的用户需在 Windows 设置中重新允许。更新程序时，请先退出旧应用，然后再次运行安装脚本。

## 可信签名包

正式分发需要 Windows SDK 的 MakeAppx、SignTool，以及主题为 `CN=CodexIsland` 的代码签名证书。证书必须已经受到目标机器信任；这里的脚本不会生成或导入信任证书。开发者可运行：

```powershell
.\packaging\build-package.ps1 -SigningCertificateThumbprint 'YOUR_EXISTING_CERTIFICATE_THUMBPRINT'
.\install-notifications.ps1 -SignedPackagePath '.\release\CodexIsland.Notifications.msix'
```

没有签名参数时 `build-package.ps1` 仅生成未签名的构建产物，并明确标注为不可正常安装。项目不携带证书或私钥。正式 MSIX 包含独立 EXE，不依赖便携版目录。

清单验证与图标生成只使用 Windows 自带 API，不需要 SDK：

```powershell
.\packaging\validate-manifest.ps1
.\packaging\build-assets.ps1
```

图标尺寸分别为 50×50、44×44、150×150。主程序不嵌入稀疏包 `<msix>` 元数据，确保未注册时仍能运行。

卸载已注册的通知版（不会删除便携版程序）：

```powershell
.\install-notifications.ps1 -Unregister
```

## Microsoft 依据

- [通知监听与用户授权](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)
- [松散文件注册](https://learn.microsoft.com/en-us/windows/apps/develop/testing/loose-file-registration)
- [Add-AppxPackage 参数](https://learn.microsoft.com/en-us/powershell/module/appx/add-appxpackage)
- [清单原生校验接口](https://learn.microsoft.com/en-us/windows/win32/api/appxpackaging/nf-appxpackaging-iappxfactory-createmanifestreader)

已生成身份清单和图标不表示安装已成功；请以脚本输出和程序实际权限状态为准。
