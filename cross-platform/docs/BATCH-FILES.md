# 批量文件与图片 / Batch files and images

跨平台预览版提供两个紧凑工具入口：**批量重命名 / Batch rename**、**批量图片 / Batch images**。中文和英文共享同一布局；输入分成双列短行，长路径与预览自动换行。它们不调用 shell，不使用 Python，也不覆盖 Windows 版的数据。

## 批量重命名

1. 多选文件，或每行填一个绝对路径。一次 1–200 个文件，源内容合计最多 8 GiB。
2. 设置前缀、后缀、名称查找替换、模板、起始编号和补零位数。模板支持 `{name}` 和 `{n}`，扩展名保持不变；`.profile` 一类点文件作为完整名称处理。
3. 点“生成预览”查看原名、目标名与所在文件夹。修改任意重命名输入后必须重新预览。
4. 点“执行重命名”。最近一批成功操作可在**本次程序运行期间**撤销；开始执行下一批或尝试撤销时，上次撤销权限立即失效（即使新操作随后取消或失败）。退出后不从历史日志自动恢复撤销权限。

预览保存源文件身份、长度、修改时间和 SHA-256。执行与撤销再次检查；内容改变、同内容文件被替换、原名被外部文件占用、重名、链接或不支持的原生能力都会停止。预览不修改源文件。名称比较读取实际目录条目身份，并探测目录大小写行为；最终是否可创建目标始终由原生“不替换”操作决定。目录名允许 Unicode 和空格，不把文件路径当命令执行。

成功更名后，当前会话保留允许外部编辑/替换的身份锚点句柄，以防原文件删除后 inode/文件标识被回收重用。Unix 完成更名即释放建议锁，只保留打开的 inode。新批次开始、撤销尝试结束或程序关闭释放锚点；新事务不会同时保留两批完整文件句柄。系统打开文件/句柄的软限仍可能使大型批次在验证阶段停止，尤其是 macOS 默认 fd 上限较低时；这种情况不会以不安全的无句柄路径继续更名。

更名采用同目录两阶段操作：先把本批源文件移到随机 `.side-island-rename-*.tmp` 名称，再移到目标名称，支持文件名互换和仅大小写变化。取消或异常会尝试按相反顺序恢复原名，恢复同样禁止覆盖外部文件。**关闭正在处理的主窗口会先发出取消并保持程序运行**；结束后再关闭，避免退出切断恢复过程。

异常恢复若遇到外部占用或文件系统故障，可能需要人工恢复。程序会保留临时文件及日志中的具体路径，不删除无法确认身份的文件。不要清理这些 `.side-island-rename-*` 文件，直到核对操作清单。

### 原生安全边界与当前验证范围

- Windows x64：源文件保持只读句柄并排除写入/删除共享，使用句柄更名 `SetFileInformationByHandle(FileRenameInfo)`，`ReplaceIfExists=false`。目录句柄阻止目录被替换；拒绝带重解析点的源及祖先目录。
- Ubuntu Linux x64/arm64：对规范化后的源目录逐级 `openat(O_DIRECTORY|O_NOFOLLOW)` 并保持目录 fd；用 `statx` 的设备/inode/mtime/size 校验身份，`renameat2(RENAME_NOREPLACE)` 更名。文件使用 `O_NOFOLLOW`，不把符号链接作为源处理。旧内核、libc、文件系统缺少这些接口时直接失败，不回退到可能覆盖的 `rename`。
- Apple Silicon macOS：使用公开 Darwin arm64 `struct stat` 布局的 `fstat/fstatat`、固定目录 fd、`renameatx_np(RENAME_EXCL)`；拒绝叶节点符号链接。不猜测 Intel Mac 的 ABI；当前 Intel macOS 不启用批量原生操作。
- 所有平台拒绝多硬链接源。Unix 对源文件的 `flock` 属于建议锁：其他不配合锁的进程仍可能写入或修改目录条目。程序在哈希前后及移动前后验证身份/内容并尝试安全恢复，但这些公开 Unix 接口不能提供针对恶意并发目录修改的完整事务隔离。批量操作期间不要让同步器、编辑器、清理器同时改动这些文件；源目录本身重命名等异常应结合清单人工核对。

`VerifyNativeAbiContract()` 使用固定字节夹具检查 Linux statx 与 Darwin arm64 stat 字段偏移、身份和非法输入，**不代表在另一平台执行了原生调用**。只有实际在该系统运行 `SideIsland.Tests` 的通过记录才属于该平台的本机验证。当前开发机是 Windows；Mac/Ubuntu 原生文件系统行为仍须目标机器验证。

API 依据：[Linux statx](https://man7.org/linux/man-pages/man2/statx.2.html)、[Linux renameat2](https://man7.org/linux/man-pages/man2/renameat2.2.html)、[Linux statx UAPI 布局](https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h)、[Darwin stat 布局](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h)、[Darwin renameatx_np/RENAME_EXCL](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stdio.h)、[Windows FileRenameInfo](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)。

## 批量图片

多选 JPG/PNG，选择已经存在的本机输出文件夹，再设置 JPG/PNG、JPG 质量 1–100、最大宽高。按原比例缩小，不放大；最大输出边长 8192。JPEG 将透明区域合成白底。输出使用 `原名-processed.ext`，已有同名文件时自动追加编号；通过原生独占创建新文件，不覆盖源文件或旧输出。

每次最多 200 张，每张压缩文件最多 64 MiB，解码最多 2400 万像素、单边最多 32768 像素。读取 PNG/JPEG 尺寸头后才交给解码器；图片逐张处理，SHA-256 与处理记录保存本机。工作在后台，缩放、白底合成和编码不阻塞页面切换。源读取使用固定目录/无跟随文件句柄，转换只处理读取到的有界快照。

处理按像素进行，不承诺保留 EXIF、自动方向、ICC 色彩配置、多帧/动画或全部原始元数据。当前仅支持 JPG/PNG，不包含 HEIC、WebP、SVG 等。PNG 是无损重新编码；JPG 质量改变可能增加或减少体积，不承诺每张输出都比源文件小。

取消或失败时已完整输出的图片保留；正在创建的单张文件在创建前检查取消，创建后先完成写入。磁盘写入故障可能留下独立命名的不完整输出，程序不盲删可能已被其他进程替换的文件。该任务停止并将错误和已保存列表记入日志；可检查输出文件夹后重新选择剩余文件。

## 记录与测试

操作记录位于跨平台数据目录下的 `batch-operations/*.jsonl`，包括本机路径、源 SHA-256、原生文件身份、目标/临时名、分步状态、完成/取消/恢复结果。它们不上传；路径和哈希属于本地诊断资料，不应未经检查公开分享。首个持久清单写入并 flush 后才开始更名；磁盘故障时恢复记录会尽力追加，最初清单仍包含全部临时名。

`BatchFileTests.Run()` 检查预览、前后缀、Unicode、编号、模板字面量、冲突、外部修改/替换、同名文件保护、大小写变化、两文件互换、取消后的恢复、会话撤销、源哈希清单和安全新输出。`RunImages()` 在 Avalonia 初始化后检查真实 PNG/JPEG 后台编码结果、透明白底、尺寸、重复输出、解码像素上限及重建界面后预览有效性。测试只在随机生成的 `side-island-batch-tests-*` 临时目录中创建/删除自己的夹具；不操作用户文件。

本文件说明功能和限制；实际通过次数、系统和完整日志以主回归产生的验证记录为准。
