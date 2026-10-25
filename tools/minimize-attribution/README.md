# Win32kAttributionCollector

独立诊断程序，不编入应用或 79 套测试。`build-collector.ps1 -SelfCheck` 只编译和检查契约，不创建 ETW session，不启动 UI。

```powershell
Win32kAttributionCollector.exe --preflight --output <项目内尚不存在的目录>
Win32kAttributionCollector.exe --watch <项目内targets.json> --output <项目内尚不存在的目录> --seconds 120
```

`--preflight` 会真实启停唯一 GUID 命名的自有 realtime session；它不是无状态自检。正式模式不写 ETL，只落经过 owned HWND 过滤的数值记录。输出目录必须尚不存在；launcher 的 stdout/stderr 可重定向到它的父目录。

targets 可以晚于 collector 创建，采用 atomic replace 更新：

```json
{"Version":1,"Targets":[{"Hwnd":"0x1234","ProcessId":123,"ThreadId":456,"ProcessStartUtc":"2026-10-25T07:00:00.0000000Z","Role":"settings"}]}
```

每 100ms 重读。只有 HWND 的真实 PID/TID、进程创建时间及项目内 `.runtime`/应用路径验证一致的目标会生效。已关闭 HWND 的缓冲事件只允许最近的历史验证时窗，句柄现已属于另一 PID/TID 时拒绝。`{"Stop":true}` 可优雅停止这一个 collector；最长 900 秒。`ready.json` 表示 consumer 已准备好，`summary.json` 保存 schema、丢失事件、拒绝归属、解析错误、计数和清理状态。

`events.jsonl` 的 `HeaderProcessId/ThreadId` 只是事件生成上下文，**不自动命名为 sender**。Win32k 实际可能把 `message/WParam/LParam` 置零，不能把 missing/zero-message 当成没有外部命令。

对仅使用隔离校准窗口的诊断，可显式加 `--diagnostic-owned-raw 1`：最多 512 个 live-owned 零消息事件保存 payload 类型、数值转换及最多 256 字节的原始数据；非数值字符串被抑制。它用于核对 provider 输出与 decoder，不应在无关窗口或用户输入场景开启。

V4 的 `owned-message-span-start/stop` 为 60→70（send）、62→83（receive）保存 `SpanId/pqmsg/HeaderPID/HeaderTID/CallbackCount/StartQpc/EndQpc`，仅保存已映射 owned start 的 stop。先精确匹配，callback count 不同只允许唯一候选并显式标记。对零消息只存路由元数据，不能由此认定该 span 是 SC_MINIMIZE。必须由 receiver 真实 WndProc 的 **绝对 QPC 与线程/窗口** 严格落入唯一 receive span，再与 send span 配对，并先用已知第二进程 sender 校准。嵌套或歧义、丢包、过期或无配对都不能直接下归因结论。

安全边界：不进行全局键盘 hook，不读取窗口标题/正文，不写 ETL；未匹配 owned HWND 的输入不落盘；不使用仅 receiver PID 的 provider filter，以免把发送侧丢掉。日志最大 100000 行 / 64MiB，active span 最大 2048；达到边界会停止并标记，不能当作完整捕获。只停止自己创建的 GUID 会话；Dispose 失败时仅对同一名称使用 `logman stop` fallback。

## 捕获启动器

`run-capture.ps1 -RunName <fresh-name>` 执行已知命令的受控校准。加 `-ObserveSeconds 1..300` 则只在自己的设置窗口重复原矩阵，不发送最小化命令，也不自动恢复。接收程序提前失败时启动器仍会显式停止自己的 ETW session。`observation-002` 因传入 600 秒超出接收程序上限而无效，不能计作观察结果；原失败日志保留。

`run-regression.ps1 -RunName <fresh-name>` 使用默认 79 套协调入口，额外启用 test-only `CODEX_ISLAND_MINIMIZE_TRACE_DIR`，把准确原测试的 HWND 和绝对 QPC 接到 collector；不替换原矩阵，不恢复窗口状态，不重跑失败套件。正常的 `build.ps1 -Test` 无此环境变量时不创建 ETW session 或额外文件。回归通过只能证明该次执行通过，不能证明历史首轮的触发者。
