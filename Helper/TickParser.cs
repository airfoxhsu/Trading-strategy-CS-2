using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExtremeSignalAppCS.Models;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 提供從日誌文字或原始資料中解析 TradeTick 的共用工具，集中管理正則表達式與盤別判定，消除重複邏輯。
    /// </summary>
    public static partial class TickParser
    {
        [GeneratedRegex(@"Symbol=([^, \t\r\n]+)")]
        private static partial Regex SymbolRegex();

        [GeneratedRegex(@"mattime=([^, \t\r\n]+)")]
        private static partial Regex MatTimeRegex();

        [GeneratedRegex(@"matpri=([-]?\d+)")]
        private static partial Regex MatPriRegex();

        [GeneratedRegex(@"tmatqty=([-]?\d+)")]
        private static partial Regex TMatQtyRegex();

        [GeneratedRegex(@"bestbp=([\d,]*)")]
        private static partial Regex BestBpRegex();

        [GeneratedRegex(@"bestsp=([\d,]*)")]
        private static partial Regex BestSpRegex();

        /// <summary>
        /// 從一行 event.log 文字解析出 TradeTick。
        /// 包含時間、盤別、去重複檢查與內外盤判定。
        /// </summary>
        /// <param name="line">日誌文字</param>
        /// <param name="lastTmatqty">用於去重複的快取字典 (Key: (BaseSym, Session))</param>
        /// <param name="activeSession">可選，指定只解析特定盤別 ("日盤" 或 "夜盤")，傳入 null 則不限制</param>
        /// <param name="filter">可選，傳入時間濾波函式，回傳 false 則略過此筆</param>
        /// <returns>解析成功的 TradeTick，若無效或被過濾則回傳 null</returns>
        public static TradeTick? ParseLogLine(
            string line, 
            Dictionary<(string, string, string), int> lastTmatqty, 
            string? activeSession = null, 
            Func<double, bool>? filter = null,
            string path = "")
        {
            if (!line.Contains("TXF") && !line.Contains("MXF")) return null;

            var match = SymbolRegex().Match(line);
            if (!match.Success) return null;
            string symbol = match.Groups[1].Value;

            string baseSym = symbol.Contains("TXF") ? "TXF" : (symbol.Contains("MXF") ? "MXF" : "");
            if (string.IsNullOrEmpty(baseSym)) return null;

            var mtMatch = MatTimeRegex().Match(line);
            var mpMatch = MatPriRegex().Match(line);
            var tqMatch = TMatQtyRegex().Match(line);

            if (!mtMatch.Success || !mpMatch.Success || !tqMatch.Success) return null;

            string timeStr = mtMatch.Groups[1].Value;
            double tValRaw = TimeParser.ParseTime(timeStr);

            // 擷取 Log 寫入時間 (格式例如 "06:32:16.975") 進行防呆，過濾盤後結算假 Tick
            if (line.Length >= 12 && line[2] == ':' && line[5] == ':')
            {
                int h = (line[0] - '0') * 10 + (line[1] - '0');
                int m = (line[3] - '0') * 10 + (line[4] - '0');
                int s = (line[6] - '0') * 10 + (line[7] - '0');
                double logTimeRaw = h * 3600 + m * 60 + s;
                
                double delay = logTimeRaw - tValRaw;
                if (delay < -43200) delay += 86400;      // 跨日處理: LogTime 在凌晨，mattime 在深夜
                else if (delay > 43200) delay -= 86400;  // 跨日處理 (極端異常保護)
                
                if (delay > 300) 
                    return null; // 延遲超過 5 分鐘，視為結算價或無效 Tick 捨棄
            }

            string session = "";
            double tVal = tValRaw;

            if (tValRaw >= 31500.0 && tValRaw <= 49500.0)
            {
                session = "日盤";
            }
            else if (tValRaw >= 54000.0 || tValRaw <= 18000.0)
            {
                session = "夜盤";
                if (tValRaw <= 18000.0) tVal += 86400.0;
            }
            else
            {
                return null;
            }

            if (activeSession != null && session != activeSession) return null;
            if (filter != null && !filter(tValRaw)) return null;

            int tmatqty = int.Parse(tqMatch.Groups[1].Value);
            var qtyKey = (baseSym, session, path);

            if (tmatqty < 0 || (lastTmatqty.ContainsKey(qtyKey) && tmatqty <= lastTmatqty[qtyKey]))
                return null;

            int tickQty = lastTmatqty.ContainsKey(qtyKey) ? (tmatqty - lastTmatqty[qtyKey]) : 1;
            lastTmatqty[qtyKey] = tmatqty;

            var bpM = BestBpRegex().Match(line);
            var spM = BestSpRegex().Match(line);

            if (!bpM.Success || !spM.Success) return null;

            int bestBp = 0, bestSp = 0;
            try
            {
                string bPrices = bpM.Groups[1].Value;
                string sPrices = spM.Groups[1].Value;

                bestBp = !string.IsNullOrEmpty(bPrices) ? (int)double.Parse(bPrices.Split(',')[0]) : 0;
                bestSp = !string.IsNullOrEmpty(sPrices) ? (int)double.Parse(sPrices.Split(',')[0]) : 0;

                if (bestBp <= 0 || bestSp <= 0) return null;
            }
            catch { return null; }

            int price = int.Parse(mpMatch.Groups[1].Value);
            TradeSide side = TradeSide.Unknown;
            if (price >= bestSp) side = TradeSide.Outer;
            else if (price <= bestBp) side = TradeSide.Inner;

            return new TradeTick(baseSym, timeStr, tVal, price, tickQty, side, bestBp, bestSp, session);
        }

        /// <summary>
        /// 從即時接收的 RawTickData 解析出 TradeTick。
        /// 包含時間、盤別與內外盤判定。
        /// </summary>
        /// <param name="raw">即時行情結構</param>
        /// <param name="activeSession">指定的盤別</param>
        /// <returns>解析成功的 TradeTick，若無效則回傳 null</returns>
        public static TradeTick? ParseRawTick(RawTickData raw, string activeSession, ref int txfLastQty, ref int mxfLastQty, Func<string, int> parseFirstPrice)
        {
            string symbol = raw.Symbol;
            string baseSym = symbol.StartsWith("TXF") ? "TXF" : (symbol.StartsWith("MXF") ? "MXF" : "");
            if (string.IsNullOrEmpty(baseSym)) return null;

            int currentQty = int.Parse(raw.TolMatchQty);
            int tickQty = 1;
            if (baseSym == "TXF")
            {
                if (currentQty <= txfLastQty) return null;
                tickQty = txfLastQty > 0 ? (currentQty - txfLastQty) : 1;
                txfLastQty = currentQty;
            }
            else
            {
                if (currentQty <= mxfLastQty) return null;
                tickQty = mxfLastQty > 0 ? (currentQty - mxfLastQty) : 1;
                mxfLastQty = currentQty;
            }

            int price = (int)double.Parse(raw.MatchPri.AsSpan());
            int bestBp = parseFirstPrice(raw.BestBuyPri);
            int bestSp = parseFirstPrice(raw.BestSellPri);

            if (price <= 0 || bestBp <= 0 || bestSp <= 0) return null;

            TradeSide side = TradeSide.Unknown;
            if (price >= bestSp) side = TradeSide.Outer;
            else if (price <= bestBp) side = TradeSide.Inner;

            string mt = raw.MatchTime.Trim();
            if (mt.Length < 6) return null;

            double tValRaw = TimeParser.ParseTime(mt);

            if ((tValRaw >= 30600 && tValRaw < 31500) || (tValRaw >= 52200 && tValRaw < 54000))
                return null;

            string session = "";
            double tVal = tValRaw;

            if (tValRaw >= 30600 && tValRaw <= 49500)
            {
                session = "日盤";
            }
            else if (tValRaw >= 52200 || tValRaw <= 18000)
            {
                session = "夜盤";
                if (tValRaw <= 18000) tVal += 86400.0;
            }
            else
            {
                return null;
            }

            return new TradeTick(baseSym, mt, tVal, price, tickQty, side, bestBp, bestSp, session);
        }
    }
}
