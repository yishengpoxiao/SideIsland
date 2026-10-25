using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexIsland
{
    internal sealed class AudioMixerDevice
    {
        internal string Id = "", Name = "";
        internal bool IsDefault;
        public override string ToString() { return Name; }
    }
    internal sealed class AudioMixerSession
    {
        internal string Id = "", DeviceId = "", Name = "";
        internal uint ProcessId;
        internal double Volume;
        internal bool Muted, Active, SystemSounds;
    }
    internal sealed class AudioMixerSnapshot
    {
        internal AudioMixerDevice[] Devices = new AudioMixerDevice[0];
        internal AudioMixerSession[] Sessions = new AudioMixerSession[0];
        internal string DefaultDeviceId = "", SelectedDeviceId = "", Error = "";
    }

    // Every call creates and releases its COM objects on the calling MTA worker.
    // Read never changes volume or endpoint policy. Commands re-resolve stable IDs.
    internal static class AudioMixerService
    {
        private const uint ClsctxAll = 23;
        private static readonly Guid SessionManagerId = typeof(IAudioSessionManager2).GUID;
        internal static double NormalizeVolume(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("音量请输入 0—100。");
            return Math.Max(0, Math.Min(100, value));
        }
        internal static AudioMixerSnapshot Read(string selectedDeviceId)
        {
            var result = new AudioMixerSnapshot(); IMMDeviceEnumerator enumerator = null; IMMDeviceCollection collection = null; IMMDevice selected = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                result.DefaultDeviceId = DefaultId(enumerator, 1);
                Check(enumerator.EnumAudioEndpoints(0, 1, out collection)); uint count; Check(collection.GetCount(out count));
                var devices = new List<AudioMixerDevice>();
                for (uint index = 0; index < Math.Min(count, 64); index++)
                {
                    IMMDevice device = null;
                    try { Check(collection.Item(index, out device)); string id; Check(device.GetId(out id)); devices.Add(new AudioMixerDevice { Id = id, Name = DeviceName(device, id), IsDefault = id == result.DefaultDeviceId }); }
                    finally { Release(device); }
                }
                result.Devices = devices.OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                result.SelectedDeviceId = devices.Any(item => item.Id == selectedDeviceId) ? selectedDeviceId : result.DefaultDeviceId;
                if (String.IsNullOrEmpty(result.SelectedDeviceId) && devices.Count > 0) result.SelectedDeviceId = devices[0].Id;
                if (String.IsNullOrEmpty(result.SelectedDeviceId)) { result.Error = "未发现可用的输出设备。"; return result; }
                Check(enumerator.GetDevice(result.SelectedDeviceId, out selected));
                var sessions = new List<AudioMixerSession>();
                VisitSessions(selected, delegate(IAudioSessionControl2 control, ISimpleAudioVolume volume) {
                    int state; Check(control.GetState(out state)); if (state == 2) return false;
                    string id; uint pid; float level; bool muted;
                    Check(control.GetSessionInstanceIdentifier(out id)); Check(control.GetProcessId(out pid)); Check(volume.GetMasterVolume(out level)); Check(volume.GetMute(out muted));
                    bool system = control.IsSystemSoundsSession() == 0;
                    sessions.Add(new AudioMixerSession { Id = id, DeviceId = result.SelectedDeviceId, ProcessId = pid, Name = SessionName(control, pid, system), Volume = NormalizeVolume(level * 100), Muted = muted, Active = state == 1, SystemSounds = system });
                    return sessions.Count >= 128;
                });
                result.Sessions = sessions.OrderByDescending(item => item.Active).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch (Exception) { result.Error = "音量会话暂不可用，请确认 Windows 音频服务和输出设备正常。"; }
            finally { Release(selected); Release(collection); Release(enumerator); }
            return result;
        }
        internal static void ChangeSession(string deviceId, string sessionId, double? percent, bool? muted)
        {
            if (String.IsNullOrEmpty(deviceId) || String.IsNullOrEmpty(sessionId)) throw new ArgumentException("音频会话已失效，请刷新。" );
            double normalized = percent.HasValue ? NormalizeVolume(percent.Value) : 0;
            IMMDeviceEnumerator enumerator = null; IMMDevice device = null; bool found = false;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator(); Check(enumerator.GetDevice(deviceId, out device));
                VisitSessions(device, delegate(IAudioSessionControl2 control, ISimpleAudioVolume volume) {
                    string id; Check(control.GetSessionInstanceIdentifier(out id)); if (!String.Equals(id, sessionId, StringComparison.Ordinal)) return false;
                    int state; Check(control.GetState(out state)); if (state == 2) return false;
                    var context = new Guid("843A974D-D7D2-45EA-9D7B-01922669CFA4");
                    if (percent.HasValue) Check(volume.SetMasterVolume((float)(normalized / 100), ref context));
                    if (muted.HasValue) Check(volume.SetMute(muted.Value, ref context));
                    found = true; return true;
                });
                if (!found) throw new InvalidOperationException("音频会话已结束，请刷新列表。" );
            }
            finally { Release(device); Release(enumerator); }
        }
        internal static void SwitchDefaultOutput(string deviceId)
        {
            IMMDeviceEnumerator enumerator = null; IMMDevice device = null; IPolicyConfig policy = null;
            var previous = new string[3]; int changed = 0;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator(); Check(enumerator.GetDevice(deviceId, out device)); uint state; Check(device.GetState(out state));
                if (state != 1) throw new InvalidOperationException("输出设备已断开，请刷新。" );
                for (int role = 0; role < 3; role++) previous[role] = DefaultId(enumerator, role);
                // Windows has no documented desktop setter. This compatibility
                // interface is used only after an explicit click and can fail;
                // failures are reported, with every changed role rolled back.
                policy = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) { Check(policy.SetDefaultEndpoint(deviceId, role)); changed++; }
                for (int role = 0; role < 3; role++) if (DefaultId(enumerator, role) != deviceId) throw new InvalidOperationException("Default endpoint verification failed.");
            }
            catch (Exception error)
            {
                bool restored = true;
                if (policy != null) for (int role = changed - 1; role >= 0; role--)
                    try { if (String.IsNullOrEmpty(previous[role]) || policy.SetDefaultEndpoint(previous[role], role) < 0) restored = false; } catch { restored = false; }
                throw new InvalidOperationException(restored ? "当前系统不支持切换，或设备已失效；已保留原默认输出。可打开 Windows 声音设置。" : "切换失败，部分默认角色未能恢复，请在 Windows 声音设置中检查输出设备。", error);
            }
            finally { Release(policy); Release(device); Release(enumerator); }
        }
        private static string DefaultId(IMMDeviceEnumerator enumerator, int role)
        {
            IMMDevice device = null;
            try { if (enumerator.GetDefaultAudioEndpoint(0, role, out device) < 0 || device == null) return ""; string id; Check(device.GetId(out id)); return id ?? ""; }
            finally { Release(device); }
        }
        private static void VisitSessions(IMMDevice device, Func<IAudioSessionControl2, ISimpleAudioVolume, bool> visitor)
        {
            object activated = null; IAudioSessionEnumerator sessions = null;
            try
            {
                Guid id = SessionManagerId; Check(device.Activate(ref id, ClsctxAll, IntPtr.Zero, out activated));
                Check(((IAudioSessionManager2)activated).GetSessionEnumerator(out sessions)); int count; Check(sessions.GetCount(out count));
                for (int index = 0; index < Math.Min(count, 256); index++)
                {
                    IAudioSessionControl2 control = null;
                    try { Check(sessions.GetSession(index, out control)); if (visitor(control, (ISimpleAudioVolume)control)) break; }
                    catch (InvalidCastException) { }
                    catch (COMException) { }
                    finally { Release(control); }
                }
            }
            finally { Release(sessions); Release(activated); }
        }
        private static string SessionName(IAudioSessionControl2 control, uint pid, bool system)
        {
            if (system) return "系统声音";
            if (pid != 0) try { using (var process = Process.GetProcessById((int)pid)) return process.ProcessName; } catch { }
            string display; if (control.GetDisplayName(out display) >= 0 && !String.IsNullOrWhiteSpace(display) && !display.StartsWith("@", StringComparison.Ordinal)) return display;
            return "音频进程 " + pid;
        }
        private static string DeviceName(IMMDevice device, string fallback)
        {
            IPropertyStore properties = null; IntPtr variant = IntPtr.Zero;
            try
            {
                Check(device.OpenPropertyStore(0, out properties)); variant = Marshal.AllocCoTaskMem(IntPtr.Size == 8 ? 24 : 16);
                for (int index = 0; index < (IntPtr.Size == 8 ? 24 : 16); index++) Marshal.WriteByte(variant, index, 0);
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                Check(properties.GetValue(ref key, variant)); if (Marshal.ReadInt16(variant) == 31) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(variant, 8)) ?? fallback;
            }
            catch (Exception) { }
            finally { if (variant != IntPtr.Zero) { PropVariantClear(variant); Marshal.FreeCoTaskMem(variant); } Release(properties); }
            return fallback;
        }
        private static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr value);
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { internal Guid Format; internal uint Id; }
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] private class PolicyConfigClient { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceCollection
        { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value); [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value); [PreserveSig] int Commit();
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(ref Guid guid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid guid, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            [PreserveSig] int RegisterSessionNotification(IntPtr notification); [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr notification); [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioSessionEnumerator
        { [PreserveSig] int GetCount(out int count); [PreserveSig] int GetSession(int index, out IAudioSessionControl2 control); }
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state); [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path); [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping); [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification); [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id); [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid); [PreserveSig] int IsSystemSoundsSession(); [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }
        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float value, ref Guid context); [PreserveSig] int GetMasterVolume(out float value);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context); [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        }
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, out IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }
    }
}
