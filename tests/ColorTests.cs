using System;
using System.Windows.Media;

namespace CodexIsland
{
    internal static class ColorTests
    {
        public static void Run()
        {
            Color value;
            Check(ColorTools.TryHex(" #8aC ", out value) && ColorTools.Hex(value) == "#88AACC", "short hex expansion");
            Check(ColorTools.TryHex("a1eacf", out value) && ColorTools.Rgb(value) == "rgb(161, 234, 207)", "RGB representation");
            foreach (string invalid in new[] { "", "ffff", "1234567", "##ffffff", "#GG0000", "0x112233", "rgba(0,0,0,0)" }) Check(!ColorTools.TryHex(invalid, out value), "invalid hex");
            Check(ColorTools.Hsl(Colors.Red) == "hsl(0, 100%, 50%)", "red HSL");
            Check(ColorTools.Hsl(Colors.White) == "hsl(0, 0%, 100%)", "white HSL");
            Check(ColorTools.Hsl(Colors.Blue) == "hsl(240, 100%, 50%)", "blue HSL");
            Check(Math.Abs(ColorTools.Contrast(Colors.Black, Colors.White) - 21) < .000001, "black and white contrast");
            Check(ColorTools.Contrast(Colors.Red, Colors.Red) == 1, "identical contrast");
            Check(ColorTools.Complement(Colors.Red) == Colors.Cyan, "RGB inverse");
            using (var panel = new ColorPanel(true))
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var input = (System.Windows.Controls.TextBox)typeof(ColorPanel).GetField("input", flags).GetValue(panel);
                var hex = (System.Windows.Controls.TextBox)typeof(ColorPanel).GetField("hex", flags).GetValue(panel);
                var status = (System.Windows.Controls.TextBlock)typeof(ColorPanel).GetField("status", flags).GetValue(panel);
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(ColorPanel).GetField("timer", flags).GetValue(panel);
                input.Text = "#f08"; typeof(ColorPanel).GetMethod("ApplyInput", flags).Invoke(panel, null);
                Check(hex.Text == "#FF0088", "real controls normalize an entered color");
                input.Text = "#NOPE"; typeof(ColorPanel).GetMethod("ApplyInput", flags).Invoke(panel, null);
                Check(hex.Text == "#FF0088" && status.Text.Contains("请输入"), "invalid color preserves the last successful sample with an error");
                typeof(ColorPanel).GetMethod("BeginPick", flags).Invoke(panel, null);
                Check(!timer.IsEnabled && status.Text.Contains("演示模式"), "demo screen-pick action never schedules a native screen read");
                panel.CancelPick();
                Check(!timer.IsEnabled, "cancel stops pending color sampling");
            }
            Console.WriteLine("PASS: color parsing, representations and contrast; no screen sampling performed.");
        }
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("Color: " + name); }
    }
}
