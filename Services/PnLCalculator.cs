using System;
using System.Collections.Generic;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// 未沖銷部位結構，用於 FIFO 損益計算。
    /// </summary>
    public class OpenTrade
    {
        /// <summary>
        /// 商品代碼
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// 買賣別："B" 代表買進，"S" 代表賣出
        /// </summary>
        public string BuySell { get; set; } = string.Empty;

        /// <summary>
        /// 成交價格
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// 剩餘未沖銷口數
        /// </summary>
        public int RemainingQty { get; set; }

        /// <summary>
        /// 交易時間
        /// </summary>
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// 今日平倉損益計算器，採用 FIFO (先進先出) 沖銷算法。
    /// </summary>
    public class PnLCalculator
    {
        private readonly Dictionary<string, Queue<OpenTrade>> _buyQueues = new();
        private readonly Dictionary<string, Queue<OpenTrade>> _sellQueues = new();
        private double _totalPnL;

        /// <summary>
        /// 累計已平倉總損益 (新台幣)
        /// </summary>
        public double TotalPnL
        {
            get => _totalPnL;
            set => _totalPnL = value;
        }

        /// <summary>
        /// 新增一筆成交明細，執行 FIFO 沖銷並更新累計損益。
        /// </summary>
        /// <param name="symbol">商品代碼 (例如 "TX00", "MTX00")</param>
        /// <param name="buySell">買賣方向 ("B" 或 "S", 或包含 "買"/"賣")</param>
        /// <param name="price">成交價格</param>
        /// <param name="qty">成交口數</param>
        public void AddExecution(string symbol, string buySell, double price, int qty)
        {
            if (string.IsNullOrEmpty(symbol) || qty <= 0) return;

            symbol = symbol.ToUpper().Trim();
            string direction = NormalizeDirection(buySell);

            if (direction == "B")
            {
                ProcessBuy(symbol, price, qty);
            }
            else if (direction == "S")
            {
                ProcessSell(symbol, price, qty);
            }
        }

        /// <summary>
        /// 重置計算器，清空所有部位與損益。
        /// </summary>
        public void Reset()
        {
            _buyQueues.Clear();
            _sellQueues.Clear();
            _totalPnL = 0;
        }

        private string NormalizeDirection(string input)
        {
            input = input.ToUpper().Trim();
            if (input.StartsWith("B") || input == "1" || input.Contains("買") || input.Contains("CB"))
            {
                return "B";
            }
            if (input.StartsWith("S") || input == "2" || input.Contains("賣") || input.Contains("CS"))
            {
                return "S";
            }
            return "B"; // 預設買
        }

        private void ProcessBuy(string symbol, double price, int qty)
        {
            double multiplier = GetMultiplier(symbol);

            // 若有賣單未平倉部位，先進行沖銷 (買入回補)
            if (_sellQueues.TryGetValue(symbol, out var sellQueue) && sellQueue.Count > 0)
            {
                while (qty > 0 && sellQueue.Count > 0)
                {
                    var match = sellQueue.Peek();
                    int closedQty = Math.Min(qty, match.RemainingQty);

                    // 賣出開倉，買入平倉：損益 = (賣出價 - 買入價) * 乘數 * 口數
                    double pointsDiff = match.Price - price;
                    _totalPnL += pointsDiff * multiplier * closedQty;

                    qty -= closedQty;
                    match.RemainingQty -= closedQty;

                    if (match.RemainingQty == 0)
                    {
                        sellQueue.Dequeue();
                    }
                }
            }

            // 若仍有剩餘的買入數量，則作為未平倉買單加入佇列
            if (qty > 0)
            {
                if (!_buyQueues.TryGetValue(symbol, out var buyQueue))
                {
                    buyQueue = new Queue<OpenTrade>();
                    _buyQueues[symbol] = buyQueue;
                }
                buyQueue.Enqueue(new OpenTrade
                {
                    Symbol = symbol,
                    BuySell = "B",
                    Price = price,
                    RemainingQty = qty,
                    Time = DateTime.Now
                });
            }
        }

        private void ProcessSell(string symbol, double price, int qty)
        {
            double multiplier = GetMultiplier(symbol);

            // 若有買單未平倉部位，先進行沖銷 (多單平倉)
            if (_buyQueues.TryGetValue(symbol, out var buyQueue) && buyQueue.Count > 0)
            {
                while (qty > 0 && buyQueue.Count > 0)
                {
                    var match = buyQueue.Peek();
                    int closedQty = Math.Min(qty, match.RemainingQty);

                    // 買入開倉，賣出平倉：損益 = (賣出價 - 買入價) * 乘數 * 口數
                    double pointsDiff = price - match.Price;
                    _totalPnL += pointsDiff * multiplier * closedQty;

                    qty -= closedQty;
                    match.RemainingQty -= closedQty;

                    if (match.RemainingQty == 0)
                    {
                        buyQueue.Dequeue();
                    }
                }
            }

            // 若仍有剩餘的賣出數量，則作為未平倉賣單加入佇列
            if (qty > 0)
            {
                if (!_sellQueues.TryGetValue(symbol, out var sellQueue))
                {
                    sellQueue = new Queue<OpenTrade>();
                    _sellQueues[symbol] = sellQueue;
                }
                sellQueue.Enqueue(new OpenTrade
                {
                    Symbol = symbol,
                    BuySell = "S",
                    Price = price,
                    RemainingQty = qty,
                    Time = DateTime.Now
                });
            }
        }

        private double GetMultiplier(string symbol)
        {
            // 大臺 (TX) 為 200 元/點，小臺 (MTX) 為 50 元/點
            if (symbol.StartsWith("TX")) return 200.0;
            if (symbol.StartsWith("MTX")) return 50.0;
            if (symbol.StartsWith("TFO")) return 50.0;
            return 50.0; // 預設乘數
        }
    }
}
