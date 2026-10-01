using System;

namespace Kare.Space.Core
{
    /// <summary>
    /// Игровое время — секунды от J2000.0 (2000-01-01 12:00). Разницу TT/UTC (~1 мин) не учитываем:
    /// для игры важна согласованность, а не секунды эфемерид.
    /// </summary>
    public static class GameCalendar
    {
        static readonly string[] MonthsRu = { "янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек" };

        /// <summary>Юлианская дата по григорианской дате (Meeus, гл. 7). day может быть дробным.</summary>
        public static double ToJulianDate(int year, int month, double day)
        {
            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }
            int a = year / 100;
            int b = 2 - a + a / 4;
            return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
        }

        public static double ToGameTime(int year, int month, int day, int hour = 0, int minute = 0, double second = 0)
        {
            double jd = ToJulianDate(year, month, day + (hour + (minute + second / 60.0) / 60.0) / 24.0);
            return (jd - Constants.JD_J2000) * Constants.Day;
        }

        public static double ToJulianDate(double gameTime) => Constants.JD_J2000 + gameTime / Constants.Day;

        public static void ToDate(double gameTime, out int year, out int month, out int day, out int hour, out int minute, out double second)
        {
            double jd = ToJulianDate(gameTime) + 0.5;
            double z = Math.Floor(jd);
            double f = jd - z;
            double a = z;
            if (z >= 2299161)
            {
                double alpha = Math.Floor((z - 1867216.25) / 36524.25);
                a = z + 1 + alpha - Math.Floor(alpha / 4);
            }
            double b = a + 1524;
            double c = Math.Floor((b - 122.1) / 365.25);
            double d = Math.Floor(365.25 * c);
            double e = Math.Floor((b - d) / 30.6001);
            day = (int)(b - d - Math.Floor(30.6001 * e));
            month = (int)(e < 14 ? e - 1 : e - 13);
            year = (int)(month > 2 ? c - 4716 : c - 4715);
            double secs = f * Constants.Day;
            // Защита от 23:59:60 из-за округления.
            if (secs >= Constants.Day - 1e-6) secs = Constants.Day - 1e-6;
            hour = (int)(secs / 3600);
            minute = (int)((secs - hour * 3600) / 60);
            second = secs - hour * 3600 - minute * 60;
        }

        /// <summary>«4 окт 1957, 19:28:34».</summary>
        public static string Format(double gameTime)
        {
            ToDate(gameTime, out int y, out int mo, out int d, out int h, out int mi, out double s);
            return $"{d} {MonthsRu[mo - 1]} {y}, {h:00}:{mi:00}:{(int)s:00}";
        }

        /// <summary>Длительность: «2 д 03:14:05», «14:05», «3 г 120 д».</summary>
        public static string FormatDuration(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "—";
            string sign = seconds < 0 ? "−" : "";
            seconds = Math.Abs(seconds);
            double year = 365.25 * Constants.Day;
            if (seconds >= year)
            {
                int years = (int)(seconds / year);
                int days = (int)((seconds - years * year) / Constants.Day);
                return $"{sign}{years} г {days} д";
            }
            int dd = (int)(seconds / Constants.Day);
            double rest = seconds - dd * Constants.Day;
            int hh = (int)(rest / 3600);
            int mm = (int)((rest - hh * 3600) / 60);
            int ss = (int)(rest - hh * 3600 - mm * 60);
            if (dd > 0) return $"{sign}{dd} д {hh:00}:{mm:00}:{ss:00}";
            if (hh > 0) return $"{sign}{hh}:{mm:00}:{ss:00}";
            return $"{sign}{mm:00}:{ss:00}";
        }
    }
}
