using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexIsland
{
    // Independent diagnostic entry. No RTB, screenshots, global input, live
    // accounts or product/test edits. Geometry is sampled on actual Rendering
    // and its subsequently queued ContextIdle callback, without UpdateLayout.
    internal static class FunctionSwitchPoseProbe
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=64*1024*1024};
        private static readonly Dictionary<DependencyObject,int> Ids=new Dictionary<DependencyObject,int>();
        private static readonly List<object> Summaries=new List<object>();
        private static readonly Stopwatch Total=Stopwatch.StartNew();
        private static IslandWindow Window;
        private static StreamWriter Csv,Events;
        private static string Scenario,Matrix;
        private static int ScenarioIndex,Frame,ActionFrame;
        private static bool ActionStarted,PickerIntentional;
        private static StringBuilder Pending;
        private static Dictionary<string,Track> Tracks;
        private static Stopwatch ScenarioClock;
        private static int MaximumSeconds=60;
        private static string Output;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            try
            {
                if(args.Length==1 && args[0]=="--self-check")
                {
                    foreach(string field in new[]{"toolPanels","toolButtons","toolPicker","detailsScroll","expandedHeader","expandedTabs","settings"}) Check(typeof(IslandWindow).GetField(field,Private)!=null,"missing field "+field);
                    foreach(string method in new[]{"SelectTool","SelectToolsPage","SelectPage","ApplyAppearance"}) Check(typeof(IslandWindow).GetMethod(method,Private)!=null,"missing route "+method);
                    Console.WriteLine("SELF-CHECK PASS UI-created=false; Rendering/ContextIdle/glyph poses; matrix=zh,en x light,dark x 391,560; overlap scenario available.");return 0;
                }
                string group="all",matrix="full";Output=Path.Combine(Environment.CurrentDirectory,"artifacts","function-switch-pose-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                for(int index=0;index<args.Length;index+=2)
                {
                    if(index+1>=args.Length) throw new ArgumentException("Options need values.");
                    switch(args[index]) {case "--scenario":group=args[index+1];break;case "--matrix":matrix=args[index+1];break;case "--output":Output=args[index+1];break;case "--max-seconds":MaximumSeconds=Int32.Parse(args[index+1],CultureInfo.InvariantCulture);break;default:throw new ArgumentException("Unknown option "+args[index]);}
                }
                Check(new[]{"all","tools","picker","pages","overlap"}.Contains(group),"invalid scenario");Check(matrix=="full"||matrix=="small","invalid matrix");Check(MaximumSeconds>=5 && MaximumSeconds<=60,"deadline must be5..60 seconds");
                Output=Path.GetFullPath(Output);Check(Output.StartsWith(Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"output must be inside project");
                Directory.CreateDirectory(Output);VerifyBuild();Run(group,matrix);return 0;
            }
            catch(Exception error) {Console.Error.WriteLine(error);return 1;}
        }

        private static void Run(string group,string matrixMode)
        {
            string oldData=Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"),oldLanguage=UiLocalization.Language,oldTheme=UiTheme.Name;
            var oldContext=SynchronizationContext.Current;
            string fixture=Path.Combine(Path.GetTempPath(),"CodexIsland.FunctionSwitchPose."+Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",fixture);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                new SettingsStore().Save(new Settings {Language="zh",Theme="dark",AlwaysOnTop=false,AutoCollapse=false,HoverExpandEnabled=false,NotificationsEnabled=false,AutoMusic=false,OnlineLyricsEnabled=false,CustomExpandedSize=true,ExpandedWidth=391,ExpandedHeight=617,HiddenTools="",FavoriteTools=""});
                Window=new IslandWindow(true) {ShowActivated=false,IsHitTestVisible=false,Topmost=false};Window.Loaded+=delegate{StopTimers();};
                using(Csv=new StreamWriter(Path.Combine(Output,"poses.csv"),false,new UTF8Encoding(false),65536))
                using(Events=new StreamWriter(Path.Combine(Output,"events.jsonl"),false,new UTF8Encoding(false),65536))
                {
                    Csv.WriteLine("scenario,matrix,route,phase,render_frame,elapsed_ms,action_started,id,region,type,name,text,x,y,actual_w,actual_h,desired_w,desired_h,opacity,self_opacity,picker_h,scroll_y,page_fade,tool_fade");
                    Window.Show();Window.SetExpanded(true,false);Pump(240);StopTimers();
                    foreach(string language in new[]{"zh","en"}) foreach(string theme in new[]{"light","dark"}) foreach(int width in new[]{391,560})
                    {
                        if(matrixMode=="small" && !((language=="zh"&&theme=="dark"&&width==391)||(language=="en"&&theme=="light"&&width==560))) continue;
                        if(Expired()) break;
                        Matrix=language+"/"+theme+"/"+width;var settings=Read<Settings>(Window,"settings");settings.Language=language;settings.Theme=theme;settings.ExpandedWidth=width;settings.ExpandedHeight=617;settings.CustomExpandedSize=true;
                        Call(Window,"ApplyAppearance");Window.SetExpanded(true,false);Call(Window,"SelectToolsPage");Select("focus");Picker().IsExpanded=false;Pump(240);
                        if(group=="all"||group=="tools")
                        {
                            foreach(string key in new[]{"text","calculator","media","system"}) {string captured=key;Measure("tools-collapsed-to-"+key,false,delegate{Select(captured);},null);}
                            ScrollCase();
                        }
                        if(group=="all"||group=="picker")
                        {
                            SetupTools("focus",false);Measure("picker-open",true,delegate{Picker().IsExpanded=true;},null);
                            Measure("picker-select-text-auto-close",true,delegate{ClickTool("text");},null);
                        }
                        if(group=="all"||group=="pages")
                        {
                            Call(Window,"SelectPage",true);Pump(200);
                            Measure("pages-messages-to-tools",false,delegate{Call(Window,"SelectToolsPage");},null);
                            Measure("pages-tools-to-status",false,delegate{Call(Window,"SelectPage",false);},null);
                            Measure("pages-status-to-messages",false,delegate{Call(Window,"SelectPage",true);},null);
                        }
                        if(group=="all"||group=="overlap")
                        {
                            SetupTools("focus",true);Call(Window,"SelectPage",true);Pump(200);
                            Measure("overlap-open-picker-away-return-tool-at35ms",true,delegate{Call(Window,"SelectToolsPage");},delegate{ClickTool("text");});
                            SetupTools("focus",false);Call(Window,"SelectPage",true);Pump(200);
                            Measure("overlap-picker-open-during-page-fade-at35ms",true,delegate{Call(Window,"SelectToolsPage");},delegate{Picker().IsExpanded=true;});
                            ScrollCase();
                        }
                    }
                    JsonFile("summary.json",new Dictionary<string,object>{{"Kind","function-switch-real-rendered-geometry-probe"},{"UIExecuted",true},{"Demo",true},{"RTBUsed",false},{"GlobalInputUsed",false},{"ScenarioGroup",group},{"MatrixMode",matrixMode},{"DeadlineSeconds",MaximumSeconds},{"DeadlineReached",Expired()},{"ElapsedMilliseconds",Total.ElapsedMilliseconds},{"Scenarios",Summaries},{"Interpretation","Compare each element only while actually visible; baseline header glyphs remain eligible across a switch. Incoming hidden elements are not compared at stale prior positions. Picker/scroll displacement is marked intentional; no-picker chrome should remain stationary. Sampling cost is recorded; this is a pose diagnostic, not a frame-rate benchmark."}});
                    Console.WriteLine("POSE PROBE complete scenarios="+Summaries.Count+" deadlineReached="+Expired()+" output="+Output);
                }
            }
            finally
            {
                if(Window!=null) {StopTimers();Window.Close();Window=null;}
                AppAppearance.Configure(oldLanguage,oldTheme);SynchronizationContext.SetSynchronizationContext(oldContext);Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR",oldData);
                string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\";if(Directory.Exists(fixture)&&Path.GetFullPath(fixture).StartsWith(temp,StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture,true);
            }
        }

        private static void SetupTools(string key,bool picker) {Call(Window,"SelectToolsPage");Select(key);Picker().IsExpanded=picker;Pump(230);StopTimers();}
        private static void ScrollCase()
        {
            if(Expired()) return;var settings=Read<Settings>(Window,"settings");settings.ExpandedHeight=420;Window.SetExpanded(true,false);SetupTools("system",false);
            var scroll=Read<ScrollViewer>(Window,"detailsScroll");scroll.ScrollToVerticalOffset(scroll.ScrollableHeight/2);Pump(70);
            Event("scroll-case-setup",new Dictionary<string,object>{{"ScrollableHeight",scroll.ScrollableHeight},{"ActualOffset",scroll.VerticalOffset},{"RequestedHeight",420},{"MidScrollAchieved",scroll.VerticalOffset>.01}});
            Measure("scroll-middle-long-system-to-short-calculator",false,delegate{Select("calculator");},null);
            settings.ExpandedHeight=617;Window.SetExpanded(true,false);Pump(170);
        }

        private static void Measure(string name,bool pickerMotion,Action action,Action delayed)
        {
            if(Expired()) return;Scenario=(++ScenarioIndex).ToString("000")+"/"+name;PickerIntentional=pickerMotion;Frame=0;ActionFrame=-1;ActionStarted=false;Tracks=new Dictionary<string,Track>();Pending=new StringBuilder(128*1024);ScenarioClock=Stopwatch.StartNew();
            int renderEvents=0,idleEvents=0,layoutEvents=0;bool pendingIdle=false,done=false;double maxSample=0;
            var frame=new DispatcherFrame();var deadline=new DispatcherTimer(DispatcherPriority.Send){Interval=TimeSpan.FromMilliseconds(390)};
            var late=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(35)};
            EventHandler layout=delegate {layoutEvents++;Event("LayoutUpdated",null);};
            EventHandler rendering=delegate(object sender,EventArgs args)
            {
                if(done) return;Frame++;renderEvents++;var cost=Stopwatch.StartNew();Capture(ActionStarted?"Rendering":"BaselineRendering");maxSample=Math.Max(maxSample,cost.Elapsed.TotalMilliseconds);
                Event("Rendering",new Dictionary<string,object>{{"RenderingTime",((RenderingEventArgs)args).RenderingTime.TotalMilliseconds},{"SampleCostMs",cost.Elapsed.TotalMilliseconds}});
                if(!ActionStarted)
                {
                    Window.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {if(done)return;ActionStarted=true;ActionFrame=Frame;Event("ACTION",null);action();StopTimers();if(delayed!=null)late.Start();}));
                }
                if(!pendingIdle)
                {
                    pendingIdle=true;int scheduledFrame=Frame;
                    Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(delegate {pendingIdle=false;if(done)return;idleEvents++;var idleCost=Stopwatch.StartNew();Capture("ContextIdle");maxSample=Math.Max(maxSample,idleCost.Elapsed.TotalMilliseconds);Event("ContextIdle",new Dictionary<string,object>{{"ScheduledAfterRenderFrame",scheduledFrame},{"SampleCostMs",idleCost.Elapsed.TotalMilliseconds}});}));
                }
            };
            late.Tick+=delegate {late.Stop();Event("DELAYED_ACTION_35MS",null);delayed();StopTimers();};
            deadline.Tick+=delegate {done=true;frame.Continue=false;};
            Window.LayoutUpdated+=layout;CompositionTarget.Rendering+=rendering;deadline.Start();
            try {Dispatcher.PushFrame(frame);}
            finally {done=true;late.Stop();deadline.Stop();Window.LayoutUpdated-=layout;CompositionTarget.Rendering-=rendering;StopTimers();}
            Csv.Write(Pending.ToString());Csv.Flush();Events.Flush();
            var summary=new Dictionary<string,object>{{"Scenario",Scenario},{"Matrix",Matrix},{"IntentionalPickerMotion",pickerMotion},{"IntentionalScrollReset",name.StartsWith("scroll-")},{"ActionFrame",ActionFrame},{"RenderingEvents",renderEvents},{"ContextIdleEvents",idleEvents},{"LayoutUpdatedEvents",layoutEvents},{"MaxSampleCostMilliseconds",maxSample},{"Tracks",Tracks.Values.Select(item=>item.Record()).ToArray()}};
            Summaries.Add(summary);Console.WriteLine("SCENARIO "+Matrix+" "+Scenario+" frames="+renderEvents+" idle="+idleEvents+" targets="+Tracks.Count);
        }

        private static void Capture(string phase)
        {
            foreach(var visual in Descendants(Window))
            {
                var element=visual as FrameworkElement;if(element==null || !(element is TextBlock || element is AccessText || element is System.Windows.Shapes.Path || element is ContentPresenter || IsAnchor(element))) continue;
                if(!Visible(element)) continue;
                Point origin;try {origin=element.TransformToAncestor(Window).Transform(new Point());}catch(InvalidOperationException){continue;}
                string id=Identity(element),region=Region(element),text=TextOf(element);double opacity=OpacityOf(element);
                Pose(id,region,element.GetType().Name,element.Name,text,origin.X,origin.Y,element.ActualWidth,element.ActualHeight,element.DesiredSize.Width,element.DesiredSize.Height,opacity,element.Opacity,phase);
                DrawingGroup drawing=VisualTreeHelper.GetDrawing(element);if(drawing!=null) Glyphs(drawing,MatrixTransform.Identity.Value,element,id,region,text,opacity,phase,"0");
            }
        }
        private static void Glyphs(Drawing drawing,Matrix transform,FrameworkElement owner,string id,string region,string text,double opacity,string phase,string path)
        {
            var group=drawing as DrawingGroup;if(group!=null){Matrix next=group.Transform==null?transform:System.Windows.Media.Matrix.Multiply(group.Transform.Value,transform);for(int index=0;index<group.Children.Count;index++)Glyphs(group.Children[index],next,owner,id,region,text,opacity,phase,path+"."+index);return;}
            var glyph=drawing as GlyphRunDrawing;if(glyph==null||glyph.GlyphRun==null)return;
            Point baseline=transform.Transform(glyph.GlyphRun.BaselineOrigin);baseline=owner.TransformToAncestor(Window).Transform(baseline);Rect ink=glyph.GlyphRun.ComputeInkBoundingBox();
            Pose(id+"/glyph/"+path,region,"GlyphRun",owner.Name,text+" [em="+F(glyph.GlyphRun.FontRenderingEmSize)+" glyphs="+glyph.GlyphRun.GlyphIndices.Count+"]",baseline.X,baseline.Y,ink.Width,ink.Height,ink.X,ink.Y,opacity,owner.Opacity,phase);
        }
        private static void Pose(string id,string region,string type,string name,string text,double x,double y,double w,double h,double dw,double dh,double opacity,double self,string phase)
        {
            if(!Finite(x)||!Finite(y)||!Finite(w)||!Finite(h))return;
            Track track;if(!Tracks.TryGetValue(id,out track)){track=new Track{Id=id,Region=region,Type=type,Text=text};Tracks.Add(id,track);}track.Add(x,y,w,h,dw,dh,opacity,phase,Frame,ActionStarted);
            var picker=Picker();var scroll=Read<ScrollViewer>(Window,"detailsScroll");
            string[] row={Scenario,Matrix,Scenario.Substring(4),phase,Frame.ToString(),F(ScenarioClock.Elapsed.TotalMilliseconds),ActionStarted.ToString(),id,region,type,name,text,F(x),F(y),F(w),F(h),F(dw),F(dh),F(opacity),F(self),F(picker.ActualHeight),F(scroll.VerticalOffset),Read<bool>(Window,"pageTransitionActive").ToString(),Read<bool>(Window,"toolTransitionActive").ToString()};
            Pending.AppendLine(String.Join(",",row.Select(CsvValue)));
        }
        private static void Event(string kind,Dictionary<string,object> extra)
        {
            if(Events==null)return;var data=extra??new Dictionary<string,object>();data["Event"]=kind;data["Scenario"]=Scenario;data["Matrix"]=Matrix;data["ElapsedMs"]=ScenarioClock==null?0:ScenarioClock.Elapsed.TotalMilliseconds;data["Frame"]=Frame;
            data["PageFade"]=Read<bool>(Window,"pageTransitionActive");data["ToolFade"]=Read<bool>(Window,"toolTransitionActive");data["PickerExpanded"]=Picker().IsExpanded;data["PickerHeight"]=Picker().ActualHeight;data["ScrollY"]=Read<ScrollViewer>(Window,"detailsScroll").VerticalOffset;
            Events.WriteLine(Json.Serialize(data));
        }

        private sealed class Track
        {
            internal string Id,Region,Type,Text;internal double[] First,Last,Min,Max;internal int Samples,FirstFrame,LastFrame;internal bool SeenBefore,SeenAfter;internal double MaxStep;
            internal void Add(double x,double y,double w,double h,double dw,double dh,double opacity,string phase,int frame,bool after)
            {
                double[] values={x,y,w,h,dw,dh,opacity};if(First==null){First=(double[])values.Clone();Min=(double[])values.Clone();Max=(double[])values.Clone();FirstFrame=frame;}
                // Do not compare a reappearing target with a stale old pose.
                if(Last!=null && frame<=LastFrame+1) MaxStep=Math.Max(MaxStep,Math.Sqrt((x-Last[0])*(x-Last[0])+(y-Last[1])*(y-Last[1])));
                for(int index=0;index<values.Length;index++){if(Finite(values[index])){Min[index]=Math.Min(Min[index],values[index]);Max[index]=Math.Max(Max[index],values[index]);}}
                Last=values;LastFrame=frame;Samples++;SeenAfter|=after;SeenBefore|=!after;
            }
            internal object Record(){return new Dictionary<string,object>{{"Id",Id},{"Region",Region},{"Type",Type},{"Text",Text},{"Samples",Samples},{"FirstVisibleFrame",FirstFrame},{"LastVisibleFrame",LastFrame},{"SeenBeforeAction",SeenBefore},{"SeenAfterAction",SeenAfter},{"Columns",new[]{"X","Y","ActualWidth","ActualHeight","DesiredWidthOrInkX","DesiredHeightOrInkY","Opacity"}},{"First",First},{"Last",Last},{"Minimum",Min},{"Maximum",Max},{"MaxConsecutivePositionStep",MaxStep},{"IntentionalBodyMovement",PickerIntentional && Region=="body"}};}
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,index)))yield return child;}
        private static bool Visible(FrameworkElement element)
        {
            if(!element.IsVisible||element.ActualWidth<=0||element.ActualHeight<=0||OpacityOf(element)<=.0001)return false;
            Rect bounds;try{bounds=element.TransformToAncestor(Window).TransformBounds(new Rect(new Point(),element.RenderSize));}catch(InvalidOperationException){return false;}
            if(!bounds.IntersectsWith(new Rect(0,0,Window.ActualWidth,Window.ActualHeight)))return false;
            DependencyObject parent=VisualTreeHelper.GetParent(element);while(parent!=null&&parent!=Window){var clipping=parent as FrameworkElement;if(clipping!=null&&clipping.ClipToBounds){Rect clip=clipping.TransformToAncestor(Window).TransformBounds(new Rect(new Point(),clipping.RenderSize));if(!bounds.IntersectsWith(clip))return false;}parent=VisualTreeHelper.GetParent(parent);}return true;
        }
        private static bool IsAnchor(FrameworkElement element){return ReferenceEquals(element,Read<object>(Window,"expandedHeader"))||ReferenceEquals(element,Read<object>(Window,"expandedTabs"))||ReferenceEquals(element,Picker())||ReferenceEquals(element,Read<object>(Window,"detailsScroll"))||Panels().Values.Any(value=>ReferenceEquals(value,element));}
        private static string Region(DependencyObject element){for(var current=element;current!=null;current=VisualTreeHelper.GetParent(current)){if(ReferenceEquals(current,Read<object>(Window,"expandedHeader")))return "header";if(ReferenceEquals(current,Read<object>(Window,"expandedTabs")))return "tabs";if(ReferenceEquals(current,Picker()))return "picker";}return "body";}
        private static double OpacityOf(DependencyObject element){double value=1;for(var current=element;current!=null;current=VisualTreeHelper.GetParent(current)){var ui=current as UIElement;if(ui!=null)value*=ui.Opacity;}return value;}
        private static string TextOf(FrameworkElement element){var text=element as TextBlock;var access=element as AccessText;var presenter=element as ContentPresenter;string value=text!=null?text.Text:access!=null?access.Text:presenter!=null&&presenter.Content is string?(string)presenter.Content:"";return value.Length>100?value.Substring(0,100):value;}
        private static string Identity(DependencyObject value){int id;if(!Ids.TryGetValue(value,out id)){id=Ids.Count+1;Ids.Add(value,id);}return id.ToString(CultureInfo.InvariantCulture);}
        private static bool Expired(){return Total.Elapsed.TotalSeconds>=MaximumSeconds;}
        private static void Pump(int milliseconds){if(Expired())return;var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(Math.Max(1,Math.Min(milliseconds,(MaximumSeconds*1000)-Total.Elapsed.TotalMilliseconds)))};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();StopTimers();}
        private static Expander Picker(){return Read<Expander>(Window,"toolPicker");}
        private static Dictionary<string,UIElement> Panels(){return Read<Dictionary<string,UIElement>>(Window,"toolPanels");}
        private static void Select(string key){Call(Window,"SelectTool",key);}
        private static void ClickTool(string key){Read<Dictionary<string,Button>>(Window,"toolButtons")[key].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        private static T Read<T>(object target,string field){return (T)target.GetType().GetField(field,Private).GetValue(target);}
        private static object Call(object target,string method,params object[] args){try{return target.GetType().GetMethod(method,Private).Invoke(target,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
        private static void StopTimers(){if(Window==null)return;var objects=new List<object>{Window};objects.AddRange(Panels().Values.Cast<object>());foreach(var target in objects)foreach(var field in target.GetType().GetFields(Private))if(field.FieldType==typeof(DispatcherTimer)){var timer=field.GetValue(target) as DispatcherTimer;if(timer!=null)timer.Stop();}}
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        private static string F(double value){return value.ToString("0.######",CultureInfo.InvariantCulture);}
        private static string CsvValue(string text){return "\""+(text??"").Replace("\"","\"\"").Replace("\r"," ").Replace("\n"," ")+"\"";}
        private static void JsonFile(string file,object value){File.WriteAllText(Path.Combine(Output,file),Json.Serialize(value),new UTF8Encoding(false));}
        private static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
        private static void VerifyBuild(){string executable=Assembly.GetExecutingAssembly().Location;var manifest=(Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable),"function-switch-pose-build.json")));Check(Hash(executable)==(string)manifest["TargetSHA256"],"binary differs from manifest");foreach(var item in (object[])manifest["Sources"]){var source=(Dictionary<string,object>)item;Check(Hash((string)source["Path"])==(string)source["SHA256"],"source changed since compile: "+source["Path"]);}}
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
