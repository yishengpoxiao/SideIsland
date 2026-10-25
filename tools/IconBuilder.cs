using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconBuilder
{
    public static void Main(string[] args)
    {
        using (var image = new Bitmap(256, 256))
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using (var brush = new SolidBrush(Color.FromArgb(17, 21, 24))) graphics.FillEllipse(brush, 8, 8, 240, 240);
            using (var track = new Pen(Color.FromArgb(46, 58, 56), 13)) graphics.DrawEllipse(track, 52, 52, 152, 152);
            using (var arc = new Pen(Color.FromArgb(161, 234, 207), 13)) { arc.StartCap = LineCap.Round; arc.EndCap = LineCap.Round; graphics.DrawArc(arc, 52, 52, 152, 152, -90, 275); }
            using (var star = new SolidBrush(Color.FromArgb(220, 247, 234))) graphics.FillPolygon(star, new[] { new Point(128, 84), new Point(140, 116), new Point(172, 128), new Point(140, 140), new Point(128, 172), new Point(116, 140), new Point(84, 128), new Point(116, 116) });
            using (var png = new MemoryStream())
            {
                image.Save(png, ImageFormat.Png);
                byte[] bytes = png.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
                using (var writer = new BinaryWriter(File.Create(args[0])))
                {
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(bytes.Length); writer.Write(22); writer.Write(bytes);
                }
            }
        }
    }
}
