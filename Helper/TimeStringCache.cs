using System;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 時間字串快取池 (Flyweight Pattern)。
    /// 預先配置一日 86400 秒的時間字串，使得高頻呼叫取得時間字串時達到 100% Zero Allocation。
    /// </summary>
    public static class TimeStringCache
    {
        private static readonly string[] _cache;

        static TimeStringCache()
        {
            _cache = new string[86400];
            for (int i = 0; i < 86400; i++)
            {
                int h = i / 3600;
                int m = (i % 3600) / 60;
                int s = i % 60;
                _cache[i] = $"{h:D2}{m:D2}{s:D2}";
            }
        }

        /// <summary>
        /// 從快取中取得秒數對應的時間字串，不會產生任何 GC 配置。
        /// </summary>
        /// <param name="timeVal">當日累計秒數 (例如 08:45:00 是 31500)</param>
        /// <returns>時間字串，例如 "084500"</returns>
        public static string GetTimeStr(int timeVal)
        {
            if (timeVal < 0) timeVal = 0;
            if (timeVal >= 86400) timeVal %= 86400; // 跨日保護
            return _cache[timeVal];
        }

        /// <summary>
        /// 取得可讀的格式化時間 (如 08:45:00)。注意：如果沒有預快取此格式，此方法可能會產生配置。
        /// 若要在極限環境下也 0 allocation，可再建立另一組 FormatCache。
        /// 此專案以原本 "084500" 格式進行處理。
        /// </summary>
        public static string GetFormattedTimeStr(int timeVal)
        {
            if (timeVal < 0) timeVal = 0;
            if (timeVal >= 86400) timeVal %= 86400;
            // 若為效能極致，我們也可以做一個 _formattedCache。這裡僅示範或可擴充。
            string t = _cache[timeVal];
            return $"{t[0]}{t[1]}:{t[2]}{t[3]}:{t[4]}{t[5]}";
        }
    }
}
