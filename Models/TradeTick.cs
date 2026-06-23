using System;
using ExtremeSignalAppCS.Helper;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 唯讀高性能成交 Tick 資料結構。
    /// 宣告為 struct 以在 high-frequency 行情（每秒 10,000+ Ticks）下
    /// 完全在 Stack 上運行，杜絕 GC Heap 分配與垃圾回收暫停。
    /// 引進 Flyweight Pattern 字串快取與 Enum/Byte 替代 Reference Type。
    /// </summary>
    public readonly struct TradeTick
    {
        /// <summary>
        /// 商品代碼 ID (0=TXF, 1=MXF)
        /// </summary>
        public byte SymbolId { get; }

        /// <summary>
        /// 交易盤別 ID (0=日盤, 1=夜盤)
        /// </summary>
        public byte SessionId { get; }

        /// <summary>
        /// 當日累計秒數 (高精精確至微秒)
        /// </summary>
        public double TimeVal { get; }

        /// <summary>
        /// 成交價位
        /// </summary>
        public int Price { get; }

        /// <summary>
        /// 買賣雙邊主動方向 (Outer/Inner/Unknown)
        /// </summary>
        public TradeSide Side { get; }

        /// <summary>
        /// 當時最佳買價 (Best Bid Price)
        /// </summary>
        public int BestBp { get; }

        /// <summary>
        /// 當時最佳賣價 (Best Ask Price)
        /// </summary>
        public int BestSp { get; }

        /// <summary>
        /// 當筆成交口數 (Volume)
        /// </summary>
        public int Qty { get; }

        // === 零分配唯讀屬性封裝 ===

        /// <summary>
        /// 取得商品名稱 (字串實例已 interned)
        /// </summary>
        public string Symbol => SymbolId == 1 ? "MXF" : "TXF";

        /// <summary>
        /// 取得盤別名稱 (字串實例已 interned)
        /// </summary>
        public string Session => SessionId == 1 ? "夜盤" : "日盤";

        /// <summary>
        /// 取得行情時間字串，由快取池提供，避免字串分配 (0 Alloc)
        /// </summary>
        public string Time => TimeStringCache.GetTimeStr((int)TimeVal);

        /// <summary>
        /// 高效能 Zero Allocation 建構子
        /// </summary>
        public TradeTick(byte symbolId, byte sessionId, double timeVal, int price, int qty, TradeSide side, int bestBp = 0, int bestSp = 0)
        {
            SymbolId = symbolId;
            SessionId = sessionId;
            TimeVal = timeVal;
            Price = price;
            Qty = qty;
            Side = side;
            BestBp = bestBp;
            BestSp = bestSp;
        }

        /// <summary>
        /// 向下相容建構子，於轉換過程中使用。
        /// </summary>
        public TradeTick(string symbol, string time, double timeVal, int price, int qty, TradeSide side, int bestBp = 0, int bestSp = 0, string session = "")
        {
            SymbolId = (byte)(symbol == "MXF" ? 1 : 0);
            SessionId = (byte)(session == "夜盤" ? 1 : 0);
            TimeVal = timeVal;
            Price = price;
            Qty = qty;
            Side = side;
            BestBp = bestBp;
            BestSp = bestSp;
        }
    }
}
