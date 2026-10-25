using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexIsland
{
    // Controlled commands target this receiver's HWNDs only. Observe mode never
    // sends a minimize command and never restores an unexpected minimization.
    internal static class MinimizeOriginCalibrationV8
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=16*1024*1024};
        private static readonly Stopwatch Clock=Stopwatch.StartNew();
        private static readonly List<object> Results=new List<object>(),Modules=new List<object>();
        private sealed class ModuleRange {internal long Base;internal int Size;internal string Name;}
        private static readonly List<ModuleRange> ModuleRanges=new List<ModuleRange>();
        private static string Output,Context="initializing"; private static StreamWriter Events;
        private static IslandWindow Island; private static SettingsWindow Settings; private static NativeWindowStateTrace OwnerTrace,SettingsTrace;
        private static OwnThreadMessageOriginObserverV4 MessageOriginObserver;
        private static IntPtr OwnerHwnd,SettingsHwnd,CbtHook,EventHook; private static bool Controlled,ObservedMinimize;
        private static HookDelegate CbtDelegate; private static WinEventDelegate EventDelegate;
        private static readonly List<HwndSource> Sources=new List<HwndSource>();
        [StructLayout(LayoutKind.Sequential)] private struct Point {internal int X,Y;}
        [StructLayout(LayoutKind.Sequential)] private struct RectNative {internal int L,T,R,B;}
        [StructLayout(LayoutKind.Sequential)] private struct Placement {internal uint Length,Flags,ShowCmd;internal Point Min,Max;internal RectNative Normal;}
        [StructLayout(LayoutKind.Sequential)] private struct InputMessageSource {internal uint DeviceType,OriginId;}
        private delegate IntPtr HookDelegate(int code,IntPtr w,IntPtr l);
        private delegate void WinEventDelegate(IntPtr hook,uint id,IntPtr hwnd,int obj,int child,uint thread,uint time);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out RectNative r);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h,ref Placement p);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPlacement(IntPtr h,ref Placement p);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool ShowWindowAsync(IntPtr h,int command);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind,HookDelegate callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventDelegate callback,uint pid,uint thread,uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern uint InSendMessageEx(IntPtr unused);
        [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("ntdll.dll")] private static extern ushort RtlCaptureStackBackTrace(uint skip,uint capture,[Out] IntPtr[] trace,out uint hash);

        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            try
            {
                if(args.Length==1&&args[0]=="--self-check") {Check(typeof(SettingsWindow).GetField("animatedSections",Private)!=null,"section contract");Console.WriteLine("SELF-CHECK PASS no UI/hook created");return 0;}
                var options=Parse(args); if(options.ContainsKey("--sender")) return Sender(options);
                string root=Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\')+"\\";
                Output=Path.GetFullPath(options.ContainsKey("--output")?options["--output"]:Path.Combine(root,"artifacts","minimize-attribution","calibration-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
                Check(Output.StartsWith(root,StringComparison.OrdinalIgnoreCase),"output must stay in project");Directory.CreateDirectory(Output);
                int observe=options.ContainsKey("--observe-seconds")?Int32.Parse(options["--observe-seconds"],CultureInfo.InvariantCulture):0;
                Check(observe==0||(observe>=1&&observe<=300),"observe1..300seconds");Run(observe);return 0;
            }
            catch(Exception error){Console.Error.WriteLine("FAIL "+error);return 1;}
        }
        private static Dictionary<string,string> Parse(string[] args)
        {
            var result=new Dictionary<string,string>();for(int i=0;i<args.Length;i+=2){Check(i+1<args.Length,"option value missing");result.Add(args[i],args[i+1]);}
            if(result.ContainsKey("--suite"))Check(result["--suite"]=="MinimizeOriginCalibration","unknown suite");return result;
        }
        private static void Run(int observe)
        {
            string oldData=Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"),oldLanguage=UiLocalization.Language,oldTheme=UiTheme.Name;
            string fixture=Path.Combine(Path.GetTempPath(),"CodexIsland.MinimizeOrigin."+Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",fixture);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                new SettingsStore().Save(new Settings {AlwaysOnTop=false,AutoCollapse=false,HoverExpandEnabled=false,NotificationsEnabled=false,AutoMusic=false,OnlineLyricsEnabled=false,CustomExpandedSize=true,ExpandedWidth=391,ExpandedHeight=638});
                Events=new StreamWriter(Path.Combine(Output,"events.jsonl"),false,new UTF8Encoding(false)){AutoFlush=true};
                MessageOriginObserver=OwnThreadMessageOriginObserverV4.Attach(h=>h==OwnerHwnd||h==SettingsHwnd, observation=>Event("OWN_THREAD_MESSAGE_ORIGIN",observation,observation.Qpc));
                Island=new IslandWindow(true){ShowActivated=false,IsHitTestVisible=false};OwnerTrace=NativeWindowStateTrace.Attach(Island,"origin-owner");
                Island.SourceInitialized+=delegate{OwnerHwnd=new WindowInteropHelper(Island).Handle;Attach(OwnerHwnd);PublishTargets();};
                Island.Loaded+=delegate{StopTimers();};Island.Show();Island.SetExpanded(true,false);Pump(50);
                Settings=new SettingsWindow(new Settings {AlwaysOnTop=false,NotificationsEnabled=false,CodexPath="",CodexHome=""}){Owner=Island,ShowActivated=false,IsHitTestVisible=false};
                SettingsTrace=NativeWindowStateTrace.Attach(Settings,"origin-settings");
                Settings.SourceInitialized+=delegate{SettingsHwnd=new WindowInteropHelper(Settings).Handle;Attach(SettingsHwnd);PublishTargets();};
                AppAppearance.Register(Settings);Settings.Show();Pump(40);Settings.Width=320;Settings.Height=750;Settings.UpdateLayout();Pump(40);
                OwnerHwnd=new WindowInteropHelper(Island).Handle;SettingsHwnd=new WindowInteropHelper(Settings).Handle;Own(OwnerHwnd,Process.GetCurrentProcess().Id,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);Own(SettingsHwnd,Process.GetCurrentProcess().Id,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
                foreach(ProcessModule module in Process.GetCurrentProcess().Modules){Modules.Add(new {Name=module.ModuleName,Base=Hex(module.BaseAddress),Size=module.ModuleMemorySize,Path=module.FileName});ModuleRanges.Add(new ModuleRange {Base=module.BaseAddress.ToInt64(),Size=module.ModuleMemorySize,Name=module.ModuleName});}
                Save("modules.json",Modules);Attach(OwnerHwnd);Attach(SettingsHwnd);
                CbtDelegate=Cbt;CbtHook=SetWindowsHookEx(5,CbtDelegate,IntPtr.Zero,GetCurrentThreadId());Check(CbtHook!=IntPtr.Zero,"own-thread CBT hook unavailable");
                EventDelegate=WinEvent;EventHook=SetWinEventHook(0x0016,0x0017,IntPtr.Zero,EventDelegate,(uint)Process.GetCurrentProcess().Id,0,0);Check(EventHook!=IntPtr.Zero,"own-process WinEvent hook unavailable");
                PublishTargets();Log("READY targets="+Path.Combine(Output,"targets.json"));Event("ready",null);
                CaptionProbe();
                if(observe>0) Observe(observe); else
                {
                    Controlled=true;SafeMouseMessageControl();Step("own-managed",false,false);Step("own-send-sc",false,false);
                    foreach(string kind in new[]{"send-sc","timeout-sc","post-sc","nested-own-send","show-window","show-window-async","placement"})Step(kind,true,false);
                    Step("owner-managed",false,true);
                }
                Controlled=false;Context=observe>0?"OBSERVE/final-flush-hold":"CONTROLLED/final-flush-hold-no-commands";
                Event("receiver-hold-begin",new {Milliseconds=2000,CommandsSent=false});Pump(2000);Event("receiver-hold-end",Snapshot());
                if(observe==0)Check(!ObservedMinimize,"unexpected minimize outside a controlled action");
                Save("result.json",new {Completed=true,Controlled=observe==0,ObservedMinimize=ObservedMinimize,OriginalHistoricalSenderProven=false,Steps=Results,ElapsedMilliseconds=Clock.ElapsedMilliseconds});Log("SUITE PASS:MinimizeOriginCalibration");
            }
            finally
            {
                if(CbtHook!=IntPtr.Zero)UnhookWindowsHookEx(CbtHook);if(EventHook!=IntPtr.Zero)UnhookWinEvent(EventHook);foreach(var source in Sources)if(!source.IsDisposed)source.RemoveHook(WindowMessage);
                if(Settings!=null)Settings.Close();if(Island!=null){StopTimers();Island.Close();}if(SettingsTrace!=null)SettingsTrace.Dispose();if(OwnerTrace!=null)OwnerTrace.Dispose();if(MessageOriginObserver!=null){MessageOriginObserver.Dispose();Event("MESSAGE_ORIGIN_OBSERVER_SUMMARY",new {MessageOriginObserver.CallbackErrors,MessageOriginObserver.CleanupErrors,MessageOriginObserver.QueueRecords,MessageOriginObserver.MouseRecords,MessageOriginObserver.CallEntryRecords,MessageOriginObserver.CallReturnRecords,MessageOriginObserver.CallPairingErrors,MessageOriginObserver.ActiveCallSpans,MessageOriginObserver.NonScReturnParameterDifferences,OnlyOwnThread=true,OwnHwndPredicate=true,NoMessagesChanged=true});MessageOriginObserver=null;}if(Events!=null)Events.Dispose();Events=null;
                AppAppearance.Configure(oldLanguage,oldTheme);Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",oldData);
                if(Directory.Exists(fixture)&&Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))Directory.Delete(fixture,true);
            }
        }
        private static object Target(IntPtr hwnd,string role){uint pid;uint thread=GetWindowThreadProcessId(hwnd,out pid);return new {Hwnd=Hex(hwnd),ProcessId=pid,ThreadId=thread,ProcessStartUtc=Process.GetProcessById((int)pid).StartTime.ToUniversalTime().ToString("o"),Role=role};}
        private static void PublishTargets()
        {
            var targets=new List<object>();if(OwnerHwnd!=IntPtr.Zero&&IsWindow(OwnerHwnd))targets.Add(Target(OwnerHwnd,"owner"));if(SettingsHwnd!=IntPtr.Zero&&IsWindow(SettingsHwnd))targets.Add(Target(SettingsHwnd,"settings"));
            string path=Path.Combine(Output,"targets.json"),temporary=path+".tmp";File.WriteAllText(temporary,Json.Serialize(new {Version=1,Targets=targets}),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            Event("targets-published",new {Count=targets.Count,Source="SourceInitialized or ready"});
        }
        private static void Attach(IntPtr hwnd){var source=HwndSource.FromHwnd(hwnd);Check(source!=null,"own HwndSource missing");if(Sources.Contains(source))return;source.AddHook(WindowMessage);Sources.Add(source);}
        private static IntPtr WindowMessage(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled)
        {
            long entryQpc=Stopwatch.GetTimestamp();
            if(m==0x8007 && h==SettingsHwnd && Controlled && Context=="CONTROLLED/nested-own-send" && w.ToInt64()==0xC0DE)
            {
                Event("CONTROLLED_NESTED_HOST_ENTRY",new {HostMessage=m,Hwnd=Hex(h),OuterInSendMessageEx=InSendMessageEx(IntPtr.Zero),ActualSCIssuerProcessId=Process.GetCurrentProcess().Id,ActualSCIssuerThreadId=GetCurrentThreadId(),Meaning="Known remote custom-message host; receiver deliberately issues the nested SC. Outer requester is not the direct SC sender."},entryQpc);
                SendMessage(h,0x112,new IntPtr(0xF020),IntPtr.Zero);
                Event("CONTROLLED_NESTED_HOST_RETURN",new {Hwnd=Hex(h),StillInSendMessageEx=InSendMessageEx(IntPtr.Zero)});
                handled=true;return IntPtr.Zero;
            }
            if(m==0x112||m==5||m==0x18||m==0x82||(m>=0xA1&&m<=0xA9))
            {Event("WM",new {Hwnd=Hex(h),Message=m,W=Hex(w),L=Hex(l),ISMEX=InSendMessageEx(IntPtr.Zero),InputSource=(m==0x112||(m>=0xA1&&m<=0xA9))?InputSource():null,NativeStack=Stack()},entryQpc);if(!Controlled&&m==5&&w.ToInt64()==1)ObservedMinimize=true;}
            return IntPtr.Zero;
        }
        private static IntPtr Cbt(int code,IntPtr w,IntPtr l){long entryQpc=Stopwatch.GetTimestamp();try{if(code==1&&(w==OwnerHwnd||w==SettingsHwnd))Event("HCBT_MINMAX",new {Hwnd=Hex(w),Command=l.ToInt64(),ThreadId=GetCurrentThreadId(),ISMEX=InSendMessageEx(IntPtr.Zero),InputSource=InputSource(),NativeStack=Stack()},entryQpc);}catch(Exception error){Console.Error.WriteLine("CBT observation error "+error.GetType().Name);}return CallNextHookEx(CbtHook,code,w,l);}
        private static object InputSource(){uint thread=GetCurrentThreadId();try{InputMessageSource value;bool available=GetCurrentInputMessageSource(out value);int error=available?0:Marshal.GetLastWin32Error();return new {Available=available,CaptureThreadId=thread,ThreadLocalContext=true,DeviceType=value.DeviceType,OriginId=value.OriginId,LastError=error,Interpretation="Current receiver-thread input context only; does not identify a sender process."};}catch(EntryPointNotFoundException){return new {Available=false,CaptureThreadId=thread,ThreadLocalContext=true,Reason="API unavailable"};}}
        private static void WinEvent(IntPtr hook,uint id,IntPtr h,int obj,int child,uint thread,uint time){long entryQpc=Stopwatch.GetTimestamp();try{if(h==OwnerHwnd||h==SettingsHwnd)Event("WinEvent",new {Hwnd=Hex(h),Id=id,EventThreadId=thread,HookThreadId=GetCurrentThreadId(),EventTime=time,ObjectId=obj,ChildId=child},entryQpc);}catch(Exception error){Console.Error.WriteLine("WinEvent observation error "+error.GetType().Name);}}
        private static object[] Stack(){var frames=new IntPtr[24];uint hash;ushort count=RtlCaptureStackBackTrace(1,24,frames,out hash);var names=new List<object>();for(int i=0;i<count;i++){string moduleName="unmapped";long offset=0;foreach(var module in ModuleRanges){if(frames[i].ToInt64()>=module.Base&&frames[i].ToInt64()<module.Base+module.Size){moduleName=module.Name;offset=frames[i].ToInt64()-module.Base;break;}}names.Add(new {Address=Hex(frames[i]),Module=moduleName,Offset="0x"+offset.ToString("X")});}return names.ToArray();}
        private static void CaptionProbe()
        {
            RectNative r;Check(GetWindowRect(SettingsHwnd,out r),"caption rectangle missing");var hits=new List<object>();
            for(int y=r.T;y<Math.Min(r.B,r.T+70);y+=2)for(int x=Math.Max(r.L,r.R-260);x<r.R;x+=2)
            {long packed=((long)(ushort)y<<16)|(ushort)x;long hit=SendMessage(SettingsHwnd,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt64();if(hit==8){hits.Add(new {X=x,Y=y,Hit=hit});if(hits.Count>=8)break;}if(hits.Count>=8)break;}
            Save("caption-hit-test.json",new {IsHitTestVisible=Settings.IsHitTestVisible,ShowActivated=Settings.ShowActivated,HitMinButtonPoints=hits,Interpretation="WM_NCHITTEST only; no pointer movement or button click sent."});Event("caption-query",new {MinButtonHitCount=hits.Count});
        }
        private static void SafeMouseMessageControl()
        {
            Context="CONTROLLED/safe-own-NCLBUTTONUP-HTCLIENT";SettingsTrace.Mark(Context);OwnerTrace.Mark(Context);
            Check(!IsIconic(SettingsHwnd)&&Settings.WindowState==WindowState.Normal,"safe mouse control requires a normal settings window");
            object before=Snapshot();object inputBefore=InputSource();RectNative rectangle;Check(GetWindowRect(SettingsHwnd,out rectangle),"safe mouse control rectangle unavailable");
            int x=rectangle.L+Math.Min(30,Math.Max(1,(rectangle.R-rectangle.L)/2)),y=rectangle.T+Math.Min(80,Math.Max(1,(rectangle.B-rectangle.T)/2));
            Check(x>=rectangle.L&&x<rectangle.R&&y>=rectangle.T&&y<rectangle.B,"safe mouse point must remain within own window");
            Own(SettingsHwnd,Process.GetCurrentProcess().Id,Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            Event("safe-mouse-control-before",new {Message=0xA2,WParam=1,X=x,Y=y,InputSource=inputBefore,Before=before,PointerMoved=false,ButtonDownSent=false,SendInputUsed=false});
            long packed=((long)(ushort)y<<16)|(ushort)x;long result=SendMessage(SettingsHwnd,0xA2,new IntPtr(1),new IntPtr(packed)).ToInt64();Pump(40);
            Check(!IsIconic(SettingsHwnd)&&Settings.WindowState==WindowState.Normal&&IsWindowVisible(SettingsHwnd),"safe NCLBUTTONUP changed the normal settings state");
            object after=Snapshot();var record=new {Kind="safe-own-nclbuttonup-htclient",ExplicitControlledCommand=true,Message=0xA2,WParam=1,ScreenX=x,ScreenY=y,SenderProcessId=Process.GetCurrentProcess().Id,SenderThreadId=GetCurrentThreadId(),TargetHwnd=Hex(SettingsHwnd),NativeReturn=result,InputSourceBefore=inputBefore,InputSourceAfter=InputSource(),Before=before,After=after,NormalBeforeAndAfter=true,MinimizeExpected=false,PointerMoved=false,ButtonDownSent=false,SendInputUsed=false,HistoricalSenderProven=false};
            Results.Add(record);Save("safe-mouse-message-control.json",record);Event("safe-mouse-control-after",record);Log("CONTROL PASS safe NCLBUTTONUP/HTCLIENT; no Down, no pointer movement, settings remains Normal");
        }
        private static void Step(string kind,bool external,bool owner)
        {
            Check(Clock.Elapsed.TotalSeconds<60,"controlled run deadline");Context="CONTROLLED/"+kind;SettingsTrace.Mark(Context);OwnerTrace.Mark(Context);IntPtr target=owner?OwnerHwnd:SettingsHwnd;object before=Snapshot();Event("action-before",new {Kind=kind,External=external,Target=Hex(target)});int senderPid=Process.GetCurrentProcess().Id;
            if(external)senderPid=Child(kind,target);else if(kind=="own-managed")Settings.WindowState=WindowState.Minimized;else if(kind=="owner-managed")Island.WindowState=WindowState.Minimized;else SendMessage(target,0x112,new IntPtr(0xF020),IntPtr.Zero);
            Until(delegate{return IsIconic(target);},5000,"expected controlled minimize");Pump(40);object minimized=Snapshot();Event("action-minimized",new {Kind=kind,SenderProcessId=senderPid});
            if(kind=="own-managed"){Console.Error.WriteLine("EXPECTED CONTROL TRACE; not a historical sender attribution.");SettingsTrace.DumpFailure("controlled own managed minimize");OwnerTrace.DumpFailure("controlled own managed minimize");}
            if(owner)Island.WindowState=WindowState.Normal;else Settings.WindowState=WindowState.Normal;
            Until(delegate{return !IsIconic(target)&&IsWindowVisible(SettingsHwnd);},5000,"controlled restore");Pump(40);
            var record=new {Kind=kind,ExplicitControlledCommand=true,External=external,SenderProcessId=senderPid,Before=before,Minimized=minimized,Restored=Snapshot(),HistoricalSenderProven=false};Results.Add(record);Save("steps.json",Results);Log("CONTROL PASS "+kind+" sender="+senderPid);
        }
        private static int Child(string kind,IntPtr target)
        {
            var owner=Process.GetCurrentProcess();string log=Path.Combine(Output,"sender-"+kind+".json");string args="--sender "+kind+" --target-hwnd "+Hex(target)+" --target-pid "+owner.Id+" --target-start-ticks "+owner.StartTime.ToUniversalTime().Ticks+" --log "+Quote(log);
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Environment.CurrentDirectory};
            using(var child=Process.Start(start)){var stdout=new StringBuilder();var stderr=new StringBuilder();child.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(stdout)stdout.AppendLine(e.Data);};child.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(stderr)stderr.AppendLine(e.Data);};child.BeginOutputReadLine();child.BeginErrorReadLine();Until(delegate{return child.HasExited;},6500,"sender completion (receiver must pump)");child.WaitForExit();File.WriteAllText(Path.Combine(Output,"sender-"+kind+".stdout.log"),stdout.ToString());File.WriteAllText(Path.Combine(Output,"sender-"+kind+".stderr.log"),stderr.ToString());Check(child.ExitCode==0,"sender failed "+kind+": "+stderr);return child.Id;}
        }
        private static int Sender(Dictionary<string,string> options)
        {
            string kind=options["--sender"],hwndText=options["--target-hwnd"];IntPtr h=new IntPtr(Int64.Parse(hwndText.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture));int pid=Int32.Parse(options["--target-pid"]);long ticks=Int64.Parse(options["--target-start-ticks"]);Own(h,pid,ticks);
            string log=Path.GetFullPath(options["--log"]),root=Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\')+"\\";Check(log.StartsWith(root,StringComparison.OrdinalIgnoreCase),"sender log outside project");
            var started=DateTime.UtcNow;uint tid=GetCurrentThreadId();object before=Native(h);long result=0;int error=0;
            if(kind=="send-sc")result=SendMessage(h,0x112,new IntPtr(0xF020),IntPtr.Zero).ToInt64();
            else if(kind=="nested-own-send")result=SendMessage(h,0x8007,new IntPtr(0xC0DE),IntPtr.Zero).ToInt64();
            else if(kind=="timeout-sc"){IntPtr reply;result=SendMessageTimeout(h,0x112,new IntPtr(0xF020),IntPtr.Zero,2,4000,out reply).ToInt64();error=Marshal.GetLastWin32Error();Check(result!=0,"SendMessageTimeout failed");}
            else if(kind=="post-sc"){Check(PostMessage(h,0x112,new IntPtr(0xF020),IntPtr.Zero),"PostMessage failed");result=1;}
            else if(kind=="show-window")result=ShowWindow(h,6)?1:0;
            else if(kind=="show-window-async"){Check(ShowWindowAsync(h,6),"ShowWindowAsync failed");result=1;}
            else if(kind=="placement"){var placement=new Placement{Length=(uint)Marshal.SizeOf(typeof(Placement))};Check(GetWindowPlacement(h,ref placement),"GetWindowPlacement failed");placement.ShowCmd=2;Check(SetWindowPlacement(h,ref placement),"SetWindowPlacement failed");result=1;}
            else throw new ArgumentException("unsupported controlled sender");
            File.WriteAllText(log,Json.Serialize(new {Controlled=true,SenderProcessId=Process.GetCurrentProcess().Id,SenderThreadId=tid,ReceiverProcessId=pid,ReceiverHwnd=hwndText,Command=kind,StartedUtc=started.ToString("o"),EndedUtc=DateTime.UtcNow.ToString("o"),NativeReturn=result,LastError=error,Before=before,After=Native(h),HistoricalSenderProven=false}),new UTF8Encoding(false));Console.WriteLine("CONTROL SENDER PASS "+kind);return 0;
        }
        private static void Own(IntPtr h,int pid,long ticks){uint actual;GetWindowThreadProcessId(h,out actual);Check(IsWindow(h)&&actual==pid,"HWND identity changed");using(var process=Process.GetProcessById(pid)){Check(process.StartTime.ToUniversalTime().Ticks==ticks,"PID creation changed");Check(String.Equals(Path.GetFullPath(process.MainModule.FileName),Path.GetFullPath(Assembly.GetExecutingAssembly().Location),StringComparison.OrdinalIgnoreCase),"receiver is not this project calibration executable");}}
        private static object Native(IntPtr h){RectNative r;bool rect=GetWindowRect(h,out r);var p=new Placement{Length=(uint)Marshal.SizeOf(typeof(Placement))};bool placement=GetWindowPlacement(h,ref p);uint pid;uint tid=GetWindowThreadProcessId(h,out pid);return new {Hwnd=Hex(h),ProcessId=pid,ThreadId=tid,Valid=IsWindow(h),Visible=IsWindowVisible(h),Iconic=IsIconic(h),RectAvailable=rect,Rectangle=new[]{r.L,r.T,r.R-r.L,r.B-r.T},PlacementAvailable=placement,ShowCmd=p.ShowCmd};}
        private static object Snapshot(){return new {Owner=Native(OwnerHwnd),Settings=Native(SettingsHwnd),OwnerManagedState=Island.WindowState.ToString(),SettingsManagedState=Settings.WindowState.ToString(),MinimizedMetricWidth=GetSystemMetrics(57),MinimizedMetricHeight=GetSystemMetrics(58)};}
        private static void Observe(int seconds)
        {
            Controlled=false;Context="OBSERVE/no-minimize-commands";Log("OBSERVE BEGIN seconds="+seconds+"; no minimize/restore commands");var observe=Stopwatch.StartNew();int cycle=0;
            var sections=(IList)typeof(SettingsWindow).GetField("animatedSections",Private).GetValue(Settings);Check(sections.Count>0,"settings section missing");var section=(SmoothSettingsSection)sections[0];var scroll=(ScrollViewer)typeof(SettingsWindow).GetField("settingsScroll",Private).GetValue(Settings);
            while(observe.Elapsed.TotalSeconds<seconds&&!ObservedMinimize)
            {
                string language=(cycle/8)%2==0?"zh":"en",theme=(cycle/4)%2==0?"light":"dark";int width=(cycle/2)%2==0?320:590,height=cycle%2==0?360:750;
                Context="OBSERVE/"+cycle+"/"+language+"/"+theme+"/"+width+"x"+height;SettingsTrace.Mark(Context);OwnerTrace.Mark(Context);section.IsExpanded=false;section.SettleMotion();AppAppearance.Configure(language,theme);Settings.Width=width;Settings.Height=height;Settings.UpdateLayout();Pump(1);scroll.ScrollToTop();Pump(1);
                foreach(bool open in new[]{true,false,true,false}){if(ObservedMinimize||observe.Elapsed.TotalSeconds>=seconds)break;if(!open&&section.IsExpanded){scroll.ScrollToBottom();Pump(1);}section.IsExpanded=open;var wait=Stopwatch.StartNew();while(section.IsMotionActive&&wait.ElapsedMilliseconds<1800&&!ObservedMinimize&&observe.Elapsed.TotalSeconds<seconds){Pump(12);if(IsIconic(SettingsHwnd)||IsIconic(OwnerHwnd))ObservedMinimize=true;}Check(!section.IsMotionActive||ObservedMinimize||observe.Elapsed.TotalSeconds>=seconds,"section natural completion timed out");}
                cycle++;
            }
            if(ObservedMinimize){Event("UNEXPECTED-MINIMIZE-PAUSE",Snapshot());SettingsTrace.DumpFailure("observe actual minimize; receiver will not restore");OwnerTrace.DumpFailure("observe actual minimize; receiver will not restore");Log("OBSERVE MINIMIZE DETECTED; pausing without restore until observation deadline");while(observe.Elapsed.TotalSeconds<seconds)Pump(50);}
            Save("observe.json",new {Seconds=seconds,Cycles=cycle,MinimizeObserved=ObservedMinimize,CommandsSent=false,AutomaticRestore=false,Final=Snapshot()});Log("OBSERVE END cycles="+cycle+" minimized="+ObservedMinimize);
        }
        private static void Until(Func<bool> predicate,int milliseconds,string label){var wait=Stopwatch.StartNew();while(!predicate()&&wait.ElapsedMilliseconds<milliseconds)Pump(10);Check(predicate(),label+" timed out");}
        private static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};StopTimers();timer.Start();Dispatcher.PushFrame(frame);StopTimers();}
        private static void StopTimers(){if(Island==null)return;foreach(var field in typeof(IslandWindow).GetFields(Private))if(field.FieldType==typeof(DispatcherTimer))((DispatcherTimer)field.GetValue(Island)).Stop();}
        private static void Event(string kind,object detail,long entryQpc=0){long qpc=entryQpc==0?Stopwatch.GetTimestamp():entryQpc;if(Events==null)return;Events.WriteLine(Json.Serialize(new {Utc=DateTime.UtcNow.ToString("o"),Qpc=qpc,QpcFrequency=Stopwatch.Frequency,ElapsedMs=Clock.ElapsedMilliseconds,Event=kind,Context=Context,Controlled=Controlled,ReceiverProcessId=Process.GetCurrentProcess().Id,CurrentThreadId=GetCurrentThreadId(),Detail=detail}));}
        private static void Save(string name,object value){File.WriteAllText(Path.Combine(Output,name),Json.Serialize(value),new UTF8Encoding(false));}
        private static void Log(string text){Console.WriteLine(DateTime.UtcNow.ToString("o")+" "+text);Console.Out.Flush();}
        private static string Hex(IntPtr value){return "0x"+value.ToInt64().ToString("X",CultureInfo.InvariantCulture);}
        private static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
