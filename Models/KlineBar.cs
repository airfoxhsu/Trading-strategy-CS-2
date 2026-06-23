using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// K 線棒資料模型。
    /// 用於聚合分 K 資料，供 WPF DataGrid 表格資料綁定及 KLineChartControl 原生硬體加速繪製。
    /// 實作 INotifyPropertyChanged 以支援 WPF DataGrid 差量更新，消滅暴力 Items.Refresh() 全量重繪。
    /// </summary>
    public class KlineBar : ObservableObject
    {

        private string _timeLabel = string.Empty;
        private double _high;
        private double _low;
        private double _open;
        private double _close;
        private string _signals = string.Empty;
        private string _breakHigh = string.Empty;
        private string _breakLow = string.Empty;
        private string _tag = "flat";
        private bool _isObsKLowHighlight;
        private bool _isObsKHighHighlight;

        /// <summary>
        /// 時間標籤 (如 "08:45~09:15")
        /// </summary>
        public string TimeLabel { get => _timeLabel; set => SetField(ref _timeLabel, value); }

        /// <summary>
        /// 最高價
        /// </summary>
        public double High { get => _high; set => SetField(ref _high, value); }

        /// <summary>
        /// 最低價
        /// </summary>
        public double Low { get => _low; set => SetField(ref _low, value); }

        /// <summary>
        /// 開盤價
        /// </summary>
        public double Open { get => _open; set => SetField(ref _open, value); }

        /// <summary>
        /// 收盤價
        /// </summary>
        public double Close { get => _close; set => SetField(ref _close, value); }

        /// <summary>
        /// 即時分析聚合所得的訊號標記字串
        /// </summary>
        public string Signals { get => _signals; set => SetField(ref _signals, value); }

        /// <summary>
        /// 突破上高文字 (如 "是"、"做多" 或 "")
        /// </summary>
        public string BreakHigh { get => _breakHigh; set => SetField(ref _breakHigh, value); }

        /// <summary>
        /// 跌破上低文字 (如 "是"、"做空" 或 "")
        /// </summary>
        public string BreakLow { get => _breakLow; set => SetField(ref _breakLow, value); }

        /// <summary>
        /// 漲跌標籤狀態 ("up" / "down" / "flat")，用以渲染紅/綠 K 棒與字體顏色
        /// </summary>
        public string Tag { get => _tag; set => SetField(ref _tag, value); }

        /// <summary>
        /// 是否為當前做空的觀察區間 (綠底白字)
        /// </summary>
        public bool IsObsKLowHighlight { get => _isObsKLowHighlight; set => SetField(ref _isObsKLowHighlight, value); }

        /// <summary>
        /// 是否為當前做多的觀察區間 (紅底白字)
        /// </summary>
        public bool IsObsKHighHighlight { get => _isObsKHighHighlight; set => SetField(ref _isObsKHighHighlight, value); }

        /// <summary>
        /// 建立預設 K棒。
        /// </summary>
        public KlineBar() { }

        /// <summary>
        /// 建立並初始化 K棒。
        /// </summary>
        public KlineBar(string timeLabel, double high, double low, double open, double close, string signals, string breakHigh, string breakLow, string tag)
        {
            _timeLabel = timeLabel;
            _high = high;
            _low = low;
            _open = open;
            _close = close;
            _signals = signals;
            _breakHigh = breakHigh;
            _breakLow = breakLow;
            _tag = tag;
        }

        /// <summary>
        /// 深拷貝一份 K棒。
        /// </summary>
        public KlineBar Clone()
        {
            return new KlineBar(TimeLabel, High, Low, Open, Close, Signals, BreakHigh, BreakLow, Tag)
            {
                IsObsKLowHighlight = this.IsObsKLowHighlight,
                IsObsKHighHighlight = this.IsObsKHighHighlight
            };
        }
    }
}
