using System;
using System.Threading.Tasks;

namespace CodexIsland
{
    // Nothing in this class schedules a command. Entry points are explicit UI actions.
    // At most one volume driver call and one media command run concurrently.
    internal sealed class CompactMediaController : IDisposable
    {
        private readonly bool demo;
        private readonly Func<string, Task<string>> sender;
        private readonly Func<AudioSnapshot> readAudio;
        private readonly Func<double, bool> writeVolume;
        private bool disposed, commanding, adjusting;
        private int wheelRemainder, pendingSteps;
        internal event Action<string> StatusChanged;
        internal CompactMediaController(bool demoMode, Func<string, Task<string>> send,
            Func<AudioSnapshot> audioReader = null, Func<double, bool> volumeWriter = null)
        { demo = demoMode; sender = send; readAudio = audioReader ?? AudioService.Read; writeVolume = volumeWriter ?? AudioService.SetVolume; }

        internal static bool Supports(MediaSnapshot snapshot, string command)
        {
            return snapshot != null && snapshot.Available && (command == "toggle" ? snapshot.CanToggle : command == "previous" ? snapshot.CanPrevious : command == "next" && snapshot.CanNext);
        }
        internal async Task<bool> SendAsync(MediaSnapshot snapshot, string command)
        {
            if (disposed || demo || commanding || sender == null || !Supports(snapshot, command)) return false;
            commanding = true;
            try
            {
                string result = await Task.Run(() => sender(command));
                if (!disposed) Publish(result); return !disposed;
            }
            catch (Exception) { if (!disposed) Publish("媒体会话暂不可用，请稍后重试"); return false; }
            finally { commanding = false; }
        }
        internal async Task<bool> AdjustVolumeAsync(int delta)
        {
            if (disposed || demo || delta == 0) return false;
            // Precision wheels accumulate partial detents instead of jumping on every event.
            long accumulated = (long)wheelRemainder + delta;
            int steps = (int)Math.Max(-50, Math.Min(50, accumulated / 120));
            wheelRemainder = (int)(accumulated % 120);
            pendingSteps = Math.Max(-50, Math.Min(50, pendingSteps + steps));
            if (adjusting) return true;
            if (pendingSteps == 0) return true;
            adjusting = true;
            try
            {
                while (!disposed && pendingSteps != 0)
                {
                    int current = pendingSteps; pendingSteps = 0;
                    var audio = await Task.Run(readAudio);
                    if (disposed) return false;
                    if (audio == null || !audio.OutputAvailable || Double.IsNaN(audio.Volume) || Double.IsInfinity(audio.Volume)) { pendingSteps = 0; Publish("未找到可调节的音频输出设备"); return false; }
                    double target = Math.Max(0, Math.Min(100, Math.Round(audio.Volume) + current * 2));
                    bool success = await Task.Run(() => writeVolume(target));
                    if (disposed) return false;
                    if (!success) { pendingSteps = 0; Publish("音量设置失败，请检查音频设备"); return false; }
                    Publish(UiLocalization.Format("系统音量 {0}%", Math.Round(target)));
                }
                return true;
            }
            catch (Exception) { pendingSteps = 0; if (!disposed) Publish("音量设置失败，请检查音频设备"); return false; }
            finally { adjusting = false; }
        }
        private void Publish(string value) { var handler = StatusChanged; if (handler != null) handler(value); }
        public void Dispose() { disposed = true; pendingSteps = wheelRemainder = 0; StatusChanged = null; }
    }
}
