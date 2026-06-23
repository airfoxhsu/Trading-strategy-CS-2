using System;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 行情成交買賣雙邊枚舉。
    /// </summary>
    public enum TradeSide
    {
        /// <summary>
        /// 外盤 (主動買)
        /// </summary>
        Outer,

        /// <summary>
        /// 內盤 (主動賣)
        /// </summary>
        Inner,

        /// <summary>
        /// 未知或非雙邊
        /// </summary>
        Unknown
    }
}
