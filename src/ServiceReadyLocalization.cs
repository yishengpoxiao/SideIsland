namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private static void ServiceReadyCatalog()
        {
            Map(@"等待服务或文件就绪|Wait for a service or file
手动开始后监测 TCP 连接、HTTP／HTTPS 的 2xx 状态，或文件大小和修改时间持续稳定。每项只提醒一次，可取消或移除；退出后停止，不自动重启监测。|Start manually to monitor TCP connectivity, HTTP/HTTPS 2xx status, or stable file size and modification time. Each task notifies once and can be canceled or removed. Monitoring stops on exit and does not resume automatically.
提醒名称（可选）|Reminder name (optional)
TCP 主机与端口|TCP host and port
HTTP／HTTPS 地址|HTTP / HTTPS URL
文件持续稳定|Stable file
主机、完整网址或文件绝对路径|Host, full URL or absolute file path
TCP 端口（1—65535）|TCP port (1–65535)
文件稳定时长（1—3600 秒）|File stability duration (1–3600 seconds)
开始就绪提醒|Start readiness watch
选择目标文件|Choose target file
选择要等待稳定的文件|Choose a file to wait until stable
HTTP 只检查响应状态，不读取正文、不跟随重定向，也不发送 Windows 凭据。单次检查约 2.5 秒超时；TCP 可连接只代表端口开始接受连接。|HTTP checks status without reading the body, following redirects or sending Windows credentials. Each check times out after about 2.5 seconds. TCP connectivity only means the port accepts connections.
已开始等待就绪，每项完成后仅提醒一次。|Readiness watch started. Each task notifies once.
尚未开始就绪提醒。填写目标后手动开始。|No readiness watches. Enter a target and start manually.
服务或文件已就绪|Service or file is ready
请输入有效名称和监测目标。|Enter a valid name and target.
连接超时应为 250 至 10000 毫秒。|Connection timeout must be between 250 and 10000 milliseconds.
请输入有效主机名称或 IP，以及 1 至 65535 的端口。|Enter a valid host or IP address and a port between 1 and 65535.
请输入完整 HTTP／HTTPS 地址，不要在地址中包含用户名或密码。|Enter a full HTTP/HTTPS URL without a username or password.
请使用文件的完整绝对路径。|Use the file's full absolute path.
文件稳定时长应为 1 至 3600 秒。|File stability duration must be between 1 and 3600 seconds.
请选择 TCP、HTTP 或文件稳定监测。|Choose TCP, HTTP or file stability monitoring.
最多保留 20 项就绪提醒，请先移除旧任务。|Up to 20 readiness watches can be kept. Remove an old task first.
等待检查|Waiting to check
正在检查…|Checking…
演示模式不连接网络或读取文件。|Demo mode does not connect to the network or read files.
已取消|Canceled
已就绪，已提醒一次|Ready; notification sent once
暂时无法检查，稍后重试。|Cannot check right now. Retrying later.
文件存在，等待大小和修改时间保持稳定。|File exists. Waiting for stable size and modification time.
后台检查繁忙，等待上次连接结束。|Checks are busy. Waiting for previous connections to finish.
连接或文件检查超时，稍后重试。|Connection or file check timed out. Retrying later.
暂时无法连接或读取，稍后重试。|Cannot connect or read right now. Retrying later.
文件尚不存在或不可读取。|File does not exist yet or is not readable.
文件暂不可读取，稍后重试。|File is not readable right now. Retrying later.
TCP 端口已接受连接|TCP port accepts connections
TCP 端口暂未接受连接。|TCP port is not accepting connections yet.
HTTP 服务暂不可用，稍后重试。|HTTP service is unavailable right now. Retrying later.
HTTP 返回重定向，未跟随。|HTTP redirect received and not followed.
HTTP 状态正常|HTTP status is healthy
HTTP 状态暂未正常|HTTP status is not healthy yet
取消监测|Cancel watch");
        }
    }
}
