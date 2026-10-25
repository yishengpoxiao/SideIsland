using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bitmap = System.Drawing.Bitmap;

// Independent pixel/data probe. It does not reference or alter production code.
// Native images cover only this HWND's opaque white interior, after ownership
// and stable-presentation checks. No RenderTargetBitmap is used for these pixels:
// that software path cannot establish on-screen ClearType behavior.
internal static class GlyphOpacityProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private static readonly double[] Alphas = { .35, .7, .99, 1.0 };
    private static readonly string[] Families = { "Segoe UI", "Microsoft YaHei UI" };
    private static string output;
    private static Window window;
    private static StackPanel fade;
    private static TextBlock[] labels;
    private static TextBlock tab;
    private static System.Windows.Shapes.Path path;
    private sealed class Sample
    {
        internal string Name, Geometry;
        internal double Opacity;
        internal Bitmap Pixels;
        internal List<object> Runs;
        internal Dictionary<string, System.Drawing.Rectangle> Regions;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            output = System.IO.Path.Combine(project, "artifacts", "glyph-opacity-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--suite" && index + 1 < args.Length) { index++; continue; }
                if (args[index] == "--output" && index + 1 < args.Length) { output = Path.GetFullPath(args[++index]); continue; }
                throw new ArgumentException("Use --output project-directory and optional collector --suite GlyphOpacityProbe.");
            }
            Check(output.StartsWith(project, StringComparison.OrdinalIgnoreCase), "output must remain inside current project");
            Directory.CreateDirectory(output);
            Log("CONFIG exe-sha256=" + Hash(Assembly.GetExecutingAssembly().Location) + " pixel-source=native-screen-own-opaque-interior RTB=false");
            Run();
            Console.WriteLine("PROBE COMPLETE:GlyphOpacityProbe"); Console.Out.Flush();
            return 0;
        }
        catch (Exception error) { Log("FAIL " + error); return 1; }
        finally { if (window != null) window.Close(); }
    }

    private static void Run()
    {
        window = new Window { Width = 620, Height = 300, Left = 48, Top = 48, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false,
            Topmost = true, UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        var host = new StackPanel();
        window.Content = new Border { Background = Brushes.White, Padding = new Thickness(16), Child = host };
        fade = new StackPanel { Background = Brushes.Transparent };
        labels = new[] { MakeText("Network settings: waiting for task", 14), MakeText("消息 工具 状态 设置：保存原文", 14), MakeText("◇ ☼ ☾ → ✕  Settings", 16) };
        foreach (var label in labels) fade.Children.Add(label);
        path = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2,20 L 14,2 L 26,20 Z M 38,2 L 38,20 M 32,11 L 44,11"),
            Stroke = Brushes.Black, StrokeThickness = 1.5, Width = 80, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        fade.Children.Add(path); host.Children.Add(fade);
        tab = MakeText("状态 Settings", 14); tab.TextAlignment = TextAlignment.Center;
        host.Children.Add(new Border { Width = 180, Height = 38, Background = Brushes.White, Child = tab, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) });
        window.Show(); window.UpdateLayout(); Paint();
        var source = (HwndSource)PresentationSource.FromVisual(window);
        Log("WINDOW AllowsTransparency=true TextFormattingMode=Display TextRenderingMode=" + TextOptions.GetTextRenderingMode(window) +
            " ClearTypeHint=" + RenderOptions.GetClearTypeHint(window) + " dpi=" + source.CompositionTarget.TransformToDevice + " render-tier=" + (RenderCapability.Tier >> 16));
        var results = new List<object>();
        foreach (string family in Families)
        {
            foreach (var label in labels) label.FontFamily = new FontFamily(family);
            tab.FontFamily = new FontFamily(family); window.UpdateLayout();
            var samples = new List<Sample>();
            try
            {
                foreach (double alpha in Alphas)
                {
                    fade.Opacity = alpha; window.UpdateLayout();
                    var sample = Capture(family.Replace(" ", "-") + "-opacity-" + alpha.ToString("0.00", CultureInfo.InvariantCulture), alpha);
                    samples.Add(sample);
                    sample.Pixels.Save(Path.Combine(output, sample.Name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(Path.Combine(output, sample.Name + "-glyphs.json"), Json.Serialize(sample.Runs), new UTF8Encoding(false));
                }
                var full = samples[samples.Count - 1];
                foreach (var sample in samples)
                {
                    var regions = new List<object>();
                    foreach (var region in sample.Regions) regions.Add(new { Region = region.Key, Metrics = Compare(sample.Pixels, sample.Opacity, full.Pixels, region.Value) });
                    results.Add(new { Kind = "opacity", FontFamily = family, Opacity = sample.Opacity,
                        GlyphRunGeometryExactlyEqualToOpacityOne = sample.Geometry == full.Geometry, GeometrySHA256 = HashText(sample.Geometry), Regions = regions });
                    Log("OPACITY font=" + family + " alpha=" + sample.Opacity + " glyph-geometry-equal-to-one=" + (sample.Geometry == full.Geometry));
                }
            }
            finally { foreach (var sample in samples) sample.Pixels.Dispose(); }

            // Reproduce a fixed-width, centered tab whose selected state changes
            // weight. Its outer rectangle can stay fixed while glyph origins and
            // advance widths move. Record the real glyph baselines for both.
            fade.Opacity = 1;
            var weights = new List<object>();
            foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
            {
                tab.FontWeight = weight; window.UpdateLayout(); Paint();
                var runs = Runs(tab);
                using (var bitmap = NativeCapture(tab)) bitmap.Save(Path.Combine(output, family.Replace(" ", "-") + "-tab-" + weight + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                tab.Measure(new Size(Double.PositiveInfinity, 38));
                var intrinsic = tab.DesiredSize;
                window.UpdateLayout();
                weights.Add(new { Weight = weight.ToString(), IntrinsicDesiredWidth = intrinsic.Width, ActualWidth = tab.ActualWidth,
                    BaselinesAndAdvances = runs, BoundsInWindow = tab.TransformToAncestor(window).TransformBounds(new Rect(tab.RenderSize)).ToString(CultureInfo.InvariantCulture) });
            }
            results.Add(new { Kind = "fixed-centered-tab-weight-change", FontFamily = family, States = weights });
        }
        File.WriteAllText(Path.Combine(output, "results.json"), Json.Serialize(new { Diagnostic = "native-opacity-glyph-and-tab-weight-probe",
            Complete = true, ClaimsUnchangedPixels = false, ProductionSourceChanged = false, RenderingPolicyChanged = false, Results = results }), new UTF8Encoding(false));
        Log("COMPLETE results=" + Path.Combine(output, "results.json") + "; observed pixel differences are data, not an assumed pass/fail threshold");
        window.Close(); window = null;
    }

    private static TextBlock MakeText(string text, double size)
    { return new TextBlock { Text = text, Foreground = Brushes.Black, FontSize = size, FontWeight = FontWeights.Normal, Height = 30, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }; }

    private static Sample Capture(string name, double opacity)
    {
        Paint();
        Bitmap first = NativeCapture(fade); Paint(); Bitmap second = NativeCapture(fade);
        try { Check(SamePixels(first, second), name + ": native image not stable across two presentation checkpoints"); }
        finally { first.Dispose(); }
        var runs = new List<object>(); foreach (var label in labels) runs.AddRange(Runs(label));
        var regions = new Dictionary<string, System.Drawing.Rectangle>();
        var origin = fade.PointToScreen(new Point()); int left = (int)Math.Floor(origin.X), top = (int)Math.Floor(origin.Y);
        for (int index = 0; index < labels.Length; index++) regions.Add("text-row-" + index, Region(labels[index], left, top, second.Width, second.Height));
        regions.Add("vector-path-control", Region(path, left, top, second.Width, second.Height));
        return new Sample { Name = name, Opacity = opacity, Pixels = second, Runs = runs, Geometry = Json.Serialize(runs), Regions = regions };
    }

    private static List<object> Runs(Visual owner)
    {
        var result = new List<object>();
        CollectRuns(owner, result);
        Check(result.Count > 0, "no actual GlyphRunDrawing found for text visual");
        return result;
    }
    private static void CollectRuns(Visual owner, List<object> result)
    {
        Walk(VisualTreeHelper.GetDrawing(owner), Matrix.Identity, owner, result);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++) CollectRuns((Visual)VisualTreeHelper.GetChild(owner, index), result);
    }
    private static void Walk(Drawing drawing, Matrix parent, Visual owner, List<object> result)
    {
        var group = drawing as DrawingGroup;
        if (group != null)
        {
            Matrix transform = group.Transform == null ? Matrix.Identity : group.Transform.Value; transform.Append(parent);
            foreach (var child in group.Children) Walk(child, transform, owner, result);
            return;
        }
        var glyphDrawing = drawing as GlyphRunDrawing;
        if (glyphDrawing == null) return;
        GlyphRun run = glyphDrawing.GlyphRun;
        Point local = parent.Transform(run.BaselineOrigin), inWindow = owner.TransformToAncestor(window).Transform(local);
        var advances = new List<double>(); foreach (double value in run.AdvanceWidths) advances.Add(value);
        var indices = new List<int>(); foreach (ushort value in run.GlyphIndices) indices.Add(value);
        result.Add(new { BaselineLocalX = local.X, BaselineLocalY = local.Y, BaselineWindowX = inWindow.X, BaselineWindowY = inWindow.Y,
            EmSize = run.FontRenderingEmSize, Advances = advances, GlyphIndices = indices,
            Typeface = run.GlyphTypeface.FontUri.ToString(), InkBounds = run.ComputeInkBoundingBox().ToString(CultureInfo.InvariantCulture) });
    }

    private static Bitmap NativeCapture(FrameworkElement element)
    {
        Point p = element.PointToScreen(new Point()), end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        int left = (int)Math.Floor(p.X), top = (int)Math.Floor(p.Y), width = (int)Math.Ceiling(end.X) - left, height = (int)Math.Ceiling(end.Y) - top;
        Check(width > 0 && height > 0, "native capture rectangle is empty");
        IntPtr own = new WindowInteropHelper(window).Handle;
        foreach (double y in new[] { .05, .5, .95 }) foreach (double x in new[] { .05, .5, .95 })
        {
            IntPtr hit = WindowFromPoint(new NativePoint { X = left + (int)(x * width), Y = top + (int)(y * height) });
            Check(GetAncestor(hit, 2) == own, "own opaque interior is obscured; refuse to capture another window");
        }
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
        return bitmap;
    }
    private static System.Drawing.Rectangle Region(FrameworkElement element, int left, int top, int width, int height)
    {
        var point = element.PointToScreen(new Point()); var end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(0,0,width,height), new System.Drawing.Rectangle((int)Math.Floor(point.X)-left,(int)Math.Floor(point.Y)-top,(int)Math.Ceiling(end.X-point.X),(int)Math.Ceiling(end.Y-point.Y)));
    }
    private static object Compare(Bitmap sample, double alpha, Bitmap full, System.Drawing.Rectangle region)
    {
        Check(sample.Size == full.Size, "native pixel footprint changed across opacity levels");
        double error = 0, maximum = 0, aMass = 0, bMass = 0, ax = 0, ay = 0, bx = 0, by = 0, chroma = 0; int ink = 0;
        for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
        {
            var a = sample.GetPixel(x,y); var b = full.GetPixel(x,y);
            double ac = Math.Max(0,Math.Min(1,(765.0-a.R-a.G-a.B)/(765.0*alpha))), bc = (765.0-b.R-b.G-b.B)/765.0;
            if (ac > .02 || bc > .02) { double difference = Math.Abs(ac-bc); error += difference; maximum = Math.Max(maximum,difference); ink++; }
            aMass += ac; bMass += bc; ax += ac*x; ay += ac*y; bx += bc*x; by += bc*y;
            chroma += Math.Max(a.R,Math.Max(a.G,a.B))-Math.Min(a.R,Math.Min(a.G,a.B));
        }
        return new { InkPixels=ink, NormalizedMeanAbsoluteError=ink==0?0:error/ink, NormalizedMaximumError=maximum,
            CoverageMassRatio=bMass==0?0:aMass/bMass, CentroidDeltaX=aMass==0||bMass==0?0:ax/aMass-bx/bMass, CentroidDeltaY=aMass==0||bMass==0?0:ay/aMass-by/bMass,
            MeanRgbChroma=region.Width*region.Height==0?0:chroma/(region.Width*region.Height) };
    }
    private static bool SamePixels(Bitmap first, Bitmap second)
    { if(first.Size!=second.Size)return false;for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++)if(first.GetPixel(x,y)!=second.GetPixel(x,y))return false;return true; }
    private static void Paint()
    {
        var frame = new DispatcherFrame(); int count = 0; bool timeout = false;
        var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1500) };
        EventHandler rendering = delegate { if(++count>=2)frame.Continue=false; };
        deadline.Tick += delegate { timeout=true;frame.Continue=false; };
        CompositionTarget.Rendering += rendering; deadline.Start();
        try { Dispatcher.PushFrame(frame); } finally { deadline.Stop();CompositionTarget.Rendering-=rendering; }
        Check(!timeout,"no stable real Rendering checkpoint within 1500ms");
        // Rendering handlers precede the retained-scene commit. Drain to the
        // lower-priority boundary, then synchronize the desktop compositor.
        var drain = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{drain.Continue=false;})); Dispatcher.PushFrame(drain);
        Check(DwmFlush()>=0,"DWM presentation synchronization failed");
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
    private static string HashText(string text){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","");}
    private static void Log(string message){Console.WriteLine(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+" "+message);Console.Out.Flush();}
}
