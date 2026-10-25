using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Bitmap = System.Drawing.Bitmap;

// Offline font-metrics/native-PNG analysis only. No HWND, Dispatcher, screen
// capture or product execution. The original all-header failure is not edited.
internal static class AnalyzeChromeGlyphPixels
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=16*1024*1024};
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1)throw new ArgumentException("Pass the preserved production chrome pixel evidence directory.");
            string directory=Path.GetFullPath(args[0]);var cells=new List<object>();bool passed=true;int comparisons=0;
            foreach(string baselineFile in Directory.GetFiles(directory,"*-messages-header.json"))
            {
                string cell=Path.GetFileName(baselineFile).Replace("-messages-header.json","");
                var baseline=Read(baselineFile);Rect actual=Parse((string)baseline["ActualBox"]),physical=Parse((string)baseline["PhysicalBox"]);
                double sx=physical.Width/actual.Width,sy=physical.Height/actual.Height;
                var bounds=new List<object>();bool[] mask;int width,height;
                using(var image=new Bitmap(Path.ChangeExtension(baselineFile,".png"))){width=image.Width;height=image.Height;mask=new bool[width*height];}
                // Union every recorded route, so shifted glyphs could not evade
                // comparison. Metadata equality is independently mandatory.
                foreach(string recordFile in Directory.GetFiles(directory,cell+"-*-header.json"))
                {
                    var record=Read(recordFile);
                    foreach(object item in (object[])record["Glyphs"])
                    {
                        var glyph=(Dictionary<string,object>)item;var indices=new List<ushort>();var advances=new List<double>();
                        foreach(object value in (object[])glyph["GlyphIndices"])indices.Add(Convert.ToUInt16(value,CultureInfo.InvariantCulture));
                        foreach(object value in (object[])glyph["Advances"])advances.Add(Convert.ToDouble(value,CultureInfo.InvariantCulture));
                        var typeface=new GlyphTypeface(new Uri((string)glyph["Typeface"],UriKind.Absolute));
                        var run=new GlyphRun(typeface,0,false,Number(glyph,"EmSize"),(float)sx,indices,new Point(),advances,null,null,null,null,null,XmlLanguage.GetLanguage("en-US"));
                        Rect ink=run.ComputeInkBoundingBox();
                        if(ink.IsEmpty)continue;
                        double bx=Number(glyph,"BaselineX"),by=Number(glyph,"BaselineY");
                        // ComputeInkBoundingBox is relative to the baseline;
                        // mapping uses the recorded native/DIP scale, not an
                        // assumed global DPI. Include all ink plus two pixels.
                        int left=Math.Max(0,(int)Math.Floor((bx+ink.Left-actual.Left)*sx)-2);
                        int top=Math.Max(0,(int)Math.Floor((by+ink.Top-actual.Top)*sy)-2);
                        int right=Math.Min(width,(int)Math.Ceiling((bx+ink.Right-actual.Left)*sx)+2);
                        int bottom=Math.Min(height,(int)Math.Ceiling((by+ink.Bottom-actual.Top)*sy)+2);
                        for(int y=top;y<bottom;y++)for(int x=left;x<right;x++)mask[y*width+x]=true;
                        if(recordFile==baselineFile)bounds.Add(new {Text=glyph["Text"],Typeface=glyph["Typeface"],EmSize=glyph["EmSize"],BaselineX=bx,BaselineY=by,
                            InkBoundsRelativeToBaseline=ink.ToString(CultureInfo.InvariantCulture),MaskLeft=left,MaskTop=top,MaskRightExclusive=right,MaskBottomExclusive=bottom});
                    }
                }
                int maskPixels=0;foreach(bool value in mask)if(value)maskPixels++;
                if(maskPixels==0)throw new InvalidOperationException("Glyph mask is empty: "+cell);
                var routes=new List<object>();
                using(var firstHeader=new Bitmap(Path.ChangeExtension(baselineFile,".png")))
                using(var firstFooter=new Bitmap(Path.Combine(directory,cell+"-messages-footer.png")))
                {
                    var firstFooterMeta=Read(Path.Combine(directory,cell+"-messages-footer.json"));
                    foreach(string headerFile in Directory.GetFiles(directory,cell+"-*-header.json"))
                    {
                        string stem=Path.GetFileNameWithoutExtension(headerFile).Replace("-header","");var metadata=Read(headerFile);
                        var footerMeta=Read(Path.Combine(directory,stem+"-footer.json"));
                        bool geometryEqual=SameMetadata(baseline,metadata)&&SameMetadata(firstFooterMeta,footerMeta);
                        int changed=0,maskedChanged=0,footerChanged=0,maximum=0;var outside=new List<object>();
                        using(var current=new Bitmap(Path.ChangeExtension(headerFile,".png")))
                        using(var footer=new Bitmap(Path.Combine(directory,stem+"-footer.png")))
                        {
                            if(current.Size!=firstHeader.Size||footer.Size!=firstFooter.Size)throw new InvalidOperationException("Native crop footprint changed: "+stem);
                            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                            {
                                var before=firstHeader.GetPixel(x,y);var after=current.GetPixel(x,y);
                                if(before==after)continue;changed++;
                                maximum=Math.Max(maximum,Math.Max(Math.Abs(before.R-after.R),Math.Max(Math.Abs(before.G-after.G),Math.Abs(before.B-after.B))));
                                if(mask[y*width+x])maskedChanged++;
                                else outside.Add(new {X=x,Y=y,BeforeR=before.R,BeforeG=before.G,BeforeB=before.B,AfterR=after.R,AfterG=after.G,AfterB=after.B});
                            }
                            for(int y=0;y<footer.Height;y++)for(int x=0;x<footer.Width;x++)if(footer.GetPixel(x,y)!=firstFooter.GetPixel(x,y))footerChanged++;
                        }
                        bool routePass=geometryEqual&&maskedChanged==0&&footerChanged==0;passed&=routePass;comparisons++;
                        routes.Add(new {Route=stem,GeometryMetadataExactlyEqual=geometryEqual,HeaderAllPixelsChanged=changed,HeaderAllPixelsMaximumChannelDelta=maximum,
                            HeaderGlyphMaskChangedPixels=maskedChanged,FooterAllPixelsChanged=footerChanged,StrictGlyphAndFooterPass=routePass,OutsideGlyphMaskChanges=outside});
                    }
                }
                cells.Add(new {Cell=cell,DpiScaleX=sx,DpiScaleY=sy,HeaderWidth=width,HeaderHeight=height,GlyphMaskPixels=maskPixels,
                    PhysicalPaddingPixels=2,ReconstructedRunBounds=bounds,Comparisons=routes});
            }
            if(comparisons!=10)throw new InvalidOperationException("Expected exactly10 preserved route images, got "+comparisons);
            string report=Path.Combine(directory,"glyph-mask-analysis.json");
            File.WriteAllText(report,Json.Serialize(new {Kind="offline-native-PNG-exact-glyph-and-footer-comparison",UIExecuted=false,ProductExecuted=false,
                OriginalAllHeaderStrictFailurePreserved=true,OriginalResultsSHA256=Hash(Path.Combine(directory,"results.json")),PixelThresholdUsed=false,
                Method="Reconstruct GlyphRun using recorded Typeface URI (including TTC face), glyph indices, em size, advances and actual1.5 PixelsPerDip; ComputeInkBoundingBox relative to baseline; transform recorded DIP/native boxes; union every route; inflate exactly2physical pixels; compare every RGB byte without a faint-ink threshold.",
                MetadataLimitation="Original records omit GlyphOffsets/bidi/sideways; these fixed LTR Latin/CJK/navigation captions have no combining sequence or sideways transform. No synthetic text/font fallback is substituted; original per-run Typeface and glyph indices are retained.",
                Passed=passed,Comparisons=10,Cells=cells}),new UTF8Encoding(false));
            Console.WriteLine("OFFLINE glyph mask +2px exact RGB/footer pass="+passed+" comparisons="+comparisons+" report="+report);
            return passed?0:1;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static bool SameMetadata(Dictionary<string,object> first,Dictionary<string,object> current)
    {return (string)first["ActualBox"]==(string)current["ActualBox"]&&(string)first["PhysicalBox"]==(string)current["PhysicalBox"]&&(string)first["GlyphSHA256"]==(string)current["GlyphSHA256"];}
    private static double Number(Dictionary<string,object> item,string key){return Convert.ToDouble(item[key],CultureInfo.InvariantCulture);}
    private static Rect Parse(string value){string[] fields=value.Split(',');return new Rect(Double.Parse(fields[0],CultureInfo.InvariantCulture),Double.Parse(fields[1],CultureInfo.InvariantCulture),Double.Parse(fields[2],CultureInfo.InvariantCulture),Double.Parse(fields[3],CultureInfo.InvariantCulture));}
    private static Dictionary<string,object> Read(string path){return (Dictionary<string,object>)Json.DeserializeObject(File.ReadAllText(path));}
    private static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
}
