using System;
using System.Globalization;

namespace CodexIsland
{
    public static class TimeTools
    {
        private static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        public static DateTime ParseDate(string text)
        {
            DateTime value;
            if (!DateTime.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                throw new ArgumentException("日期请使用 yyyy-MM-dd，例如 2026-10-22。");
            return DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);
        }
        public static DateTime ParseTime(string text)
        {
            DateTime value;
            if (!DateTime.TryParseExact((text ?? "").Trim(), new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                throw new ArgumentException("时间请使用 yyyy-MM-dd HH:mm:ss，可附加 .fff 毫秒；时区由下方选择。");
            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }
        public static int ParseDays(string text)
        {
            int value;
            if (!Int32.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("天数请输入整数，负数表示向前推算。");
            return value;
        }
        public static int DaysBetween(DateTime start, DateTime end, bool includeStart, bool weekdaysOnly)
        {
            start = start.Date; end = end.Date;
            if (end < start) throw new ArgumentException("结束日期不能早于开始日期，可点击交换日期。");
            int count = (int)(end - start).TotalDays + (includeStart ? 1 : 0);
            if (!weekdaysOnly || count == 0) return count;
            DateTime first = includeStart ? start : start.AddDays(1);
            int fullWeeks = count / 7, remainder = count % 7, result = fullWeeks * 5;
            for (int i = 0; i < remainder; i++) if (IsWeekday(first.AddDays(i))) result++;
            return result;
        }
        public static DateTime AddDays(DateTime start, int amount, bool weekdaysOnly)
        {
            start = start.Date;
            try
            {
                if (!weekdaysOnly || amount == 0) return start.AddDays(amount);
                int direction = amount < 0 ? -1 : 1;
                long remaining = Math.Abs((long)amount);
                // A weekend start does not count. First move to a business day before skipping whole weeks.
                while (remaining > 0 && !IsWeekday(start))
                {
                    start = start.AddDays(direction);
                    if (IsWeekday(start)) remaining--;
                }
                long weeks = remaining / 5;
                start = start.AddDays(weeks * 7 * direction); remaining %= 5;
                while (remaining > 0)
                {
                    start = start.AddDays(direction);
                    if (IsWeekday(start)) remaining--;
                }
                return start;
            }
            catch (ArgumentOutOfRangeException) { throw new ArgumentException("结果超出支持的日期范围：0001-01-01 至 9999-12-31。"); }
        }
        private static bool IsWeekday(DateTime date) { return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday; }
        public static DateTimeOffset FromUnix(string text, bool milliseconds)
        {
            long value;
            if (!Int64.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("时间戳请输入整数；请明确选择秒或毫秒。");
            try
            {
                long ticks = checked(EpochTicks + checked(value * (milliseconds ? TimeSpan.TicksPerMillisecond : TimeSpan.TicksPerSecond)));
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new OverflowException();
                return new DateTimeOffset(ticks, TimeSpan.Zero);
            }
            catch (OverflowException) { throw new ArgumentException("时间戳超出支持的范围：公元 0001 至 9999 年。"); }
        }
        public static DateTimeOffset ToUtc(DateTime wallTime, TimeZoneInfo zone)
        {
            if (zone == null) throw new ArgumentException("所选时区不可用。");
            wallTime = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wallTime)) throw new ArgumentException("此时间因夏令时跳转而不存在，请输入另一时间或选择 UTC。");
            if (zone.IsAmbiguousTime(wallTime)) throw new ArgumentException("此时间在夏令时回拨时出现两次，请换算并输入明确的 UTC 时间。");
            long ticks = wallTime.Ticks - zone.GetUtcOffset(wallTime).Ticks;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new ArgumentException("换算后的 UTC 时间超出公元 0001 至 9999 年。");
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        public static long ToUnix(DateTimeOffset instant, bool milliseconds)
        {
            long ticks = instant.UtcDateTime.Ticks - EpochTicks;
            long unit = milliseconds ? TimeSpan.TicksPerMillisecond : TimeSpan.TicksPerSecond;
            long value = ticks / unit;
            // Unix timestamps round down, including the fractional second immediately before the epoch.
            return ticks < 0 && ticks % unit != 0 ? value - 1 : value;
        }
        public static string FormatInstant(DateTimeOffset instant, TimeZoneInfo zone)
        {
            if (zone == null) return "此设备未提供该时区";
            var offset = zone.GetUtcOffset(instant);
            long ticks = instant.UtcDateTime.Ticks + offset.Ticks;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return "该时区的日期超出支持范围";
            return new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + FormatOffset(offset);
        }
        public static string FormatOffset(TimeSpan offset)
        {
            string sign = offset < TimeSpan.Zero ? "−" : "+"; offset = offset.Duration();
            return "UTC" + sign + ((int)offset.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }
        public static TimeZoneInfo FindZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { return null; }
            catch (InvalidTimeZoneException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }
    }
}
