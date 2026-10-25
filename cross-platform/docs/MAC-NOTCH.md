# Apple Silicon Mac · 刘海优先定位

`SideIsland.Desktop/Platforms/MacNotchBridge.cs` 为 Avalonia 12.1.4 / .NET 10 提供可选的公开 AppKit 桥接。此文件可以在 Windows 上交叉编译；本次没有 Mac 设备，**编译成功不等于 Mac 上的原生调用、Spaces、全屏或菜单栏遮盖已经实测**。

## 显示方式

Mac 默认优先选择真正有摄像头遮挡的显示器。在该屏幕最上方放置黑色岛身，让刘海与岛身连成一块；紧凑态的左右翼放状态和操作，中间物理摄像头区域不放文字、图片或按钮。展开时顶边保持贴屏，正文从摄像头安全区域下方开始。软件不能让物理摄像头区域产生显示像素，也不能让它接收正常屏幕点击。

没有刘海时桥接返回 `false`，由公共定位逻辑回退到顶部居中或用户所选边缘。明确传入 `preferredDisplayId` 时，只在那个真实显示器上尝试定位，不会偷偷把外接屏上的窗口拉回内置屏。该 ID 是公开 `NSScreenNumber` 的十进制显示器编号；不要用列表索引冒充持久化 ID。

检测同时使用 `NSScreen.safeAreaInsets.top` 与 `auxiliaryTopLeftArea`／`auxiliaryTopRightArea`。只有顶部有非零遮挡，且两块可用区域之间存在合理间隙，才把它识别为刘海；没有写死某款 Mac 的刘海宽度。Apple 的辅助区域使用全局 AppKit 坐标，原点在底部，且单位是点。[NSScreen](https://developer.apple.com/documentation/appkit/nsscreen)、[auxiliaryTopLeftArea](https://developer.apple.com/documentation/appkit/nsscreen/auxiliarytopleftarea-uglc)

原生 `setFrame:display:` 全程使用该坐标系，避免把多个不同缩放屏幕上的点坐标按一个 Retina 比例换成像素。返回的 `MacNotchPlacement` 则使用窗口内部顶部原点的点坐标，便于 Avalonia 排版。

## 接口与调用次序

```csharp
MacNotchBridge.TryGetPreferredDisplay(out var display, out var error);

// 先创建/显示 Avalonia 窗口，配置普通窗口装饰、大小约束及 Topmost，
// 再调用。此桥不会自己激活、显示、隐藏窗口，也不会抢键盘焦点。
MacNotchBridge.TryApply(window, expanded, preferNotch: true,
    stayAcrossSpaces: true, showOverFullscreen: false,
    expandedWidth: 520, expandedHeight: 680,
    out var placement, out error);
```

所有调用都必须在 Avalonia UI 主线程执行，另外用 `NSThread.isMainThread` 检查 AppKit 前提。不得在后台计时器上调用。返回 `false` 且 `error == null` 表示当前系统／显示器没有可用的刘海路径，可正常回退；原生句柄尚未创建等问题会给简短诊断。

紧凑态宽度为实测刘海宽度加左右各 84 点的空间，并限于屏幕；高度至少 42 点且比遮挡区多 8 点。展开尺寸会限制到显示器范围。`NotchLeft`、`NotchWidth`、`NotchHeight` 是不可绘制核心的窗口局部边界；展开正文上边距至少使用 `ContentTop`。框架或 AppKit 仍可能施加最小尺寸，因此桥会读回真实窗口框架，而非把请求尺寸伪装为实际尺寸。

后续修改 Avalonia `Topmost` 可能重新设置原生层级；应在这些框架属性更新后重新应用桥接。布局正在由用户拖动时也不应周期性强行重定位。

离开刘海模式（改为侧边／底边、拖离刘海或关闭置顶）时，调用 `TryRestoreWindowPolicy(window, topmost, out error)`，再执行普通边缘定位。它恢复首次进入刘海前的 `collectionBehavior`，按当前用户选择切回普通或浮动层级，同时恢复曾更改的 `hidesOnDeactivate`。记录使用 `ConditionalWeakTable`，不强持有窗口，也不 retain 原生指针；若原生窗口已重建，会拒绝给旧句柄发消息。单纯给 Avalonia `Topmost` 再赋相同值不能代替该还原，因为框架可能不会触发属性更新。

普通／浮动层级使用公开的 `kCGNormalWindowLevelKey = 4`、`kCGFloatingWindowLevelKey = 5` 查询实际值，而非把当前数值写死；[Apple 的标准层级定义](https://developer.apple.com/documentation/coregraphics/cgwindowlevelkey)及[Avalonia 12.1.4 的原生置顶实现](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)说明正常窗口对应普通／浮动层级。恢复路径和 Spaces 行为仍须 Mac 实机验证。

## 原生边界

- Avalonia 12.1.4 的窗口句柄描述符为 `NSWindow`；普通嵌入顶层可能给 `NSView`。桥会检查真实 Objective-C 类型，后者用公开 `window` 属性取得所属窗口；不对未知句柄猜测调用。[对应版本源码](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Native/TopLevelImpl.cs)
- `NSRect` 和 `NSEdgeInsets` 都由四个 64 位 `CGFloat` 组成。Apple Silicon 使用正常 `objc_msgSend` 的结构返回 ABI；Intel x64 使用独立 `objc_msgSend_stret` 签名。不能把 x64 的隐藏结构输出参数调用方式照搬到 arm64。
- 层级用公开的 `CGWindowLevelForKey(kCGStatusWindowLevelKey)`，允许窗口进入菜单栏所在的区域；没有修改屏幕设置、摄像头权限或系统兼容选项。
- `stayAcrossSpaces` 选择公开的 `.canJoinAllSpaces`，并使用浮动覆盖层适合的 `.stationary`／`.ignoresCycle`。只有显式启用 `showOverFullscreen` 才添加 `.fullScreenAuxiliary`。[NSWindow 集合行为](https://developer.apple.com/documentation/appkit/nswindow/collectionbehavior-swift.struct/fullscreenauxiliary)
- **默认不加入其他应用全屏，但这里没有实现全局“当前应用正在全屏”的检测或隐藏服务，不能声称已验证全屏自动避让。** 实际参与全屏空间受 macOS、Stage Manager、窗口角色及应用状态影响，必须在目标 Mac 上复核。

应用包需要审计后设置自身 `Info.plist` 的 `NSPrefersDisplaySafeAreaCompatibilityMode` 为 `false`，才能声明自己已处理摄像头遮挡；这不是修改用户的全局系统设置。Apple 推荐先完成这项审计；本次缺少实机的部分应继续标为待验证。[Apple 的兼容模式说明](https://developer.apple.com/documentation/bundleresources/information-property-list/nsprefersdisplaysafeareacompatibilitymode)

## Mac 上还需执行的验证

启动与退出、菜单栏自动显示／隐藏、鼠标和键盘展开收起、窗口不抢焦点、刘海左右文字、中文输入法、不同文字字号、Retina 与缩放、内外屏主屏切换、拔插屏幕、休眠恢复、Spaces、Stage Manager、其他应用全屏及用户手动隐藏。记录机器型号、macOS 版本、显示器几何和截图时，单独标记实际观察，不用几何单元测试替代这些结果。
