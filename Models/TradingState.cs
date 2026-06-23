using System;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 即時/回放行情之增量累計狀態模型。
    /// 封裝 Python 的 _init_rt_state，用於 O(1) 的統計指標與買賣速度計算，
    /// 確保高頻 Tick 接收時不需每次全量重算。
    /// </summary>
    public class TradingState
    {
        public int OuterCount { get; set; }
        public int InnerCount { get; set; }
        
        public double? FirstOuterTime { get; set; }
        public double? LastOuterTime { get; set; }
        
        public double? FirstInnerTime { get; set; }
        public double? LastInnerTime { get; set; }
        
        public long SumPrice { get; set; }
        public int Count { get; set; }
        
        public int DayMax { get; set; } = -999999;
        public int DayMin { get; set; } = 999999;
        
        public string MaxTime { get; set; } = "--";
        public string MinTime { get; set; } = "--";
        
        public int RunningMax { get; set; } = -999999;
        public int RunningMin { get; set; } = 999999;
        
        public int? LastPrice { get; set; }
        
        public double LastCheckTimeH { get; set; } = -999999.0;
        public double LastCheckTimeB { get; set; } = -999999.0;
        
        public int ScanIdx { get; set; }

        public TradingState()
        {
            Reset();
        }

        /// <summary>
        /// 重置所有狀態為初始狀態 (100% 參照 Python _init_rt_state)
        /// </summary>
        public void Reset()
        {
            OuterCount = 0;
            InnerCount = 0;
            FirstOuterTime = null;
            LastOuterTime = null;
            FirstInnerTime = null;
            LastInnerTime = null;
            SumPrice = 0;
            Count = 0;
            DayMax = -999999;
            DayMin = 999999;
            MaxTime = "--";
            MinTime = "--";
            RunningMax = -999999;
            RunningMin = 999999;
            LastPrice = null;
            LastCheckTimeH = -999999.0;
            LastCheckTimeB = -999999.0;
            ScanIdx = 0;
        }

        /// <summary>
        /// 深拷貝一份當前狀態快照 (供計算執行緒在背景使用，避免 UI / 行情線程競爭)
        /// </summary>
        public TradingState Clone()
        {
            return new TradingState
            {
                OuterCount = this.OuterCount,
                InnerCount = this.InnerCount,
                FirstOuterTime = this.FirstOuterTime,
                LastOuterTime = this.LastOuterTime,
                FirstInnerTime = this.FirstInnerTime,
                LastInnerTime = this.LastInnerTime,
                SumPrice = this.SumPrice,
                Count = this.Count,
                DayMax = this.DayMax,
                DayMin = this.DayMin,
                MaxTime = this.MaxTime,
                MinTime = this.MinTime,
                RunningMax = this.RunningMax,
                RunningMin = this.RunningMin,
                LastPrice = this.LastPrice,
                LastCheckTimeH = this.LastCheckTimeH,
                LastCheckTimeB = this.LastCheckTimeB,
                ScanIdx = this.ScanIdx
            };
        }
    }
}
