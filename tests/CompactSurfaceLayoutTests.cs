using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexIsland
{
    // Owned demo HWNDs, generated pixels and local message fixtures only. No
    // player commands, notification reads, clipboard writes or global input.
    internal static class CompactSurfaceLayoutTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<string> Issues = new List<string>();
        private static int scenarios, captions, drawingDumps;

        internal static void Run(string directory = null)
        {
            directory = directory ?? Environment.GetEnvironmentVariable("CODEX_ISLAND_COMPACT_ARTIFACTS");
            string previous = Environment.GetEnvironmentVariable("CODEX_ISLAND_DATA_DIR"), language = UiLocalization.Language, theme = UiTheme.Name;
            string folder = Path.Combine(Path.GetTempPath(), "CodexIsland.CompactSurface." + Guid.NewGuid().ToString("N"));
            IslandWindow window = null;
            Issues.Clear(); scenarios = captions = drawingDumps = 0;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", folder);
                new SettingsStore().Save(new Settings { AlwaysOnTop = false, AutoCollapse = false, NotificationsEnabled = false, AutoMusic = false, OnlineLyricsEnabled = false, HoverExpandEnabled = false });
                window = new IslandWindow(true) { ShowActivated = false }; window.Loaded += (s, e) => Stop(window);
                window.Show(); Settle(window);
                if (directory != null) Directory.CreateDirectory(directory);
                var settings = Read<Settings>(window, "settings");
                foreach (string testLanguage in new[] { "zh", "en" })
                foreach (string testTheme in new[] { "light", "dark" })
                {
                    settings.Language = testLanguage; settings.Theme = testTheme; AppAppearance.Configure(testLanguage, testTheme);
                    string suffix = testLanguage + "-" + testTheme;
                    foreach (string edge in new[] { "left", "right", "top", "bottom" })
                    foreach (string direction in new[] { "auto", "rotate180", "mirror" })
                    {
                        settings.Side = edge; settings.CompactTextDirection = direction;
                        foreach (string mode in new[] { "quota", "widgets", "focus", "clock", "music", "music-unknown" })
                        {
                            Configure(window, mode); Settle(window);
                            string context = suffix + "/" + edge + "/" + direction + "/" + mode;
                            CheckCompact(window, context); scenarios++;
                            if (directory != null && direction == "auto" && (edge == "right" || edge == "top") && (mode == "quota" || mode == "focus" || mode == "music"))
                                Save(window, directory, mode + "-" + edge + "-" + suffix);
                        }
                        CheckWidgets(window, suffix + "/" + edge + "/" + direction);
                    }
                    settings.Side = "right"; settings.CompactTextDirection = "auto";
                    CheckPeek(window, suffix, directory);
                    CheckMenus(window, suffix, directory);
                }
                if (directory != null) File.WriteAllLines(Path.Combine(directory, "measurements.txt"), new[] {
                    "Scenarios=" + scenarios + "; captions=" + captions + "; issues=" + Issues.Count,
                    "zh/en x light/dark x 4 edges x 3 text directions; quota/widgets/focus/clock/music; long metadata; unknown timeline; preview privacy; native WPF menu, submenu, checkmarks, disabled items and shortcuts."
                }.Concat(Issues));
                if (Issues.Count > 0) throw new Exception("Compact surface layouts (" + Issues.Count + "): " + String.Join("\n", Issues.Take(40)));
                Console.WriteLine("PASS: " + scenarios + " compact/preview/menu states and " + captions + " captions across Chinese/English, light/dark, four edges and three text directions; compact bounds, all widgets, generated artwork, long original metadata, missing timeline, private preview, menu columns and nested popups.");
            }
            finally
            {
                if (window != null) { CloseMenus(window); Stop(window); window.Close(); }
                AppAppearance.Configure(language, theme); Environment.SetEnvironmentVariable("CODEX_ISLAND_DATA_DIR", previous);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        private static void Configure(IslandWindow window, string mode)
        {
            var settings = Read<Settings>(window, "settings"); settings.CompactMode = mode.StartsWith("music", StringComparison.Ordinal) ? "music" : mode == "widgets" ? "quota" : mode;
            settings.CompactSlot1 = mode == "widgets" ? "focus" : "codex-primary"; settings.CompactSlot2 = mode == "widgets" ? "notifications" : "codex-secondary";
            var monitor = Read<NowPlayingMonitor>(window, "nowPlaying");
            string title = "原文歌名 Long original track title with punctuation 😀 " + new string('T', 110);
            string artist = "原文歌手 Original artist / Collaborator " + new string('A', 80);
            string lyric = "原文歌词 Long synchronized lyric line / 中文与 English 😀 " + new string('L', 140);
            var media = new MediaSnapshot { Available = true, Playing = false, Title = title, Artist = artist, Source = "Fixture.Compact.Player", Duration = mode == "music-unknown" ? TimeSpan.Zero : TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(12), SampledAtUtc = DateTime.UtcNow,
                CanToggle = true, CanPrevious = true, CanNext = true, Artwork = Artwork() };
            typeof(NowPlayingMonitor).GetProperty("Snapshot", Private).SetValue(monitor, media, null);
            typeof(NowPlayingMonitor).GetProperty("Lyrics", Private).SetValue(monitor, LrcDocument.Parse("[00:01]" + lyric + "\n[02:00]Next line"), null);
            Call(window, "UpdateToolkitCompact"); window.SetExpanded(false, false); Stop(window); Call(window, "UpdateToolkitCompact");
            if (mode.StartsWith("music", StringComparison.Ordinal))
            {
                var visual = Read<CompactMusicVisual>(window, "compactMusic");
                string tooltip = Convert.ToString(Read<Grid>(window, "compactMusicTarget").ToolTip);
                Check(tooltip.Contains(title) && tooltip.Contains(artist), mode + ": original long media metadata missing from tooltip");
                Check(mode == "music-unknown" ? visual.Lyric.Length == 0 : visual.Lyric == lyric, mode + ": timeline policy changed synchronized lyric availability");
                foreach (Rect rect in new[] { visual.CoverBounds, visual.PreviousBounds, visual.NextBounds })
                    Check(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= visual.Width + .1 && rect.Bottom <= visual.Height + .1, mode + ": music action rectangle outside its visible card");
                Check(visual.ActionAt(Center(visual.CoverBounds)) == "music-toggle" && visual.ActionAt(Center(visual.PreviousBounds)) == "music-previous" && visual.ActionAt(Center(visual.NextBounds)) == "music-next", mode + ": compact transport targets lost their matching visible regions");
            }
        }

        private static void CheckCompact(IslandWindow window, string context)
        {
            Check(!Read<bool>(window, "expanded") && !Read<bool>(window, "peeking"), context + ": fixture unexpectedly expanded");
            var compact = Read<StackPanel>(window, "compact"); var shell = Read<Border>(window, "shell");
            Check(compact.IsVisible && compact.ActualWidth > 30 && compact.ActualHeight > 30, context + ": compact surface missing");
            foreach (FrameworkElement child in compact.Children.OfType<FrameworkElement>().Where(item => item.IsVisible))
            {
                Rect p = child.TransformToAncestor(shell).TransformBounds(new Rect(child.RenderSize));
                Check(p.Left >= -.8 && p.Top >= -.8 && p.Right <= shell.ActualWidth + .8 && p.Bottom <= shell.ActualHeight + .8, context + ": child outside shell " + child.GetType().Name + " " + p + "/" + child.RenderSize + "/" + shell.RenderSize);
            }
            foreach (TextBlock text in Descendants<TextBlock>(compact).Where(item => item.IsVisible && item.Text.Length > 0)) CheckText(text, context);
            foreach (FrameworkElement visual in new FrameworkElement[] { Read<CompactWidgetVisual>(window, "compactWidget1"), Read<CompactWidgetVisual>(window, "compactWidget2"), Read<CompactMusicVisual>(window, "compactMusic") }.Where(item => item.IsVisible))
            {
                var pixels=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);pixels.Render(visual);
                DrawingGroup drawing = VisualTreeHelper.GetDrawing(visual);
                if (drawing == null || drawing.Bounds.IsEmpty) {
                    var bytes=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(bytes,pixels.PixelWidth*4,0);int ink=Enumerable.Range(0,bytes.Length/4).Count(i=>bytes[i*4+3]>0);
                    if(drawingDumps++<2){Console.WriteLine("DRAWING_DIAGNOSTIC "+context+" opaquePixels="+ink);DumpDrawing(drawing,"");}
                    var widget=visual as CompactWidgetVisual; Check(false, context + ": compact drawing missing; null="+(drawing==null)+" pixels="+ink+" state="+(widget==null?"music":widget.State.Value+"/"+widget.State.Caption)); continue;
                }
                Rect bound = drawing.Bounds;
                Check(bound.Left >= -1 && bound.Top >= -1 && bound.Right <= visual.ActualWidth + 1 && bound.Bottom <= visual.ActualHeight + 1, context + ": compact drawing overflows visible footprint " + bound + "/" + visual.RenderSize);
                // Fade/translation layers can wrap the actual cover and text slots.
                // Inspect every innermost clipped slot, including empty ones:
                // filtering by glyph presence would hide a missing title/line.
                var groups=InnermostClippedGroups(drawing).ToArray();
                var widgetVisual=visual as CompactWidgetVisual;
                if(widgetVisual!=null)
                {
                    Check(groups.Length>=2,context+": value/caption drawing groups missing");
                    if(groups.Length>=2)
                    {
                        if(widgetVisual.State.Key!="media") Check(Glyphs(groups[0])>0,context+": widget value has no rendered glyphs: "+widgetVisual.State.Value);
                        Check(Glyphs(groups[groups.Length-1])>0,context+": widget caption has no rendered glyphs: "+widgetVisual.State.Caption);
                    }
                }
                else Check(groups.Length>=4 && groups.Skip(1).All(item=>Glyphs(item)>0),context+": music title/artist/lyric status has an empty text drawing");
            }
        }

        private static IEnumerable<DrawingGroup> InnermostClippedGroups(Drawing drawing)
        {
            var group = drawing as DrawingGroup;
            if (group == null) yield break;
            var nested = group.Children.SelectMany(InnermostClippedGroups).ToArray();
            if (nested.Length > 0)
            {
                foreach (var slot in nested) yield return slot;
            }
            else if (group.ClipGeometry != null) yield return group;
        }
        private static void CheckWidgets(IslandWindow window, string context)
        {
            var settings = Read<Settings>(window, "settings"); settings.CompactMode = "quota";
            var data = new CompactWidgetData { Language = settings.Language, LocalTime = new DateTime(2026,10,24,23,59,59), PrimaryRemaining = 100, SecondaryRemaining = 0, PrimaryMinutes = 300, SecondaryMinutes = 10080,
                CpuPercent = 100, MemoryPercent = 99, BatteryPercent = 1, OnAc = false, FocusStatus = "专注 199:59", FocusLabel = "Long original custom focus label / 专注", AwakeStatus = "唤醒 1:59:59", AwakeRunning = true, UnreadCount = 12345,
                PortsStatus = "3000: occupied / 8080: free", Media = new MediaSnapshot { Available = true, Title = "Long original title " + new string('W',300), Artist = "中文歌手", Artwork = Artwork() } };
            foreach (string key in CompactWidgetCatalog.Keys)
            {
                settings.CompactSlot1 = key; settings.CompactSlot2 = key; Call(window,"UpdateToolkitCompact");
                var state = CompactWidgetState.Create(key, data); var first = Read<CompactWidgetVisual>(window,"compactWidget1"); var second = Read<CompactWidgetVisual>(window,"compactWidget2");
                first.Update(state, settings.CompactTextDirection, settings.Theme == "light"); second.Update(state, settings.CompactTextDirection, settings.Theme == "light");
                Settle(window); CheckCompact(window, context + "/widget=" + key); scenarios++;
                Check(first.State.Value == state.Value && first.State.Caption == state.Caption && first.State.Tooltip == state.Tooltip, context + ": compact appearance changed original widget state");
            }
            data.PrimaryRemaining = null; data.SecondaryRemaining = Double.NaN; data.QuotaStale = true;
            foreach (string key in new[] { "codex-primary", "codex-secondary" })
            {
                var state = CompactWidgetState.Create(key, data); Read<CompactWidgetVisual>(window,"compactWidget1").Update(state, settings.CompactTextDirection, settings.Theme == "light"); Settle(window);
                Check(state.Value == "—" && !state.Fraction.HasValue && state.Stale, context + ": unknown quota invents availability"); CheckCompact(window, context + "/unknown=" + key); scenarios++;
            }
        }

        private static void CheckPeek(IslandWindow window, string context, string directory)
        {
            var settings = Read<Settings>(window,"settings"); settings.CompactMode = "quota"; window.SetExpanded(false,false); Settle(window);
            string title = "PRIVATE_TITLE_原文 " + String.Join(" ",Enumerable.Repeat("Long configuration error message 😀",8));
            string body = "PRIVATE_BODY_原文\n" + String.Join(" ",Enumerable.Repeat("Long error details / 参数配置，保持原文。",20));
            var message = new IslandNotification { Id="compact-peek-fixture",AppId="fixture.compact.preview",AppName="Long original application / 中文应用 " + new string('A',110),Title=title,Body=body,CreatedAt=DateTime.UtcNow,IsDemo=true };
            foreach (string state in new[] { "long", "private", "empty-title", "empty-body" })
            {
                bool privacy=state=="private";
                message.Title=state=="empty-title"?"":title; message.Body=state=="empty-body"?"":body; message.IsDemo=state=="long";
                settings.HideNotificationBody=privacy; Call(window,"ShowPeek",message); FinishTransition(window); Settle(window); scenarios++;
                var panel=Read<Border>(window,"peekPanel"); var source=Read<TextBlock>(window,"peekSource"); var heading=Read<TextBlock>(window,"peekTitle"); var detail=Read<TextBlock>(window,"peekBody");
                Check(Read<bool>(window,"peeking") && panel.IsVisible,context+": preview failed to open");
                foreach(var text in Descendants<TextBlock>(panel).Where(item=>item.IsVisible && item.Text.Length>0)) CheckText(text,context+"/peek/private="+privacy);
                foreach(var text in new[]{source,heading,detail})
                {
                    Point p=text.TranslatePoint(new Point(),panel);
                    Check(p.X>=-1 && p.Y>=-1 && p.X+text.ActualWidth<=panel.ActualWidth+1 && p.Y+text.ActualHeight<=panel.ActualHeight+1,context+": preview text outside panel");
                }
                if(privacy) Check(!heading.Text.Contains("PRIVATE_") && !detail.Text.Contains("PRIVATE_") && !Convert.ToString(heading.ToolTip).Contains("PRIVATE_") && !Convert.ToString(detail.ToolTip).Contains("PRIVATE_"),context+": privacy leaked original title or body in visible text/tooltip");
                else Check(heading.Text==message.Title && detail.Text==message.Body && Convert.ToString(heading.ToolTip)==message.Title && Convert.ToString(detail.ToolTip)==message.Body && Convert.ToString(source.ToolTip).Contains(message.AppName),context+": preview lost full original content or tooltip");
                Check(heading.Visibility==(message.Title.Length==0&&!privacy?Visibility.Collapsed:Visibility.Visible) && detail.Visibility==(message.Body.Length==0&&!privacy?Visibility.Collapsed:Visibility.Visible),context+": empty preview title/body retained a blank row");
                if(directory!=null && (state=="long"||privacy)) Save(window,directory,"peek"+(privacy?"-private":"")+"-"+context);
                window.SetExpanded(false,false); Settle(window);
            }
            settings.HideNotificationBody=false;
        }

        private static void CheckMenus(IslandWindow window,string context,string directory)
        {
            var shell=Read<Border>(window,"shell"); var music=Read<Grid>(window,"compactMusicTarget");
            foreach(var entry in new[]{new KeyValuePair<string,ContextMenu>("shell",shell.ContextMenu),new KeyValuePair<string,ContextMenu>("music",music.ContextMenu)})
            {
                var menu=entry.Value; menu.PlacementTarget=shell; menu.Placement=PlacementMode.Center; menu.IsOpen=true; Settle(window);
                try { CheckMenu(menu,context+"/"+entry.Key); scenarios++; if(directory!=null) SaveElement(menu,directory,"menu-"+entry.Key+"-"+context); }
                finally { menu.IsOpen=false; Settle(window); }
            }
            var fixture=new ContextMenu(); UiTheme.Install(fixture.Resources); fixture.PlacementTarget=shell; fixture.Placement=PlacementMode.Center;
            var shortItem=new MenuItem{Header="Short action",InputGestureText="Ctrl+F"};
            var checkedItem=new MenuItem{Header="Checked setting",IsCheckable=true,IsChecked=true,InputGestureText="Ctrl+Alt+P"};
            var disabledItem=new MenuItem{Header="Unavailable action",IsEnabled=false,InputGestureText="Ctrl+Shift+P"};
            var longItem=new MenuItem{Header="A very long English action with several words / 中文菜单项 " + new string('W',85),InputGestureText="Ctrl+Alt+Shift+F12"};
            var nested=new MenuItem{Header="Submenu with long original title / 子菜单"};
            nested.Items.Add(new MenuItem{Header="Nested checked option / 已选择",IsCheckable=true,IsChecked=true,InputGestureText="Alt+1"});
            nested.Items.Add(new MenuItem{Header="Long nested action / 原文 " + new string('M',110)});
            fixture.Items.Add(shortItem); fixture.Items.Add(checkedItem); fixture.Items.Add(disabledItem); fixture.Items.Add(longItem); fixture.Items.Add(new Separator()); fixture.Items.Add(nested);
            fixture.IsOpen=true; Settle(window);
            try
            {
                CheckMenu(fixture,context+"/fixture"); Check(checkedItem.IsChecked && !disabledItem.IsEnabled,context+": menu changed checked/disabled model state");
                var tick=checkedItem.Template.FindName("Tick",checkedItem) as FrameworkElement; var icon=checkedItem.Template.FindName("Icon",checkedItem) as FrameworkElement;
                Check(tick!=null && tick.IsVisible && icon!=null && icon.Visibility==Visibility.Collapsed,context+": checked menu lost its visible checkmark");
                Check(disabledItem.Opacity<.8,context+": disabled menu lacks visible differentiation");
                if(directory!=null) SaveElement(fixture,directory,"menu-fixture-"+context); scenarios++;
                nested.IsSubmenuOpen=true; Settle(window);
                var popup=(Popup)nested.Template.FindName("PART_Popup",nested);
                Check(popup!=null && popup.IsOpen && popup.Child!=null,context+": nested menu failed to open");
                CheckMenuItems(nested.Items.OfType<MenuItem>().ToArray(),popup.Child as FrameworkElement,context+"/nested");
                if(directory!=null) SaveElement((FrameworkElement)popup.Child,directory,"menu-nested-"+context); scenarios++;
            }
            finally { nested.IsSubmenuOpen=false; fixture.IsOpen=false; Settle(window); }
            Check(!shortItem.IsSubmenuOpen && !nested.IsSubmenuOpen,context+": menu popup remained open after close");
        }

        private static void CheckMenu(ContextMenu menu,string context)
        {
            Check(menu.IsOpen && menu.ActualWidth>80 && menu.ActualHeight>20,context+": menu layout missing");
            CheckMenuItems(menu.Items.OfType<MenuItem>().ToArray(),menu,context);
        }
        private static void CheckMenuItems(MenuItem[] items,FrameworkElement host,string context)
        {
            double? headerX=null,gestureRight=null;
            foreach(var item in items.Where(value=>value.IsVisible))
            {
                item.ApplyTemplate(); Point p=item.TranslatePoint(new Point(),host);
                Check(p.X>=-1 && p.X+item.ActualWidth<=host.ActualWidth+1,context+": menu item outside popup");
                foreach(var text in Descendants<TextBlock>(item).Where(value=>value.IsVisible && value.Text.Length>0)) CheckText(text,context+"/"+Convert.ToString(item.Header));
                foreach(var text in Descendants<AccessText>(item).Where(value=>value.IsVisible && value.Text.Length>0))
                {
                    captions++;
                    Check(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1 && text.DesiredSize.Height-text.Margin.Top-text.Margin.Bottom<=text.ActualHeight+1,
                        context+": access-key label cropped " + text.Text.Substring(0,Math.Min(text.Text.Length,70)) + " desired="+text.DesiredSize+" actual="+text.RenderSize);
                }
                var header=item.Template.FindName("Header",item) as FrameworkElement;
                if(header!=null) { double x=header.TranslatePoint(new Point(),host).X; if(headerX.HasValue) Check(Math.Abs(x-headerX.Value)<1,context+": menu label columns do not align"); else headerX=x; }
                var gesture=item.Template.FindName("Gesture",item) as FrameworkElement;
                if(gesture!=null && !String.IsNullOrEmpty(item.InputGestureText))
                {
                    Point g=gesture.TranslatePoint(new Point(),host); double right=g.X+gesture.ActualWidth;
                    if(gestureRight.HasValue) Check(Math.Abs(right-gestureRight.Value)<1,context+": menu shortcut right edges differ"); else gestureRight=right;
                    if(header!=null){ Point h=header.TranslatePoint(new Point(),host); Check(h.X+header.ActualWidth<=g.X+.8,context+": label overlaps shortcut"); }
                }
            }
        }
        private static void CheckText(TextBlock text,string context)
        {
            captions++; double desiredWidth=text.DesiredSize.Width-text.Margin.Left-text.Margin.Right, desiredHeight=text.DesiredSize.Height-text.Margin.Top-text.Margin.Bottom;
            bool fits=desiredWidth<=text.ActualWidth+1 && desiredHeight<=text.ActualHeight+1;
            bool intentionalEllipsis=text.TextTrimming!=TextTrimming.None && !String.IsNullOrEmpty(Convert.ToString(text.ToolTip));
            Check(text.ActualHeight>=text.FontSize && (fits || intentionalEllipsis),context+": caption cropped without accessible original " + text.Text.Substring(0,Math.Min(text.Text.Length,70))+" desired="+text.DesiredSize+" actual="+text.RenderSize);
        }
        private static BitmapSource Artwork(){ var bytes=Enumerable.Range(0,64).SelectMany(index=>new byte[]{191,125,50,255}).ToArray(); var bitmap=BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,bytes,32);bitmap.Freeze();return bitmap; }
        private static Point Center(Rect rect){return new Point(rect.X+rect.Width/2,rect.Y+rect.Height/2);}
        private static int Glyphs(Drawing drawing){var glyph=drawing as GlyphRunDrawing;if(glyph!=null)return glyph.GlyphRun==null?0:glyph.GlyphRun.GlyphIndices.Count;var group=drawing as DrawingGroup;return group==null?0:group.Children.Sum(child=>Glyphs(child));}
        private static void DumpDrawing(Drawing drawing,string indent){if(drawing==null)return;Console.WriteLine(indent+drawing.GetType().Name+" "+drawing.Bounds);var group=drawing as DrawingGroup;if(group!=null){Console.WriteLine(indent+"clip="+(group.ClipGeometry==null?"null":group.ClipGeometry.Bounds.ToString())+" transform="+group.Transform);foreach(var child in group.Children)DumpDrawing(child,indent+"  ");}}
        private static void FinishTransition(IslandWindow window){Call(window,"StopVisualTransition",true);}
        private static void CloseMenus(IslandWindow window){var shell=Read<Border>(window,"shell");if(shell.ContextMenu!=null)shell.ContextMenu.IsOpen=false;var music=Read<Grid>(window,"compactMusicTarget");if(music.ContextMenu!=null)music.ContextMenu.IsOpen=false;}
        private static void Stop(IslandWindow window){foreach(var field in typeof(IslandWindow).GetFields(Private)) if(field.FieldType==typeof(DispatcherTimer)){var timer=field.GetValue(window) as DispatcherTimer;if(timer!=null)timer.Stop();} var monitor=Read<NowPlayingMonitor>(window,"nowPlaying");if(monitor!=null)monitor.Stop();}
        private static void Settle(IslandWindow window){Stop(window);window.UpdateLayout();Pump();window.UpdateLayout();Pump();window.UpdateLayout();Stop(window);}
        private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
        private static T Read<T>(object value,string name){return (T)value.GetType().GetField(name,Private).GetValue(value);}
        private static object Call(object value,string name,params object[] args){try{return value.GetType().GetMethod(name,Private).Invoke(value,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);var value=child as T;if(value!=null)yield return value;foreach(var descendant in Descendants<T>(child))yield return descendant;}}
        private static void Check(bool value,string message){if(!value)Issues.Add(message);}
        private static void Save(Window window,string directory,string name){SaveElement(window,directory,name);}
        private static void SaveElement(FrameworkElement element,string directory,string name)
        {
            element.UpdateLayout(); element.InvalidateVisual(); var frame=new DispatcherFrame();EventHandler rendered=null;
            var deadline=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(700)};deadline.Tick+=(s,e)=>frame.Continue=false;
            rendered=(s,e)=>{CompositionTarget.Rendering-=rendered;element.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));};
            CompositionTarget.Rendering+=rendered;deadline.Start();try{Dispatcher.PushFrame(frame);}finally{deadline.Stop();CompositionTarget.Rendering-=rendered;}
            var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(element.ActualWidth)),Math.Max(1,(int)Math.Ceiling(element.ActualHeight)),96,96,PixelFormats.Pbgra32);bitmap.Render(element);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(directory,name+".png")))encoder.Save(stream);
        }
    }
}
