using System;
using System.Collections.Generic;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// 動態 N 值計算器 (基於近期 Tick 頻率校準)
    /// </summary>
    public class DynamicNCalculator
    {
        private readonly double _windowSeconds;
        private readonly double _targetObservationSeconds;
        private readonly int _minN;
        private readonly int _maxN;
        private readonly Queue<double> _recentTickTimes;

        public DynamicNCalculator(
            double windowSeconds = 60.0, 
            double targetObservationSeconds = 3.0, 
            int minN = 10, 
            int maxN = 150)
        {
            _windowSeconds = windowSeconds;
            _targetObservationSeconds = targetObservationSeconds;
            _minN = minN;
            _maxN = maxN;
            _recentTickTimes = new Queue<double>(2000); // 預分配記憶體，降低 GC
        }

        /// <summary>
        /// 每次收到新 Tick 時呼叫此方法，更新滑動窗口並回傳最新的動態 N 值
        /// </summary>
        /// <param name="currentTimeVal">當前 Tick 的時間</param>
        /// <returns>動態計算出的 N 值</returns>
        public int UpdateAndGetDynamicN(double currentTimeVal)
        {
            // 將最新 Tick 時間加入窗口
            _recentTickTimes.Enqueue(currentTimeVal);

            // 剔除過期資料：移除早於 (當前時間 - 窗口大小) 的 Tick
            double cutoffTime = currentTimeVal - _windowSeconds;
            while (_recentTickTimes.Count > 0 && _recentTickTimes.Peek() < cutoffTime)
            {
                _recentTickTimes.Dequeue();
            }

            // 計算 TPS (Ticks Per Second)
            // 防呆處理：如果窗口跨度極小，視為 1 秒避免除以零
            double actualWindowSpan = currentTimeVal - _recentTickTimes.Peek();
            if (actualWindowSpan <= 1.0) actualWindowSpan = 1.0;

            double tps = _recentTickTimes.Count / actualWindowSpan;

            // 計算動態 N 值：TPS * 期望觀察秒數
            int rawDynamicN = (int)Math.Round(tps * _targetObservationSeconds);

            // 套用邊界保護
            return Math.Clamp(rawDynamicN, _minN, _maxN);
        }
    }
}
