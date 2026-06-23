using System;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 從 COM 事件接收的原始 Tick 字串封裝，用於丟入 Channel 以便在背景執行緒進行無鎖解碼與狀態推進，達成 Zero UI Blocking。
    /// </summary>
    public readonly struct RawTickData
    {
        public string Symbol { get; }
        public string MatchTime { get; }
        public string MatchPri { get; }
        public string TolMatchQty { get; }
        public string BestBuyPri { get; }
        public string BestSellPri { get; }

        public RawTickData(string symbol, string matchTime, string matchPri, string tolMatchQty, string bestBuyPri, string bestSellPri)
        {
            Symbol = symbol;
            MatchTime = matchTime;
            MatchPri = matchPri;
            TolMatchQty = tolMatchQty;
            BestBuyPri = bestBuyPri;
            BestSellPri = bestSellPri;
        }
    }
}
