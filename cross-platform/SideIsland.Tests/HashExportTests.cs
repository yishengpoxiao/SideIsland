using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodexIsland;
using SideIsland.Desktop;
using SideIsland.Desktop.Platforms;

namespace SideIsland.Tests;

internal static class HashExportTests
{
    public static void Run(string? artifactDirectory = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "side-island-checksum-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "下载 example.txt"); byte[] original = Encoding.UTF8.GetBytes("checksum export fixture\n中文"); File.WriteAllBytes(source, original);
        string sha256 = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), sha512 = Convert.ToHexString(SHA512.HashData(original)).ToLowerInvariant();
        var copied = new List<string>(); var suggestions = new List<string>(); var pickers = new List<TaskCompletionSource<string?>>(); TaskCompletionSource? copyGate = null;
        var integrations = new IntegrationSources(_ => Task.FromResult(new MediaSnapshot(false,"","","",null,null,null,false,null)), (_,_) => Task.FromResult<string?>(null), (_,_) => throw new InvalidOperationException("unused quota"))
        {
            WriteChecksumText = text => { copied.Add(text); return copyGate?.Task ?? Task.CompletedTask; },
            ChooseChecksumDestination = name => { suggestions.Add(name); var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously); pickers.Add(picker); return picker.Task; }
        };
        var window = new IslandWindow(true, Path.Combine(directory,"prefs"), integrations);
        try
        {
            window.Show(); window.Expand(); AppearanceTestActions.Set(window,"en",window.Settings.LightTheme); window.SelectToolForTest("hash"); Flush(window);
            Check(ExportButtons(window).All(button => !button.IsEnabled), "exports unavailable before a successful calculation");
            Calculate(window,source,0); Check(ExportButtons(window).All(button=>button.IsEnabled), "successful checksum enables all export actions");
            Click(Find<Button>(window,"hash-copy")); Await(window,window.LatestChecksumExport);
            Click(Find<Button>(window,"hash-copy-manifest")); Await(window,window.LatestChecksumExport);
            Check(copied.Count==2 && copied[0]==sha256 && copied[1]==sha256+" *下载 example.txt\n", "copy digest/list uses captured hash and basename only through injected writer");
            Check(!copied[1].Contains(directory,StringComparison.Ordinal), "export does not disclose source directories");

            string destination=Path.Combine(directory,"published.sha256"); Task saving=StartSave(window);
            Check(suggestions[^1]=="下载 example.txt.sha256" && ExportButtons(window).All(button=>!button.IsEnabled), "pending save disables all exports and suggests algorithm extension");
            int count=pickers.Count; Click(Find<Button>(window,"hash-save-manifest")); Check(pickers.Count==count,"repeat click cannot open another picker");
            pickers[^1].SetResult(destination); Await(window,saving);
            Check(File.ReadAllBytes(destination).SequenceEqual(Encoding.UTF8.GetBytes(copied[1])) && File.ReadAllBytes(source).SequenceEqual(original), "actual new file contains complete BOM-free UTF8 and source stays unchanged");
            saving=StartSave(window); pickers[^1].SetResult(destination); Await(window,saving);
            Check(File.ReadAllText(destination)==copied[1] && Find<TextBlock>(window,"hash-status").Text!.StartsWith("Not saved",StringComparison.Ordinal),"existing export is refused without replacement");
            saving=StartSave(window); pickers[^1].SetResult(source); Await(window,saving);
            Check(File.ReadAllBytes(source).SequenceEqual(original),"original file cannot be an export destination");
            File.Delete(source); saving=StartSave(window); pickers[^1].SetResult(source); Await(window,saving);
            Check(!File.Exists(source),"captured original path stays protected even if source disappeared after hashing"); File.WriteAllBytes(source,original);
            saving=StartSave(window); pickers[^1].SetResult(null); Await(window,saving); Check(Find<Button>(window,"hash-save-manifest").IsEnabled,"picker cancellation restores export controls");

            foreach(string change in new[]{"path","algorithm","expected","page","language","theme"})
            {
                Calculate(window,source,0); saving=StartSave(window); string stale=Path.Combine(directory,"stale-"+change+".sha256");
                switch(change)
                {
                    case "path": Find<TextBox>(window,"hash-path").Text=source+".other"; break;
                    case "algorithm": Find<ComboBox>(window,"hash-algorithm").SelectedIndex=1; break;
                    case "expected": Find<TextBox>(window,"hash-expected").Text=new string('0',64); break;
                    case "page": window.SelectToolForTest("notes"); break;
                    case "language": Click(Header(window,window.Settings.Language=="en"?"中":"EN")); break;
                    case "theme": Click(Header(window,window.Settings.LightTheme?"☾":"☼")); break;
                }
                pickers[^1].SetResult(stale); Await(window,saving); Check(!File.Exists(stale),"late picker cannot export after "+change+" change");
                AppearanceTestActions.Set(window,"en",window.Settings.LightTheme); window.SelectToolForTest("hash"); Flush(window);
            }
            Calculate(window,source,1); Click(Find<Button>(window,"hash-copy-manifest")); Await(window,window.LatestChecksumExport);
            Check(copied[^1]==sha512+" *下载 example.txt\n","SHA512 export contains the actual SHA512 result");
            copyGate=new(TaskCreationOptions.RunContinuationsAsynchronously); Click(Find<Button>(window,"hash-copy-manifest")); Task copying=window.LatestChecksumExport;
            Find<TextBox>(window,"hash-path").Text=source+".changed"; copyGate.SetResult(); Await(window,copying); copyGate=null;
            Check(Find<TextBlock>(window,"hash-status").Text=="Inputs changed. Calculate again." && ExportButtons(window).All(button=>!button.IsEnabled),"late clipboard completion cannot restore old status or exports");
            if(OperatingSystem.IsWindows())
            {
                string unusual=Path.Combine(directory,"invalid-\uD800.bin"); File.WriteAllBytes(unusual,original);
                Check(FileHashTools.ComputeFile(unusual,"SHA256",default)==sha256,"actual UTF16 filename remains readable and hashable on this filesystem");
                window.SelectToolForTest("hash"); Flush(window); Find<TextBox>(window,"hash-path").Text=unusual; Find<TextBox>(window,"hash-expected").Text=""; Find<ComboBox>(window,"hash-algorithm").SelectedIndex=0;
                var compute=Find<Button>(window,"hash-compute"); Click(compute); Wait(window,()=>compute.IsEnabled);
                Check(Find<TextBox>(window,"hash-result").Text==sha256 && Find<Button>(window,"hash-copy").IsEnabled,"UTF8 manifest formatting failure must preserve the successfully computed checksum: "+Find<TextBlock>(window,"hash-status").Text);
                Check(!Find<Button>(window,"hash-copy-manifest").IsEnabled && !Find<Button>(window,"hash-save-manifest").IsEnabled,"only unsupported manifest actions are disabled");
                Click(Find<Button>(window,"hash-copy")); Await(window,window.LatestChecksumExport); Check(copied[^1]==sha256,"digest can still be copied without re-encoding the source filename");
            }
            FilledLayouts(window,source,artifactDirectory);
            Calculate(window,source,0); saving=StartSave(window); string closed=Path.Combine(directory,"closed.sha256"); window.Close(); pickers[^1].SetResult(closed); Await(window,saving);
            Check(!File.Exists(closed),"closing window suppresses late picker save");
            Check(Directory.GetFiles(directory,".side-island-publish-*.tmp").Length==0,"export attempts leave no owned temporary files");
            SymlinkDestinations(directory,copied[1]);
            Console.WriteLine("PASS: actual checksum export controls, SHA256/SHA512 snapshot copies, new-file-only UTF8 saves, no source overwrite, delayed picker/clipboard edit/navigation/theme/language/close rejection and 96 populated export layouts; native pickers/clipboard are not exercised.");
        }
        finally { window.Close(); foreach(var picker in pickers) picker.TrySetResult(null); copyGate?.TrySetResult(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory,true); }
    }
    private static void SymlinkDestinations(string directory,string manifest)
    {
        string target=Path.Combine(directory,"link-target.txt"); File.WriteAllText(target,"owned target");
        foreach(bool dangling in new[]{false,true})
        {
            string link=Path.Combine(directory,dangling?"dangling.sha256":"existing-link.sha256"), linkTarget=dangling?Path.Combine(directory,"missing-target.txt"):target;
            try { File.CreateSymbolicLink(link,linkTarget); }
            catch(Exception error) when(error is UnauthorizedAccessException or PlatformNotSupportedException || error is IOException && OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 1314 or 50)
            { Console.WriteLine("SKIP: symlink export fixture unavailable on this host: "+error.GetType().Name); continue; }
            bool refused=false; try{FileHashTools.SaveManifestNew(link,manifest,(path,bytes)=>SideIsland.Core.PortableFilePublication.WriteNew(path,bytes));}catch(IOException){refused=true;}
            Check(refused && new FileInfo(link).LinkTarget==linkTarget && File.ReadAllText(target)=="owned target" && (!dangling||!File.Exists(linkTarget)),"existing and dangling symlinks stay intact"); File.Delete(link);
        }
    }
    private static void FilledLayouts(IslandWindow window,string source,string? artifacts)
    {
        int count=0;
        foreach(string language in new[]{"en","zh"}) foreach(bool light in new[]{false,true}) foreach(int width in new[]{320,480,760}) foreach(int height in new[]{360,720}) foreach(double scale in new[]{1d,1.25,1.5,2})
        {
            AppearanceTestActions.Set(window,language,light);
            window.SetRenderScaling(scale); window.Width=width; window.Height=height; Calculate(window,source,1); Flush(window);
            foreach(var button in ExportButtons(window))
            {
                Check(button.IsEnabled,"populated export remains available across layout settings");
                foreach(var caption in button.GetVisualDescendants().OfType<TextBlock>()) Check(caption.Bounds.Width+1>=caption.DesiredSize.Width && caption.Bounds.Height+1>=caption.DesiredSize.Height,"export action caption fits its compact bounds");
                var point=button.TranslatePoint(default,window); Check(point is Point position && position.X>=-1 && position.X+button.Bounds.Width<=width+1,"export action stays within narrow window");
            }
            var scroller=window.GetVisualDescendants().OfType<ScrollViewer>().First(view=>view.GetVisualDescendants().Any(control=>control.Name=="hash-save-manifest")); scroller.Offset=new Vector(0,scroller.Extent.Height); Flush(window);
            if(artifacts is not null && width==320 && height==720 && ((language=="en"&&light&&scale==2)||(language=="zh"&&!light&&scale==1)))
            {
                Directory.CreateDirectory(artifacts); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Dispatcher.UIThread.RunJobs();
                using var image=new RenderTargetBitmap(new((int)(width*scale),(int)(height*scale)),new(96*scale,96*scale)); image.Render(window); image.Save(Path.Combine(artifacts,"hash-export-"+language+".png"),PngBitmapEncoderOptions.Default);
            }
            count++;
        }
        Check(count==96,"complete successful-export layout matrix");
    }
    private static void Calculate(IslandWindow window,string source,int algorithm)
    {
        window.SelectToolForTest("hash"); Flush(window); Find<TextBox>(window,"hash-path").Text=source; Find<TextBox>(window,"hash-expected").Text=""; Find<ComboBox>(window,"hash-algorithm").SelectedIndex=algorithm;
        var button=Find<Button>(window,"hash-compute"); Click(button); Wait(window,()=>button.IsEnabled); Check(!string.IsNullOrEmpty(Find<TextBox>(window,"hash-result").Text),"fixture checksum calculated");
    }
    private static Task StartSave(IslandWindow window){Click(Find<Button>(window,"hash-save-manifest"));return window.LatestChecksumExport;}
    private static IEnumerable<Button> ExportButtons(Window window)=>new[]{Find<Button>(window,"hash-copy"),Find<Button>(window,"hash-copy-manifest"),Find<Button>(window,"hash-save-manifest")};
    private static Button Header(Window window,string text)=>window.GetVisualDescendants().OfType<Button>().First(button=>button.Content is TextBlock label && label.Text==text);
    private static T Find<T>(Window window,string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(control=>control.Name==name);
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Flush(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static void Wait(Window window,Func<bool> done){var timer=Stopwatch.StartNew();while(!done()&&timer.Elapsed<TimeSpan.FromSeconds(10)){Flush(window);Thread.Sleep(2);}Flush(window);Check(done(),"async action completes before deadline");}
    private static void Await(Window window,Task task){Wait(window,()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    private static void Check(bool condition,string label){if(!condition)throw new InvalidOperationException("Checksum export: "+label);}
}
