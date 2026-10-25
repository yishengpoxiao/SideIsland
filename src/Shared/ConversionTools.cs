using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexIsland
{
    public sealed class ConversionUnit
    {
        public string Name { get; private set; }
        public double Factor { get; private set; }
        public double Offset { get; private set; }
        public ConversionUnit(string name, double factor, double offset = 0) { Name = name; Factor = factor; Offset = offset; }
        public override string ToString() { return Name; }
    }

    public sealed class ConversionCategory
    {
        public string Name { get; private set; }
        public ConversionUnit[] Units { get; private set; }
        public ConversionCategory(string name, params ConversionUnit[] units) { Name = name; Units = units; }
        public override string ToString() { return Name; }
    }

    public static class ConversionTools
    {
        public static readonly ConversionCategory[] Categories = {
            new ConversionCategory("长度", U("米 m", 1), U("千米 km", 1000), U("厘米 cm", .01), U("毫米 mm", .001), U("英寸 in", .0254), U("英尺 ft", .3048), U("码 yd", .9144), U("英里 mi", 1609.344), U("海里 nmi", 1852)),
            new ConversionCategory("质量", U("千克 kg", 1), U("克 g", .001), U("毫克 mg", .000001), U("吨 t", 1000), U("磅 lb", .45359237), U("盎司 oz", .028349523125), U("市斤", .5)),
            new ConversionCategory("温度", new ConversionUnit("摄氏度 °C", 1, 273.15), new ConversionUnit("华氏度 °F", 5.0 / 9, 273.15 - 32 * 5.0 / 9), U("开尔文 K", 1)),
            new ConversionCategory("面积", U("平方米 m²", 1), U("平方千米 km²", 1000000), U("平方厘米 cm²", .0001), U("公顷 ha", 10000), U("亩", 2000.0 / 3), U("平方英尺 ft²", .09290304), U("英亩 acre", 4046.8564224)),
            new ConversionCategory("体积", U("升 L", 1), U("毫升 mL", .001), U("立方米 m³", 1000), U("立方厘米 cm³", .001), U("美制加仑 gal", 3.785411784), U("英制加仑 gal", 4.54609), U("美制液盎司 fl oz", .0295735295625)),
            new ConversionCategory("速度", U("米/秒 m/s", 1), U("千米/小时 km/h", 1.0 / 3.6), U("英里/小时 mph", .44704), U("节 kn", 1852.0 / 3600), U("英尺/秒 ft/s", .3048)),
            new ConversionCategory("数据容量", U("字节 B", 1), U("位 bit", .125), U("KB (1000 B)", 1000), U("MB (1000² B)", 1000000), U("GB (1000³ B)", 1000000000), U("TB (1000⁴ B)", 1000000000000), U("KiB (1024 B)", 1024), U("MiB (1024² B)", 1048576), U("GiB (1024³ B)", 1073741824), U("TiB (1024⁴ B)", 1099511627776)),
            new ConversionCategory("时间", U("秒 s", 1), U("毫秒 ms", .001), U("分钟 min", 60), U("小时 h", 3600), U("天 d", 86400), U("周", 604800)),
            new ConversionCategory("能量", U("焦耳 J", 1), U("千焦 kJ", 1000), U("卡 cal (热化学)", 4.184), U("千卡 kcal", 4184), U("瓦时 Wh", 3600), U("千瓦时 kWh", 3600000)),
            new ConversionCategory("压力", U("帕 Pa", 1), U("千帕 kPa", 1000), U("兆帕 MPa", 1000000), U("巴 bar", 100000), U("标准大气压 atm", 101325), U("psi", 6894.757293168))
        };
        private static ConversionUnit U(string name, double factor) { return new ConversionUnit(name, factor); }
        public static double Parse(string text)
        {
            double value;
            if (String.IsNullOrWhiteSpace(text) || text.Length > 100 || !Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value))
                throw new ArgumentException("请输入有限数值，使用小数点；支持 1.5e3。不要使用千位分隔符。");
            return value;
        }
        public static double Convert(ConversionCategory category, ConversionUnit from, ConversionUnit to, double value)
        {
            if (category == null || from == null || to == null || !category.Units.Contains(from) || !category.Units.Contains(to)) throw new ArgumentException("请选择同一类别的有效单位。");
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("数值必须是有限数。");
            if (category.Name == "温度")
            {
                double minimum = from.Name.StartsWith("摄氏") ? -273.15 : from.Name.StartsWith("华氏") ? -459.67 : 0;
                if (value < minimum) throw new ArgumentException("温度不能低于绝对零度（−273.15 °C / −459.67 °F / 0 K）。");
            }
            // Avoid overflowing/underflowing a temporary base value when the final result fits.
            // Identity conversion also retains subnormal values exactly.
            if (Object.ReferenceEquals(from, to)) return value == 0 ? 0 : value;
            double result;
            if (from.Offset == 0 && to.Offset == 0) result = value * (from.Factor / to.Factor);
            else
            {
                double basis = value * from.Factor + from.Offset;
                if (category.Name == "温度" && basis < 0 && basis > -1e-10) basis = 0;
                result = (basis - to.Offset) / to.Factor;
            }
            if (Double.IsNaN(result) || Double.IsInfinity(result)) throw new ArgumentException("换算结果超出范围，请减小数值。");
            return result == 0 ? 0 : result;
        }
        public static string Format(double value) { return value.ToString("G15", CultureInfo.InvariantCulture); }
    }
}
