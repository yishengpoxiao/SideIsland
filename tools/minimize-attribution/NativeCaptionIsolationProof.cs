using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Independent proof of a pointer-only own-window test guard. It never
    // blocks WM_SYSCOMMAND, changes styles, moves the pointer or sends input.
    internal static class NativeCaptionIsolationProof
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=4*1024*1024};
        private static IslandWindow Island;private static SettingsWindow Settings;private static HwndSource Source;
        private static IntPtr Hwnd;private static string Output;private static int SystemMinimizeMessages;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect{internal int L,T,R,B;}
        [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out NativeRect r);
        [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(IntPtr h,int index);
        [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr h);

        [STAThread]private static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            try
            {
                if(args.Length==1&&args[0]=="--self-check"){Check(typeof(SettingsWindow).GetField("animatedSections",Private)!=null,"section contract");Log("SELF-CHECK PASS no UI/window hook created");return 0;}
                string root=Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\')+"\\";Output=Path.Combine(root,"artifacts","minimize-attribution","caption-proof-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                for(int i=0;i<args.Length;i+=2){Check(i+1<args.Length,"option value missing");if(args[i]=="--suite")Check(args[i+1]=="NativeCaptionIsolationProof","unknown suite");else if(args[i]=="--output")Output=Path.GetFullPath(args[i+1]);else throw new ArgumentException("unknown option");}
                Check(Output.StartsWith(root,StringComparison.OrdinalIgnoreCase),"output outside project");Directory.CreateDirectory(Output);Run();return 0;
            }
            catch(Exception error){Console.Error.WriteLine("FAIL "+error);return 1;}
        }
        private static void Run()
        {
            string previous=Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"),language=UiLocalization.Language,theme=UiTheme.Name;
            string fixture=Path.Combine(Path.GetTempPath(),"CodexIsland.CaptionProof."+Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",fixture);
                new SettingsStore().Save(new Settings {AlwaysOnTop=false,AutoCollapse=false,NotificationsEnabled=false,HoverExpandEnabled=false,AutoMusic=false,OnlineLyricsEnabled=false,CustomExpandedSize=true,ExpandedWidth=391,ExpandedHeight=638});
                Island=new IslandWindow(true){ShowActivated=false,IsHitTestVisible=false};Island.Loaded+=delegate{StopTimers();};Island.Show();Island.SetExpanded(true,false);Pump(40);
                Settings=new SettingsWindow(new Settings {AlwaysOnTop=false,NotificationsEnabled=false,CodexPath="",CodexHome=""}){Owner=Island,ShowActivated=false,IsHitTestVisible=false};
                AppAppearance.Register(Settings);Settings.Show();Pump(40);Settings.Width=320;Settings.Height=750;Settings.UpdateLayout();Pump(30);
                Hwnd=new WindowInteropHelper(Settings).Handle;ValidateOwn();Source=HwndSource.FromHwnd(Hwnd);Check(Source!=null,"own source missing");
                var points=FindMinButton();Check(points.Count>0,"no native HTMINBUTTON baseline found");
                bool focusedBefore=FocusOwnHeader();Keyboard.ClearFocus();Pump(10);var before=Snapshot();
                var baselines=new List<object>();foreach(var point in points){long hit=Hit(point.X,point.Y);Check(hit==8,"baseline query changed");baselines.Add(new {point.X,point.Y,Hit=hit});}
                Source.AddHook(SystemCommandObserver);
                var isolation=NativeFixtureInputIsolation.Attach(Settings);
                try
                {
                    var guarded=new List<object>();foreach(var point in points){long hit=Hit(point.X,point.Y);Check(hit==0,"pointer-only guard did not return HTNOWHERE");guarded.Add(new {point.X,point.Y,Hit=hit});}
                    var afterQueries=Snapshot();Check(Json.Serialize(before)==Json.Serialize(afterQueries),"guard query changed HWND/rect/style/DPI/layout");
                    bool focusedAfter=FocusOwnHeader();Check(focusedBefore&&focusedAfter,"keyboard focus must remain functional before/after guard");Keyboard.ClearFocus();Pump(10);
                    var afterFocus=Snapshot();Check(Json.Serialize(before)==Json.Serialize(afterFocus),"guard focus path changed window geometry/style/layout");
                    ValidateOwn();Log("CONTROL: pointer guard active; directly sending own SC_MINIMIZE to prove it is not swallowed");
                    SendMessage(Hwnd,0x112,new IntPtr(0xF020),IntPtr.Zero);Until(delegate{return IsIconic(Hwnd);},2000,"direct SC_MINIMIZE");var minimized=Snapshot();
                    Check(SystemMinimizeMessages>0&&Settings.WindowState==WindowState.Minimized,"direct system minimize must remain observable");
                    Settings.WindowState=WindowState.Normal;Until(delegate{return !IsIconic(Hwnd);},2000,"controlled own restore");Pump(30);
                    var restored=Snapshot();Check(Json.Serialize(before)==Json.Serialize(restored),"controlled own restore did not recover baseline");
                    var result=new {Passed=true,UiExecuted=true,OwnWindowsOnly=true,HistoricalActorProven=false,IsolationHelper="tests/NativeFixtureInputIsolation.cs",OriginalIsHitTestVisible=Settings.IsHitTestVisible,OriginalShowActivated=Settings.ShowActivated,PointerMoved=false,SendInputUsed=false,CaptionDownSent=false,StylesModified=false,DirectSystemCommandSuppressed=false,AutoRestoreOnUnexpectedFailure=false,GuardedQueries=isolation.GuardedQueryCount,DirectSystemMinimizeMessages=SystemMinimizeMessages,KeyboardFocusBefore=focusedBefore,KeyboardFocusAfter=focusedAfter,Before=before,AfterQueries=afterQueries,AfterFocus=afterFocus,BaselineMinButtonQueries=baselines,GuardedMinButtonQueries=guarded,ControlledMinimized=minimized,ControlledRestored=restored,Interpretation="Guard changes pointer hit testing only. Direct SC_MINIMIZE still changes state; this proof identifies no historical click or sender."};
                    Save("proof.json",result);Log("PASS baseline IsHitTestVisible=false still HTMINBUTTON8; guard HTNOWHERE0; layout/style/DPI/focus unchanged; direct SC_MINIMIZE still works; historical actor unknown");
                }
                finally{isolation.Dispose();if(Source!=null&&!Source.IsDisposed)Source.RemoveHook(SystemCommandObserver);}
                Log("SUITE PASS:NativeCaptionIsolationProof");
            }
            finally
            {
                if(Settings!=null)Settings.Close();if(Island!=null){StopTimers();Island.Close();}AppAppearance.Configure(language,theme);Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",previous);
                if(Directory.Exists(fixture)&&Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))Directory.Delete(fixture,true);
            }
        }
        private sealed class ScreenPoint{internal int X,Y;}
        private static List<ScreenPoint> FindMinButton()
        {
            NativeRect r;Check(GetWindowRect(Hwnd,out r),"native rectangle missing");var points=new List<ScreenPoint>();
            for(int y=r.T;y<Math.Min(r.B,r.T+70);y+=2){for(int x=Math.Max(r.L,r.R-260);x<r.R;x+=2){if(Hit(x,y)==8){points.Add(new ScreenPoint{X=x,Y=y});if(points.Count>=8)return points;}}}return points;
        }
        private static long Hit(int x,int y){ValidateOwn();long packed=((long)(ushort)y<<16)|(ushort)x;return SendMessage(Hwnd,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt64();}
        private static IntPtr SystemCommandObserver(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled)
        {
            if(message==0x112&&(w.ToInt64()&0xFFF0)==0xF020){SystemMinimizeMessages++;return IntPtr.Zero;}
            return IntPtr.Zero;
        }
        private static bool FocusOwnHeader()
        {
            var sections=(IList)typeof(SettingsWindow).GetField("animatedSections",Private).GetValue(Settings);Check(sections.Count>0,"module section missing");var section=(Expander)sections[0];section.ApplyTemplate();var header=(ToggleButton)section.Template.FindName("PART_Header",section);Check(header!=null,"real header missing");
            header.BringIntoView();Settings.UpdateLayout();Pump(20);var focused=Keyboard.Focus(header);Pump(10);return Object.ReferenceEquals(focused,header)&&section.IsKeyboardFocusWithin;
        }
        private static object Snapshot()
        {
            NativeRect r;Check(GetWindowRect(Hwnd,out r),"native rect unavailable");var root=(FrameworkElement)Settings.Content;var scroll=(ScrollViewer)typeof(SettingsWindow).GetField("settingsScroll",Private).GetValue(Settings);
            return new {Hwnd=Hex(Hwnd),NativeRect=new[]{r.L,r.T,r.R-r.L,r.B-r.T},Style=unchecked((uint)GetWindowLong(Hwnd,-16)),ExtendedStyle=unchecked((uint)GetWindowLong(Hwnd,-20)),Dpi=GetDpiForWindow(Hwnd),LogicalWidth=Settings.Width,LogicalHeight=Settings.Height,ActualWidth=Settings.ActualWidth,ActualHeight=Settings.ActualHeight,LayoutWidth=root.ActualWidth,LayoutHeight=root.ActualHeight,ScrollOffset=scroll.VerticalOffset,WindowState=Settings.WindowState.ToString(),IsHitTestVisible=Settings.IsHitTestVisible,IsEnabled=Settings.IsEnabled};
        }
        private static void ValidateOwn(){uint pid;GetWindowThreadProcessId(Hwnd,out pid);Check(IsWindow(Hwnd)&&pid==Process.GetCurrentProcess().Id,"target HWND no longer belongs to this proof");}
        private static void Until(Func<bool> predicate,int milliseconds,string label){var watch=Stopwatch.StartNew();while(!predicate()&&watch.ElapsedMilliseconds<milliseconds)Pump(10);Check(predicate(),label+" timeout");}
        private static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};StopTimers();timer.Start();Dispatcher.PushFrame(frame);StopTimers();}
        private static void StopTimers(){if(Island==null)return;foreach(var field in typeof(IslandWindow).GetFields(Private))if(field.FieldType==typeof(DispatcherTimer))((DispatcherTimer)field.GetValue(Island)).Stop();}
        private static string Hex(IntPtr value){return "0x"+value.ToInt64().ToString("X",CultureInfo.InvariantCulture);}
        private static void Save(string name,object value){File.WriteAllText(Path.Combine(Output,name),Json.Serialize(value),new UTF8Encoding(false));}
        private static void Log(string value){Console.WriteLine(DateTime.UtcNow.ToString("o")+" "+value);Console.Out.Flush();}
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
