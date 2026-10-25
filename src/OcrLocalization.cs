using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodexIsland
{
    internal static partial class UiLocalization
    {
        private sealed class OcrLanguageConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                var choice = values.Length == 0 ? null : values[0] as OcrLanguageChoice;
                if (choice == null) return "";
                return String.IsNullOrEmpty(choice.Tag) ? Localize(choice.Name) : choice.Name;
            }
            public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }
        internal static ComboBox OcrLanguageCombo(ComboBox target)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock));
            var text = new MultiBinding { Converter = new OcrLanguageConverter() };
            text.Bindings.Add(new Binding());
            text.Bindings.Add(new Binding { Source = State, Path = new PropertyPath(LanguageState.VersionProperty) });
            label.SetBinding(TextBlock.TextProperty, text);
            target.ItemTemplate = new DataTemplate { VisualTree = label }; return target;
        }
        private static void OcrDropCatalog()
        {
            Map(@"▣ 截图提字|▣ Extract text
截图 OCR / 图片文字识别|Screen OCR / Image text recognition
截图提字 OCR|Extract text with OCR
框选屏幕、选择 JPG / PNG 或读取剪贴板图片，在本机识别文字。结果可编辑、复制或送入文字对照。|Select a screen region, choose a JPG / PNG, or read a clipboard image to recognize text locally. Edit, copy or compare the result.
框选屏幕提字|Select screen region
读取剪贴板图片|Read clipboard image
自动（Windows 首选语言）|Automatic (Windows preferred language)
重新识别|Recognize again
取消识别|Cancel recognition
送入文字对照|Send to text comparison
复制文字|Copy text
选择图片或框选屏幕开始识别。|Choose an image or select a screen region to begin.
OCR 使用 Windows 已安装的语言包，不联网。较大图片会缩小至最长边 4,096 像素；识别准确度受字号、清晰度及语言影响。|OCR uses installed Windows language packs locally. Large images are resized to a maximum edge of 4,096 pixels. Accuracy depends on font size, clarity and language.
演示模式不读取真实屏幕、图片或剪贴板。|Demo mode does not read your screen, images or clipboard.
未安装 OCR 语言包，请在 Windows 设置的语言选项中添加文字识别。|No OCR language pack is installed. Add text recognition in Windows language options.
自动使用首选语言；也可指定已安装的 OCR 语言。|Use the preferred language automatically, or choose an installed OCR language.
Windows OCR 暂不可用，请使用 Windows 10 / 11 并检查识别语言包。|Windows OCR is unavailable. Use Windows 10 / 11 and check your OCR language packs.
选择提字图片|Choose image for OCR
图片|Images
正在读取图片…|Reading image…
已取消识别。|Recognition cancelled.
请拖动框选需要识别的区域，Esc 取消。|Drag to select a text region. Press Esc to cancel.
正在读取剪贴板图片…|Reading clipboard image…
剪贴板中没有可读取的图片。|No readable image was found on the clipboard.
正在本机识别文字…|Recognizing text locally…
未识别到文字，请尝试放大文字或切换识别语言。|No text was recognized. Try larger text or another recognition language.
识别完成，可编辑结果后复制或对照。|Recognition complete. Edit, copy or compare the result.
文字已复制。|Text copied.
识别未完成，请检查图片、Windows OCR 语言包和访问权限后重试。|Recognition failed. Check the image, Windows OCR language packs and access permissions, then retry.
请选择有效图片，最多支持 3200 万像素。|Choose a valid image up to 32 million pixels.
识别文字超过 120,000 字符，请缩小区域后重试。|Recognized text exceeds 120,000 characters. Select a smaller region.
此识别语言不可用，请在 Windows 设置中安装对应的 OCR 语言包，或选择其他语言。|This OCR language is unavailable. Install its OCR language pack in Windows settings or choose another language.
剪贴板读取超时，请重试或选择图片。|Clipboard read timed out. Retry or choose an image file.
本机文字识别超时，请缩小图片或区域后重试。|Local text recognition timed out. Try a smaller image or screen region.
识别正在进行，请先取消后再选择新图片。|Recognition is running. Cancel it before choosing a new image.
压缩或调整图片|Compress or resize image
将图片钉在桌面|Pin image on desktop
识别图片中的文字|Recognize text in image
计算文件校验值|Calculate file checksum
图片读取失败，请确认文件是有效的 JPG / PNG 图片。|Could not read the image. Check that it is a valid JPG / PNG.");
        }
    }
}
