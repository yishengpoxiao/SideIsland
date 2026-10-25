using System;
using System.Globalization;

namespace CodexIsland
{
    public static class Calculator
    {
        public static double Evaluate(string expression)
        {
            if (String.IsNullOrWhiteSpace(expression) || expression.Length > 256) throw new ArgumentException("请输入 256 字以内的算式。");
            var parser = new Parser(expression); double result = parser.Expression(); parser.Space();
            if (!parser.End) throw new ArgumentException("存在不支持的字符或多余内容。");
            return Finite(result);
        }
        private static double Finite(double value) { if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("结果超出范围或运算无定义。"); return value; }
        private sealed class Parser
        {
            private readonly string text; private int position, depth;
            public Parser(string value) { text = value; }
            public bool End { get { return position == text.Length; } }
            public void Space() { while (position < text.Length && Char.IsWhiteSpace(text[position])) position++; }
            private bool Take(char c) { Space(); if (position < text.Length && text[position] == c) { position++; return true; } return false; }
            public double Expression() { double value = Term(); while (true) { if (Take('+')) value = Finite(value + Term()); else if (Take('-')) value = Finite(value - Term()); else return value; } }
            private double Term() { double value = Unary(); while (true) { if (Take('*')) value = Finite(value * Unary()); else if (Take('/')) { double denominator = Unary(); if (denominator == 0) throw new ArgumentException("不能除以零。"); value = Finite(value / denominator); } else return value; } }
            private double Unary() { if (++depth > 32) throw new ArgumentException("算式嵌套过深。"); try { if (Take('+')) return Unary(); if (Take('-')) return -Unary(); return Power(); } finally { depth--; } }
            private double Power() { double value = Atom(); while (Take('%')) value /= 100; if (Take('^')) value = Finite(Math.Pow(value, Unary())); return value; }
            private double Atom()
            {
                if (Take('(')) { double value = Expression(); if (!Take(')')) throw new ArgumentException("括号不匹配。"); return value; }
                Space(); int start = position;
                while (position < text.Length && Char.IsLetter(text[position])) position++;
                if (position > start)
                {
                    string name = text.Substring(start, position - start).ToLowerInvariant();
                    if (name == "pi") return Math.PI; if (name == "e") return Math.E;
                    if (name != "sqrt" && name != "abs" && name != "round") throw new ArgumentException("支持 sqrt、abs、round、pi、e。");
                    if (!Take('(')) throw new ArgumentException("函数参数需要括号。"); double value = Expression(); if (!Take(')')) throw new ArgumentException("括号不匹配。");
                    return Finite(name == "sqrt" ? Math.Sqrt(value) : name == "abs" ? Math.Abs(value) : Math.Round(value, MidpointRounding.AwayFromZero));
                }
                while (position < text.Length && (Char.IsDigit(text[position]) || text[position] == '.')) position++;
                if (position < text.Length && (text[position] == 'e' || text[position] == 'E')) { position++; if (position < text.Length && (text[position] == '+' || text[position] == '-')) position++; while (position < text.Length && Char.IsDigit(text[position])) position++; }
                double number;
                if (position == start || !Double.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new ArgumentException("数字或算式不完整。");
                return Finite(number);
            }
        }
    }
}
