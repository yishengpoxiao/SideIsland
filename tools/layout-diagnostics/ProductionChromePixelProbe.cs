using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bitmap = System.Drawing.Bitmap;

namespace CodexIsland
{
    // Native pixels from the actual demo IslandWindow, after its real page/tool/
    // picker drivers finish naturally. No RTB, global input or product edits.
    internal static class ProductionChromePixelProbe
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly List<object> Records = new List<object>();
        private static readonly List<string> Issues = new List<string>();
        private static IslandWindow window;
        private static string output;
        private sealed class Capture
        {
            internal string PixelHash, GlyphHash;
            internal byte[] Pixels;
            internal Rect Box, Screen;
            internal int Width, Height;
            internal List<object> Glyphs;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                VerifyBuild();
                if (args.Length == 1 && args[0] == "--self-check")
                {
                    foreach (string field in new[] { "expandedHint", "expandedHeader", "toolPicker", "settings", "pageTransitionActive", "toolTransitionActive" })
                        Check(typeof(IslandWindow).GetField(field, Private) != null, "missing production field " + field);
                    Log("SELF-CHECK PASS UI-created=false matrix=zh/dark/391,en/light/560 routes=messages,status,tools-focus,tools-text,messages-return");
                    return 0;
                }
                string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                output = Path.Combine(project, "artifacts", "production-chrome-pixels-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                for (int index = 0; index < args.Length; index += 2)
                {
                    Check(index + 1 < args.Length, "each argument requires a value");
                    if (args[index] == "--output") output = Path.GetFullPath(args[index + 1]);
                    else if (args[index] != "--suite") throw new ArgumentException("Use --output project-directory and optional --suite ProductionChromePixelProbe.");
                }
                Check(output.StartsWith(project, StringComparison.OrdinalIgnoreCase), "output must remain in the project");
                Directory.CreateDirectory(output); Run();
                Console.WriteLine("PROBE PASS:ProductionChromePixelProbe"); Console.Out.Flush(); return 0;
            }
            catch (Exception error) { Log("FAIL " + error); if (output != null && Directory.Exists(output)) Save(); return 1; }
        }

        private static void Run()
        {
            string oldDirectory = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), oldLanguage = UiLocalization.Language, oldTheme = UiTheme.Name;
            var oldContext = SynchronizationContext.Current;
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fixture = Path.GetFullPath(Path.Combine(temporaryRoot, "CodexIsland.ProductionChromePixel." + Guid.NewGuid().ToString("N")));
            Check(fixture.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "fixture must remain in temporary root");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", fixture);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                new SettingsStore().Save(new Settings { Language="zh", Theme="dark", AlwaysOnTop=true, AutoCollapse=false,
                    HoverExpandEnabled=false, NotificationsEnabled=false, AutoMusic=false, OnlineLyricsEnabled=false,
                    CustomExpandedSize=true, ExpandedWidth=391, ExpandedHeight=617, HiddenTools="", FavoriteTools="" });
                window = new IslandWindow(true) { ShowActivated=false, Topmost=true };
                window.Loaded += delegate { StopTimers(); }; window.Show(); window.SetExpanded(true, false); window.UpdateLayout(); WaitSettled();
                Check(Read<bool>(window, "demo"), "only an isolated demo window is allowed");
                var settings = Read<Settings>(window, "settings");
                var footer = Read<TextBlock>(window, "expandedHint"); var header = Read<Grid>(window, "expandedHeader");
                int cells = 0, routes = 0;
                foreach (string locale in new[] { "zh", "en" }) foreach (string theme in new[] { "light", "dark" }) foreach (int width in new[] { 391, 560 })
                {
                    if (!((locale=="zh" && theme=="dark" && width==391) || (locale=="en" && theme=="light" && width==560))) continue;
                    string cell = locale + "-" + theme + "-" + width;
                    Log("CELL BEGIN " + cell);
                    settings.Language=locale; settings.Theme=theme; settings.ExpandedWidth=width;
                    Call(window,"ApplyAppearance"); window.SetExpanded(true,false); window.Topmost=true; window.UpdateLayout(); WaitSettled();
                    Rect expectedNative = NativeBounds(); Capture firstFooter=null, firstHeader=null;
                    try
                    {
                        foreach (string route in new[] { "messages", "status", "tools-focus", "tools-text", "messages-return" })
                        {
                            Log("ROUTE BEGIN " + cell + "/" + route);
                            if (route.StartsWith("messages", StringComparison.Ordinal)) Call(window,"SelectPage",true);
                            else if (route=="status") Call(window,"SelectPage",false);
                            else { Call(window,"SelectToolsPage"); Call(window,"SelectTool",route=="tools-text"?"text":"focus"); }
                            WaitSettled();
                            if (NativeBounds()!=expectedNative) Issues.Add(cell+"/"+route+": fixed expanded HWND changed");
                            Capture actualFooter=CaptureStable(footer,cell+"-"+route+"-footer"), actualHeader=CaptureStable(header,cell+"-"+route+"-header");
                            string[] differences;
                            if (firstFooter==null) { firstFooter=actualFooter; firstHeader=actualHeader; differences=new string[0]; }
                            else
                            {
                                var changed=new List<string>(); Compare(firstFooter,actualFooter,"footer",changed); Compare(firstHeader,actualHeader,"header",changed);
                                differences=changed.ToArray(); foreach(string difference in differences) Issues.Add(cell+"/"+route+": "+difference);
                            }
                            Records.Add(new { Cell=cell,Locale=locale,Theme=theme,Width=width,Route=route,NativeBounds=NativeBounds().ToString(CultureInfo.InvariantCulture),
                                Footer=Record(actualFooter),Header=Record(actualHeader),DifferencesFromMessages=differences }); routes++; Save();
                            Log("ROUTE END "+cell+"/"+route+" footer-sha256="+actualFooter.PixelHash+" header-sha256="+actualHeader.PixelHash+" differences="+differences.Length);
                        }
                    }
                    finally { }
                    cells++; Log("CELL END "+cell);
                }
                Check(cells==2 && routes==10,"the fixed small matrix did not complete"); Save();
                Check(Issues.Count==0,"native chrome pixel/pose changes: "+String.Join("; ",Issues.ToArray()));
                Log("PASS cells=2 routes=10 footer+header native pixels exactly identical within each locale/theme/width; glyph baselines and boxes stable; tabs intentionally excluded; RTB=false");
            }
            finally
            {
                if(window!=null){StopTimers();window.Close();window=null;}
                AppAppearance.Configure(oldLanguage,oldTheme); SynchronizationContext.SetSynchronizationContext(oldContext);
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",oldDirectory);
                if(Directory.Exists(fixture))Directory.Delete(fixture,true);
            }
        }

        private static Capture CaptureStable(FrameworkElement target,string name)
        {
            WaitSettled(); byte[] first;
            using(var image=NativeCapture(target))first=Pixels(image);
            WaitSettled();
            using(var image=NativeCapture(target))
            {
                byte[] second=Pixels(image); Check(Equal(first,second),name+": native image not stable across two presentation checkpoints");
                string png=Path.Combine(output,name+".png"); image.Save(png,System.Drawing.Imaging.ImageFormat.Png);
                var glyphs=new List<object>(); CollectGlyphs(target,glyphs);
                Check(glyphs.Count>0,name+": actual production glyph-run evidence is empty");
                Rect box=target.TransformToAncestor(window).TransformBounds(new Rect(target.RenderSize));
                Rect screen=new Rect(target.PointToScreen(new Point()),target.PointToScreen(new Point(target.ActualWidth,target.ActualHeight)));
                var capture=new Capture {PixelHash=HashBytes(second),Pixels=second,GlyphHash=HashText(Json.Serialize(glyphs)),Glyphs=glyphs,Box=box,Screen=screen,Width=image.Width,Height=image.Height};
                File.WriteAllText(Path.Combine(output,name+".json"),Json.Serialize(new {NativePixelSHA256=capture.PixelHash,PngSHA256=Hash(png),GlyphSHA256=capture.GlyphHash,
                    ActualBox=box.ToString(CultureInfo.InvariantCulture),PhysicalBox=screen.ToString(CultureInfo.InvariantCulture),Desired=target.DesiredSize.ToString(CultureInfo.InvariantCulture),Glyphs=glyphs}),new UTF8Encoding(false));
                return capture;
            }
        }
        private static object Record(Capture capture)
        {return new {PixelSHA256=capture.PixelHash,GlyphSHA256=capture.GlyphHash,Width=capture.Width,Height=capture.Height,ActualBox=capture.Box.ToString(CultureInfo.InvariantCulture),PhysicalBox=capture.Screen.ToString(CultureInfo.InvariantCulture),Glyphs=capture.Glyphs};}
        private static void Compare(Capture first,Capture actual,string name,List<string> differences)
        {
            if(first.Box!=actual.Box)differences.Add(name+" actual box changed "+first.Box+" -> "+actual.Box);
            if(first.Screen!=actual.Screen)differences.Add(name+" physical box changed "+first.Screen+" -> "+actual.Screen);
            if(first.GlyphHash!=actual.GlyphHash)differences.Add(name+" actual GlyphRun baseline/advances changed");
            if(first.PixelHash!=actual.PixelHash)
            {
                int changed=0,maximum=0;
                for(int index=0;index<Math.Min(first.Pixels.Length,actual.Pixels.Length);index++){int delta=Math.Abs(first.Pixels[index]-actual.Pixels[index]);if(delta!=0)changed++;maximum=Math.Max(maximum,delta);}
                differences.Add(name+" native pixels differ; changed-channel-bytes="+changed+" max-channel-delta="+maximum+" byte-count="+first.Pixels.Length+"/"+actual.Pixels.Length);
            }
        }
        private static Bitmap NativeCapture(FrameworkElement element)
        {
            Point start=element.PointToScreen(new Point()),end=element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight));
            int left=(int)Math.Floor(start.X),top=(int)Math.Floor(start.Y),width=(int)Math.Ceiling(end.X)-left,height=(int)Math.Ceiling(end.Y)-top;
            Check(width>0 && height>0,"empty production chrome capture rectangle"); IntPtr own=new WindowInteropHelper(window).Handle;
            foreach(double y in new[]{.05,.5,.95})foreach(double x in new[]{.05,.5,.95})
            {
                var hit=WindowFromPoint(new NativePoint {X=left+(int)(x*width),Y=top+(int)(y*height)});
                Check(GetAncestor(hit,2)==own,"production chrome is obscured; refuse capture outside own HWND");
            }
            var bitmap=new Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen(left,top,0,0,new System.Drawing.Size(width,height),System.Drawing.CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        private static byte[] Pixels(Bitmap image)
        {
            var data=new byte[image.Width*image.Height*3];int index=0;
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++){var pixel=image.GetPixel(x,y);data[index++]=pixel.R;data[index++]=pixel.G;data[index++]=pixel.B;}
            return data;
        }
        private static bool Equal(byte[] first,byte[] second){if(first.Length!=second.Length)return false;for(int index=0;index<first.Length;index++)if(first[index]!=second[index])return false;return true;}

        private static void CollectGlyphs(Visual owner,List<object> result)
        {
            Walk(VisualTreeHelper.GetDrawing(owner),Matrix.Identity,owner,result);
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(owner);index++)CollectGlyphs((Visual)VisualTreeHelper.GetChild(owner,index),result);
        }
        private static void Walk(Drawing drawing,Matrix parent,Visual owner,List<object> result)
        {
            var group=drawing as DrawingGroup;
            if(group!=null){Matrix next=group.Transform==null?Matrix.Identity:group.Transform.Value;next.Append(parent);foreach(var child in group.Children)Walk(child,next,owner,result);return;}
            var glyph=drawing as GlyphRunDrawing;if(glyph==null||glyph.GlyphRun==null)return;
            var run=glyph.GlyphRun;Point point=owner.TransformToAncestor(window).Transform(parent.Transform(run.BaselineOrigin));
            var advances=new List<double>();foreach(double value in run.AdvanceWidths)advances.Add(value);
            var indices=new List<int>();foreach(ushort value in run.GlyphIndices)indices.Add(value);
            var element=owner as FrameworkElement;var text=owner as TextBlock;
            result.Add(new {OwnerType=owner.GetType().Name,Name=element==null?"":element.Name,Text=text==null?"":text.Text,
                BaselineX=point.X,BaselineY=point.Y,EmSize=run.FontRenderingEmSize,Advances=advances,GlyphIndices=indices,Typeface=run.GlyphTypeface.FontUri.ToString()});
        }
        private static bool Settled(){return !Read<bool>(window,"visualTransitionActive")&&!Read<bool>(window,"pageTransitionActive")&&!Read<bool>(window,"toolTransitionActive")&&!Read<SmoothToolPicker>(window,"toolPicker").IsMotionActive;}
        private static void WaitSettled()
        {
            var frame=new DispatcherFrame();int stable=0;bool timedOut=false;Exception failure=null;
            var deadline=new DispatcherTimer(DispatcherPriority.Send){Interval=TimeSpan.FromMilliseconds(1800)};
            EventHandler rendering=delegate{try{StopTimers();stable=Settled()?stable+1:0;if(stable>=2)frame.Continue=false;}catch(Exception error){failure=error;frame.Continue=false;}};
            deadline.Tick+=delegate{timedOut=true;frame.Continue=false;};StopTimers();CompositionTarget.Rendering+=rendering;deadline.Start();
            try{Dispatcher.PushFrame(frame);}finally{deadline.Stop();CompositionTarget.Rendering-=rendering;StopTimers();}
            if(failure!=null)throw failure;Check(!timedOut&&stable>=2,"real production motions did not finish naturally within 1800ms");
            var idle=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(delegate{idle.Continue=false;}));Dispatcher.PushFrame(idle);
            Check(Settled(),"motion restarted after natural completion");Check(DwmFlush()>=0,"DWM synchronization failed");
        }
        private static Rect NativeBounds(){NativeRect bounds;Check(GetWindowRect(new WindowInteropHelper(window).Handle,out bounds),"own native bounds unavailable");return new Rect(bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);}
        private static void StopTimers(){foreach(var field in typeof(IslandWindow).GetFields(Private))if(field.FieldType==typeof(DispatcherTimer))((DispatcherTimer)field.GetValue(window)).Stop();}
        private static T Read<T>(object target,string name){return (T)target.GetType().GetField(name,Private).GetValue(target);}
        private static object Call(object target,string name,params object[] values){try{return target.GetType().GetMethod(name,Private).Invoke(target,values);}catch(TargetInvocationException error){throw error.InnerException??error;}}
        private static void Save(){File.WriteAllText(Path.Combine(output,"results.json"),Json.Serialize(new {Kind="actual-production-chrome-native-pixels",RTBUsed=false,GlobalInputUsed=false,Records=Records,Issues=Issues}),new UTF8Encoding(false));}
        private static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
        private static string HashBytes(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        private static string HashText(string value){return HashBytes(Encoding.UTF8.GetBytes(value));}
        private static void VerifyBuild()
        {
            string target=Assembly.GetExecutingAssembly().Location,manifestPath=Path.Combine(Path.GetDirectoryName(target),"production-chrome-pixel-build.json");
            var manifest=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(manifestPath));Check(Hash(target)==(string)manifest["TargetSHA256"],"EXE differs from compile manifest");
            foreach(var raw in (object[])manifest["Sources"]){var source=(Dictionary<string,object>)raw;Check(Hash((string)source["Path"])==(string)source["SHA256"],"source changed since compilation: "+source["Path"]);}
            Log("BUILD exe-sha256="+Hash(target)+" manifest-sha256="+Hash(manifestPath)+" source-hashes-match=true");
        }
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        private static void Log(string message){Console.WriteLine(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+" "+message);Console.Out.Flush();}
    }
}
