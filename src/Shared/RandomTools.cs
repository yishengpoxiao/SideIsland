using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace CodexIsland
{
    internal sealed class PasswordOptions
    {
        public int Length = 20;
        public bool Uppercase = true, Lowercase = true, Digits = true, Symbols = true, ExcludeAmbiguous = true;
    }

    internal sealed class RandomList
    {
        public readonly List<string> Items = new List<string>();
        public int OriginalCount;
        public int DuplicatesRemoved { get { return OriginalCount - Items.Count; } }
    }

    // One provider is reused for all draws in an operation, then disposed. Rejection
    // removes the incomplete bucket, so modulo never favors any possible result.
    internal sealed class SecureRandom : IDisposable
    {
        private readonly RandomNumberGenerator provider = RandomNumberGenerator.Create();
        private readonly byte[] bytes = new byte[4];
        public ulong Below(ulong exclusiveMaximum)
        {
            const ulong range = 1UL << 32;
            if (exclusiveMaximum == 0 || exclusiveMaximum > range) throw new ArgumentOutOfRangeException("exclusiveMaximum");
            ulong limit = range - range % exclusiveMaximum;
            ulong sample;
            do { provider.GetBytes(bytes); sample = BitConverter.ToUInt32(bytes, 0); } while (sample >= limit);
            return sample % exclusiveMaximum;
        }
        public void Dispose() { provider.Dispose(); Array.Clear(bytes, 0, bytes.Length); }
    }

    internal static class RandomTools
    {
        public const int MaximumItems = 500, MaximumListLength = 32768;
        public const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Lowercase = "abcdefghijklmnopqrstuvwxyz", Digits = "0123456789", Symbols = "!@#$%^&*()-_=+[]{}:;,.?/", Ambiguous = "Il1O0";

        internal static List<string> PasswordGroups(PasswordOptions options)
        {
            if (options == null) throw new ArgumentException("请设置密码参数。");
            if (options.Length < 8 || options.Length > 128) throw new ArgumentException("密码长度须为 8 至 128。");
            var groups = new List<string>();
            if (options.Uppercase) groups.Add(Uppercase);
            if (options.Lowercase) groups.Add(Lowercase);
            if (options.Digits) groups.Add(Digits);
            if (options.Symbols) groups.Add(Symbols);
            if (groups.Count == 0) throw new ArgumentException("请至少勾选一种字符类型。");
            if (options.ExcludeAmbiguous)
                for (int i = 0; i < groups.Count; i++) groups[i] = new string(groups[i].Where(c => Ambiguous.IndexOf(c) < 0).ToArray());
            return groups;
        }

        public static string Password(PasswordOptions options)
        {
            var groups = PasswordGroups(options); string alphabet = String.Concat(groups); char[] result = new char[options.Length];
            using (var random = new SecureRandom())
            {
                int position = 0;
                foreach (string group in groups) result[position++] = group[(int)random.Below((ulong)group.Length)];
                while (position < result.Length) result[position++] = alphabet[(int)random.Below((ulong)alphabet.Length)];
                for (int i = result.Length - 1; i > 0; i--) { int j = (int)random.Below((ulong)i + 1); char value = result[i]; result[i] = result[j]; result[j] = value; }
            }
            string password = new string(result); Array.Clear(result, 0, result.Length); return password;
        }

        public static int Integer(int minimum, int maximum)
        {
            if (minimum > maximum) throw new ArgumentException("最小值不能大于最大值。");
            ulong count = (ulong)((long)maximum - minimum + 1);
            using (var random = new SecureRandom()) return (int)(minimum + (long)random.Below(count));
        }

        public static RandomList ParseList(string text, bool deduplicate)
        {
            if (text == null) text = "";
            if (text.Length > MaximumListLength) throw new ArgumentException("名单超过 32,768 个文本单元，请缩短后重试。");
            var result = new RandomList(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (++result.OriginalCount > MaximumItems) throw new ArgumentException("名单最多允许 500 个非空行（包含重复行）。");
                if (!deduplicate || seen.Add(line)) result.Items.Add(line);
            }
            return result;
        }

        public static List<string> Draw(RandomList list, int count)
        {
            if (list == null || list.Items.Count == 0) throw new ArgumentException("请先输入名单，每行一项。");
            if (list.Items.Count > MaximumItems) throw new ArgumentException("名单最多允许 500 项。");
            if (count < 1 || count > list.Items.Count) throw new ArgumentException("抽取数量须为 1 至 " + list.Items.Count + "，且不能超过可选项数。");
            var items = new List<string>(list.Items);
            using (var random = new SecureRandom())
                for (int i = 0; i < count; i++) { int j = i + (int)random.Below((ulong)(items.Count - i)); string value = items[i]; items[i] = items[j]; items[j] = value; }
            return items.GetRange(0, count);
        }
    }
}
