using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 停損回測狀態機之單筆觀測結果。
    /// 用於「極值觀測表 DataGrid」的直接資料繫結，並快取歷史狀態以實現增量更新與點選連動。
    /// 實作 INotifyPropertyChanged 以支援 WPF DataGrid 差量更新，消滅暴力 Items.Refresh() 全量重繪。
    /// </summary>
    public class SimulationResult : ObservableObject
    {
        // 唯讀快取 Key，用於去重 (Price, ATime, ObsN)
        public (int Price, string ATime, int ObsN) ConfirmedKey => (BestAPrice, BestATime, ObsN);

        // --- 私有欄位 ---
        private string _displayTitle = string.Empty;
        private string _bestATime = string.Empty;
        private int _bestAPrice;
        private string _trigTime = "N/A";
        private string _trigPrice = "N/A";
        private string _pre = "N/A";
        private string _post = "N/A";
        private string _stopLossDisplay = "N/A";
        private string _type = string.Empty;
        private int _obsEntry;
        private int _prevHigh;
        private int _prevLow;
        private int _bIndex;
        private int _obsN;
        private bool _isBroken;
        private string? _breakTime;
        private int _stopLossPrice;
        private int _ampVal;
        private bool _isTargetPriceHighlighted;
        private bool _isChecked;
        private bool _isCheckable = true;
        private string? _orderNo;
        private string? _orderedSymbol;

        /// <summary>
        /// 顯示標籤 (如 "N=25 抓新高反轉")
        /// </summary>
        public string DisplayTitle { get => _displayTitle; set => SetField(ref _displayTitle, value); }

        /// <summary>
        /// A 點極值發生的時間
        /// </summary>
        public string BestATime { get => _bestATime; set => SetField(ref _bestATime, value); }

        /// <summary>
        /// A 點極端價位
        /// </summary>
        public int BestAPrice { get => _bestAPrice; set => SetField(ref _bestAPrice, value); }

        /// <summary>
        /// B 點訊號確立觸發時間
        /// </summary>
        public string TrigTime { get => _trigTime; set => SetField(ref _trigTime, value); }

        public string BestATimeDisplay => FormatTimeStr(BestATime);
        public string TrigTimeDisplay => FormatTimeStr(TrigTime);

        private string FormatTimeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "N/A" || raw.Contains(":")) return raw;
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length >= 6)
                return $"{digits.Substring(0, 2)}:{digits.Substring(2, 2)}:{digits.Substring(4, 2)}";
            return raw;
        }

        /// <summary>
        /// B 點訊號確立觸發價位 (即進場價)
        /// </summary>
        public string TrigPrice { get => _trigPrice; set => SetField(ref _trigPrice, value); }

        /// <summary>
        /// A 點前向同盤 N 筆的平均間隔秒數 (顯示文字如 "0.2345s")
        /// </summary>
        public string Pre { get => _pre; set => SetField(ref _pre, value); }

        /// <summary>
        /// A 點後向同盤 N 筆的平均間隔秒數 (顯示文字如 "0.1234s")
        /// </summary>
        public string Post { get => _post; set => SetField(ref _post, value); }

        /// <summary>
        /// 停損價位的顯示字串 (如 "18400" 或 "18400(已破)")
        /// </summary>
        public string StopLossDisplay { get => _stopLossDisplay; set => SetField(ref _stopLossDisplay, value); }

        /// <summary>
        /// 渲染樣式標籤 (如 "obs_high"、"obs_low"、"history"、"annotation")
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        // --- WPF UI 繫結樣式屬性 ---
        public string ForegroundColor => Tags.Contains("up") || Tags.Contains("obs_low") ? "#EB4B4B" : 
                                         (Tags.Contains("down") || Tags.Contains("obs_high") ? "#28A745" : 
                                         (Tags.Contains("annotation") ? "#808080" : "#DCDCDC"));

        public string BackgroundColor => Tags.Contains("obs_k_low_highlight") ? "#1928A745" : // 10% 亮綠透明
                                         (Tags.Contains("obs_k_high_highlight") ? "#19EB4B4B" : // 10% 亮紅透明
                                         "Transparent");

        public string FontWeightVal => Tags.Contains("obs_high") || Tags.Contains("obs_low") || Tags.Contains("up") || Tags.Contains("down") ? "Bold" : "Normal";

        public void UpdateTags(IEnumerable<string> newTags)
        {
            var newList = newTags.ToList();
            bool isChanged = Tags.Count != newList.Count || !Tags.SequenceEqual(newList);

            if (isChanged)
            {
                Tags.Clear();
                Tags.AddRange(newList);
                OnPropertyChanged(nameof(ForegroundColor));
                OnPropertyChanged(nameof(BackgroundColor));
                OnPropertyChanged(nameof(FontWeightVal));
            }
        }

        // --- 核心狀態追蹤屬性 (不直接綁定 DataGrid，但用於引擎計算與破位檢測) ---

        /// <summary>
        /// 訊號類型 ("做多" 或 "做空")
        /// </summary>
        public string Type { get => _type; set => SetField(ref _type, value); }

        /// <summary>
        /// 觀察關卡價 (做空對應時段最高；做多對應時段最低)
        /// </summary>
        public int ObsEntry { get => _obsEntry; set => SetField(ref _obsEntry, value); }

        /// <summary>
        /// 前一根已收盤 K 棒的最高點 (做空停損防守)
        /// </summary>
        public int PrevHigh { get => _prevHigh; set => SetField(ref _prevHigh, value); }

        /// <summary>
        /// 前一根已收盤 K 棒的最低點 (做多停損防守)
        /// </summary>
        public int PrevLow { get => _prevLow; set => SetField(ref _prevLow, value); }

        /// <summary>
        /// B 點確立時的 Tick 索引 (用於時間軸已破檢測)
        /// </summary>
        public int BIndex { get => _bIndex; set => SetField(ref _bIndex, value); }

        /// <summary>
        /// 觀察 N 筆值
        /// </summary>
        public int ObsN { get => _obsN; set => SetField(ref _obsN, value); }

        /// <summary>
        /// 此停損點目前是否已在歷史上被突破跌破而失效
        /// </summary>
        public bool IsBroken { get => _isBroken; set => SetField(ref _isBroken, value); }

        /// <summary>
        /// 停損被觸發的精確時間 (若未被破則為 null)
        /// </summary>
        public string? BreakTime { get => _breakTime; set => SetField(ref _breakTime, value); }

        /// <summary>
        /// 原始防守停損價
        /// </summary>
        public int StopLossPrice { get => _stopLossPrice; set => SetField(ref _stopLossPrice, value); }

        /// <summary>
        /// 極值點振幅
        /// </summary>
        public int AmpVal { get => _ampVal; set => SetField(ref _ampVal, value); }

        /// <summary>
        /// 特殊反白標記 (A點價)
        /// </summary>
        public bool IsTargetPriceHighlighted { get => _isTargetPriceHighlighted; set => SetField(ref _isTargetPriceHighlighted, value); }

        /// <summary>
        /// 記錄使用者是否勾選此列。
        /// </summary>
        public bool IsChecked { get => _isChecked; set => SetField(ref _isChecked, value); }

        /// <summary>
        /// 控制 CheckBox 是否可用。當被設為 false 時，若原本已勾選，則會自動取消勾選。
        /// </summary>
        public bool IsCheckable
        {
            get => _isCheckable;
            set
            {
                if (SetField(ref _isCheckable, value))
                {
                    if (!value && IsChecked)
                    {
                        IsChecked = false;
                    }
                }
            }
        }

        /// <summary>
        /// 記錄此列下單成功的委託書號 (OrderNo)。若未成功或未下單則為 null。
        /// </summary>
        public string? OrderNo { get => _orderNo; set => SetField(ref _orderNo, value); }

        /// <summary>
        /// 記錄此列下單時的完整商品代碼 (例如 TXFF6 / MXFF6)。
        /// </summary>
        public string? OrderedSymbol { get => _orderedSymbol; set => SetField(ref _orderedSymbol, value); }

        /// <summary>
        /// 建立空的 SimulationResult。
        /// </summary>
        public SimulationResult() { }

        /// <summary>
        /// 將目前狀態打包為 DataGrid 顯示所需的字串陣列 (對照 Python row tuple)。
        /// </summary>
        public string[] ToRowArray()
        {
            return new string[]
            {
                DisplayTitle,
                BestATimeDisplay,
                BestAPrice.ToString(),
                TrigTimeDisplay,
                TrigPrice,
                Pre,
                Post,
                StopLossDisplay
            };
        }
    }
}
