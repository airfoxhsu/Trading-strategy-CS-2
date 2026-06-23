using System;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 高精時間解析工具。
    /// 負責將交易所成交時間戳記轉換為累積秒數。
    /// </summary>
    public static class TimeParser
    {
        /// <summary>
        /// 解析時間字串為當日累積秒數 (支援交易所 12 位元高精微秒格式與 HH:mm:ss 格式)。
        /// (Zero Allocation Lock-Free)
        /// </summary>
        public static double ParseTime(string timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr))
                return 0.0;
                
            return ParseTimeSpan(timeStr.AsSpan().Trim());
        }

        private static double ParseTimeSpan(ReadOnlySpan<char> timeSpan)
        {
            if (timeSpan.IsEmpty)
                return 0.0;

            // 1. 處理帶有冒號 `:` 的標準格式 (例如 "15:04:02.223")
            int colonIdx = timeSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                try
                {
                    int dotIdx = timeSpan.IndexOf('.');
                    ReadOnlySpan<char> hmsSpan = dotIdx >= 0 ? timeSpan.Slice(0, dotIdx) : timeSpan;
                    ReadOnlySpan<char> msSpan = dotIdx >= 0 ? timeSpan.Slice(dotIdx + 1) : default;

                    int c1 = hmsSpan.IndexOf(':');
                    int c2 = hmsSpan.LastIndexOf(':');

                    if (c1 > 0 && c2 > c1)
                    {
                        int h = int.Parse(hmsSpan.Slice(0, c1));
                        int m = int.Parse(hmsSpan.Slice(c1 + 1, c2 - c1 - 1));
                        int s = int.Parse(hmsSpan.Slice(c2 + 1));

                        double ms = 0.0;
                        if (!msSpan.IsEmpty)
                        {
                            int msLen = msSpan.Length;
                            if (msLen == 2) ms = int.Parse(msSpan) / 100.0;
                            else if (msLen == 3) ms = int.Parse(msSpan) / 1000.0;
                            else if (msLen == 6) ms = int.Parse(msSpan) / 1000000.0;
                            else ms = int.Parse(msSpan) / Math.Pow(10, msLen);
                        }
                        return h * 3600 + m * 60 + s + ms;
                    }
                    else if (c1 > 0 && c2 == c1)
                    {
                        // 處理 HH:mm 格式 (Excel 可能截斷秒數)
                        int h = int.Parse(hmsSpan.Slice(0, c1));
                        int m = int.Parse(hmsSpan.Slice(c1 + 1));
                        int s = 0;

                        double ms = 0.0;
                        if (!msSpan.IsEmpty)
                        {
                            int msLen = msSpan.Length;
                            if (msLen == 2) ms = int.Parse(msSpan) / 100.0;
                            else if (msLen == 3) ms = int.Parse(msSpan) / 1000.0;
                            else if (msLen == 6) ms = int.Parse(msSpan) / 1000000.0;
                            else ms = int.Parse(msSpan) / Math.Pow(10, msLen);
                        }
                        return h * 3600 + m * 60 + s + ms;
                    }
                }
                catch { }
                return 0.0;
            }

            // 2. 處理純數字格式 (例如 "150405143000" 或 "095957612000" 或 "84500" 或 "0845")
            // 智慧補零：若長度為 11 (如早上 9 點 93005143000)，虛擬為 12 位
            bool padZero = timeSpan.Length == 11;
            int effectiveLength = padZero ? 12 : timeSpan.Length;

            if (effectiveLength >= 4)
            {
                try
                {
                    int h = 0, m = 0, s = 0;
                    ReadOnlySpan<char> msSpan = default;
                    
                    if (padZero)
                    {
                        h = timeSpan[0] - '0';
                        m = int.Parse(timeSpan.Slice(1, 2));
                        s = int.Parse(timeSpan.Slice(3, 2));
                        msSpan = timeSpan.Slice(5);
                    }
                    else if (effectiveLength >= 6)
                    {
                        h = int.Parse(timeSpan.Slice(0, 2));
                        m = int.Parse(timeSpan.Slice(2, 2));
                        s = int.Parse(timeSpan.Slice(4, 2));
                        msSpan = timeSpan.Slice(6);
                    }
                    else if (effectiveLength == 5)
                    {
                        // 可能是 Hmmss 如 "84500"
                        h = timeSpan[0] - '0';
                        m = int.Parse(timeSpan.Slice(1, 2));
                        s = int.Parse(timeSpan.Slice(3, 2));
                    }
                    else if (effectiveLength == 4)
                    {
                        // 可能是 HHmm 如 "0845" 或 Hmm 如 "845" (這時長度是 3, 但條件為 >=4, 所以只處理 HHmm)
                        h = int.Parse(timeSpan.Slice(0, 2));
                        m = int.Parse(timeSpan.Slice(2, 2));
                    }

                    double ms = 0.0;
                    if (!msSpan.IsEmpty)
                    {
                        int msLen = msSpan.Length;
                        if (msLen == 2) ms = int.Parse(msSpan) / 100.0;
                        else if (msLen == 3) ms = int.Parse(msSpan) / 1000.0;
                        else if (msLen == 6) ms = int.Parse(msSpan) / 1000000.0;
                        else ms = int.Parse(msSpan) / Math.Pow(10, msLen);
                    }
                    
                    return h * 3600 + m * 60 + s + ms;
                }
                catch { }
            }

            return 0.0;
        }

        /// <summary>
        /// 將當日累積秒數格式化為交易員易讀的 HH:mm:ss 字串。
        /// </summary>
        public static string FormatTime(double timeVal)
        {
            timeVal %= 86400.0;
            int h = (int)(timeVal / 3600);
            int m = (int)((timeVal % 3600) / 60);
            int s = (int)(timeVal % 60);
            return $"{h:D2}:{m:D2}:{s:D2}";
        }
    }
}
