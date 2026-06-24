using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ExtremeSignalAppCS.Models;
using ExtremeSignalAppCS.Services;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Run = System.Windows.Documents.Run;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// UnbrokenKMonitor.xaml 的互動邏輯。
    /// </summary>
    public partial class UnbrokenKMonitor : System.Windows.Controls.UserControl
    {
        private TradingEngine? _engine;
        private MainWindow? _parentApp;
        
        private readonly object _lock = new();

        // 快取當前未破停損價格 (Key: (Type[high/low], PriceStr) -> Value: 訊號數量)
        private Dictionary<(string Type, string Price), int> _currentUnbrokenMap = new();
        private Dictionary<(string Type, string Price), string> _currentUnbrokenTimeMap = new();
        private Dictionary<(string Type, string Price), double> _brokenTimestamps = new();
        
        private class TrendEvent
        {
            public int Direction { get; set; }
            public int LongCount { get; set; }
            public int ShortCount { get; set; }
            public string EstablishedTime { get; set; } = "";
            public int? EstablishedPrice { get; set; }
            public string Reason { get; set; } = "";
        }
        
        // 趨勢方向表單狀態: 1 = 多方, -1 = 空方, 0 = 無資料
        private int _trendDirection = 0;
        private readonly List<TrendEvent> _trendHistory = new();
        
        // 記錄目前在趨勢表單中被選取反白的時間點
        private string? _selectedTrendTime = null;

        public UnbrokenKMonitor()
        {
            InitializeComponent();
        }

        public void Initialize(TradingEngine engine, MainWindow parentApp)
        {
            _engine = engine;
            _parentApp = parentApp;
        }

        public void UpdateFromSharedData(Dictionary<int, List<SimulationResult>> resultsMap, string currentPrice, string tradeTimeStr)
        {
            if (_engine == null || _parentApp == null) return;

            var tempUnbrokenMap = new Dictionary<(string Type, string Price), int>();
            var tempTimeMap = new Dictionary<(string Type, string Price), string>();
            var timeline = new List<(double TVal, string TimeStr, string Type, int Delta, int? Price, string Reason)>();

            // 我們已經完全棄用分K機制，所以直接取第一組結果作為唯一來源，避免重複統計
            var uniqueResults = resultsMap.Values.FirstOrDefault() ?? new List<SimulationResult>();

            foreach (var item in uniqueResults)
            {
                if (item.Tags.Contains("history") || item.Tags.Contains("annotation"))
                    continue;

                    string sigLabel = item.DisplayTitle;
                    string stopLossVal = item.StopLossDisplay;
                    
                    string? typeObj = null;
                    if (item.Type == "做多") typeObj = "做多";
                    else if (item.Type == "做空") typeObj = "做空";
                    
                    if (typeObj != null)
                    {
                        double trigTVal = ParseTimeStr(item.TrigTime);
                        double? breakTVal = null;
                        string breakTimeStr = "";
                        if (item.IsBroken && !string.IsNullOrEmpty(item.BreakTime))
                        {
                            breakTVal = ParseTimeStr(item.BreakTime);
                            breakTimeStr = item.BreakTime;
                        }
                        
                        int? tp = int.TryParse(item.TrigPrice, out int tpVal) ? tpVal : (int?)null;
                        
                        if (trigTVal < 999999)
                        {
                            timeline.Add((trigTVal, item.TrigTime, typeObj, 1, tp, "極值訊號"));
                        }
                        
                        if (breakTVal.HasValue && breakTVal.Value < 999999)
                        {
                            timeline.Add((breakTVal.Value, breakTimeStr, typeObj, -1, item.StopLossPrice, "即時破位"));
                        }
                    }

                    if (!string.IsNullOrEmpty(stopLossVal) && stopLossVal != "N/A" && !stopLossVal.Contains("已破"))
                    {
                        string? type = null;
                        if (item.Type == "做多") type = "high";
                        else if (item.Type == "做空") type = "low";

                        if (type != null)
                        {
                            bool isInstantlyBroken = false;
                            double sl = 0;
                            var key = (type, stopLossVal);
                            
                            if (double.TryParse(currentPrice, out double cp) && double.TryParse(stopLossVal, out sl))
                            {
                                if (type == "low" && cp > sl) isInstantlyBroken = true;
                                if (type == "high" && cp < sl) isInstantlyBroken = true;
                            }

                            if (!isInstantlyBroken && _brokenTimestamps.TryGetValue(key, out double brokenTime))
                            {
                                double itemTrigTime = ParseTimeStr(item.TrigTime);
                                if (itemTrigTime <= brokenTime)
                                {
                                    isInstantlyBroken = true;
                                }
                            }

                            if (isInstantlyBroken)
                            {
                                // 引擎尚未判定破位，但目前報價已破(或曾經已破)，提早注入破位事件以維持趨勢統計正確
                                double bt = ParseTimeStr(tradeTimeStr);
                                lock (_lock) { _brokenTimestamps[key] = bt; }
                                
                                if (bt < 999999)
                                {
                                    timeline.Add((bt, tradeTimeStr, typeObj ?? "", -1, (int)sl, "即時破位"));
                                }
                            }
                            else
                            {
                                string timeStr = item.BestATimeDisplay;
                                lock (_lock)
                                {
                                    if (!tempUnbrokenMap.ContainsKey(key))
                                    {
                                        tempUnbrokenMap[key] = 0;
                                    }
                                    tempUnbrokenMap[key]++;

                                    if (!tempTimeMap.ContainsKey(key) || string.Compare(timeStr, tempTimeMap[key]) > 0)
                                    {
                                        tempTimeMap[key] = timeStr;
                                    }
                                }
                        }
                    }
                }
            }

            var groupedTimeline = timeline.GroupBy(x => x.TVal).OrderBy(x => x.Key);
            
            int computedLongCount = 0;
            int computedShortCount = 0;
            int computedDir = 0;
            int lastAddedDir = 0;
            var computedHistory = new List<TrendEvent>();
            
            foreach (var group in groupedTimeline)
            {
                int preDir = computedDir;
                string lastReason = "";
                int? lastPrice = null;
                string timeStr = "";

                foreach (var ev in group)
                {
                    if (ev.Type == "做多") computedLongCount += ev.Delta;
                    if (ev.Type == "做空") computedShortCount += ev.Delta;
                    lastReason = ev.Reason;
                    lastPrice = ev.Price;
                    timeStr = ev.TimeStr;
                }
                
                int newDir = computedDir;
                if (computedLongCount > computedShortCount) newDir = 1;
                else if (computedShortCount > computedLongCount) newDir = -1;
                else if (computedLongCount == 0 && computedShortCount == 0) newDir = 0;
                
                if (newDir != computedDir)
                {
                    computedDir = newDir;
                    if (computedDir != 0 && computedDir != lastAddedDir)
                    {
                        lastAddedDir = computedDir;
                        computedHistory.Add(new TrendEvent
                        {
                            Direction = computedDir,
                            LongCount = computedLongCount,
                            ShortCount = computedShortCount,
                            EstablishedTime = FormatTimeStr(timeStr),
                            EstablishedPrice = lastPrice,
                            Reason = lastReason
                        });
                        if (computedHistory.Count > 1000) computedHistory.RemoveAt(0);
                    }
                }
            }

            // 更新內部狀態
            _currentUnbrokenMap = tempUnbrokenMap;
            _currentUnbrokenTimeMap = tempTimeMap;
            UpdateUI(currentPrice, tradeTimeStr, computedHistory, computedDir);
        }

        /// <summary>
        /// Tick 行情跳動時 O(1) 穿價破位即時剔除。
        /// </summary>
        public void CheckInstantUnbrokenBreakout(double price, string tradeTimeStr = "")
        {
            if (_currentUnbrokenMap.Count == 0) return;

            var brokenKeys = new List<(string Type, string Price)>();

            lock (_lock)
            {
                foreach (var kvp in _currentUnbrokenMap)
                {
                    var (sigType, stopLossVal) = kvp.Key;
                    if (double.TryParse(stopLossVal, out double slPrice))
                    {
                        if (sigType == "low" && price > slPrice)
                        {
                            brokenKeys.Add(kvp.Key);
                        }
                        else if (sigType == "high" && price < slPrice)
                        {
                            brokenKeys.Add(kvp.Key);
                        }
                    }
                }

                if (brokenKeys.Count > 0)
                {
                    double breakTime = ParseTimeStr(tradeTimeStr);
                    foreach (var k in brokenKeys)
                    {
                        _currentUnbrokenMap.Remove(k);
                        _brokenTimestamps[k] = breakTime;
                    }
                    UpdateUI(price.ToString(), tradeTimeStr);
                }
            }
        }

        private string FormatTimeStr(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 6) return t;
            if (t.Contains(':')) return t;
            return $"{t[..2]}:{t[2..4]}:{t[4..6]}";
        }

        private double ParseTimeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "N/A") return 999999;
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length < 6) return 999999;
            string hStr = digits.Substring(0, 2);
            string mStr = digits.Substring(2, 2);
            string sStr = digits.Substring(4, 2);
            if (int.TryParse(hStr, out int h) && int.TryParse(mStr, out int m) && int.TryParse(sStr, out int s))
            {
                double t = h * 3600 + m * 60 + s;
                if (t <= 18000) t += 86400;
                return t;
            }
            return 999999;
        }

        /// <summary>
        /// 主執行緒 UI 著色與排版更新。
        /// 智慧快取滾動條位置，防止更新文字時畫面抖動跳躍。
        /// </summary>
        private void UpdateUI(string currentPrice, string tradeTimeStr = "", List<TrendEvent>? newHistory = null, int? newDir = null)
        {
            // 1. 快取滾動條位置
            double vOffset = txtDisplay.VerticalOffset;
            double hOffset = txtDisplay.HorizontalOffset;

            // 趨勢歷史表單智慧捲動偵測
            double trendVOffset = txtTrendHistory.VerticalOffset;
            double trendHOffset = txtTrendHistory.HorizontalOffset;
            bool isTrendAtBottom = (txtTrendHistory.VerticalOffset + txtTrendHistory.ViewportHeight >= txtTrendHistory.ExtentHeight - 5.0) || txtTrendHistory.ExtentHeight < 1.0;

            int displayPrice = 0;
            if (double.TryParse(currentPrice, out double p))
            {
                displayPrice = (int)Math.Round(p);
            }

            string displayTimeStr = string.IsNullOrEmpty(tradeTimeStr) || tradeTimeStr == "N/A" 
                ? DateTime.Now.ToString("HH:mm:ss") 
                : FormatTimeStr(tradeTimeStr);

            lblTitle.Text = $"🛡️ 未破分 K 停損監控 | 目前時間：{displayTimeStr} | 價位: {(displayPrice > 0 ? displayPrice.ToString() : currentPrice)}";

            txtDisplay.Document.Blocks.Clear();

            var shortEntries = new List<(int count, string price, string timeStr)>();
            var longEntries = new List<(int count, string price, string timeStr)>();

            lock (_lock)
            {
                foreach (var kvp in _currentUnbrokenMap)
                {
                    var (sigType, priceVal) = kvp.Key;
                    string timeStr = _currentUnbrokenTimeMap.TryGetValue(kvp.Key, out var t) ? t : "";

                    if (sigType == "low")
                    {
                        shortEntries.Add((kvp.Value, priceVal, timeStr));
                    }
                    else if (sigType == "high")
                    {
                        longEntries.Add((kvp.Value, priceVal, timeStr));
                    }
                }
            }

            // 100% 遵守原版排序：停損價格從高到低 (由大到小) 排序
            shortEntries.Sort((x, y) =>
            {
                double.TryParse(x.price, out double px);
                double.TryParse(y.price, out double py);
                return py.CompareTo(px);
            });

            longEntries.Sort((x, y) =>
            {
                double.TryParse(x.price, out double px);
                double.TryParse(y.price, out double py);
                return py.CompareTo(px);
            });

            int totalShortIntervals = shortEntries.Sum(x => x.count);
            int totalLongIntervals = longEntries.Sum(x => x.count);

            int maxShortCount = shortEntries.Count > 0 ? shortEntries.Max(x => x.count) : -1;
            int maxLongCount = longEntries.Count > 0 ? longEntries.Max(x => x.count) : -1;

            // 更新統計顯示條
            lblSummaryShort.Text = $"做空共有 {totalShortIntervals} 項";
            lblSummaryLong.Text = $"做多共有 {totalLongIntervals} 項";

            // 增量填入 Paragraph，並且對多/空標題進行發光著色
            // 使用單一 Paragraph 且設定 Margin=0 以精準控制換行距離
            var pContent = new Paragraph { Margin = new Thickness(0) };

            void AppendUnbrokenEntry(Paragraph pContent, string price, string timeStr, int itemCount, bool isMaxCount)
            {
                var prefixLabel = new Run("  停損價: ");
                var priceVal = new Run(price);

                if (isMaxCount)
                {
                    priceVal.Foreground = Brushes.Yellow;
                    priceVal.FontWeight = FontWeights.Bold;

                    prefixLabel.Foreground = Brushes.Yellow;
                    prefixLabel.FontWeight = FontWeights.Bold;
                }

                pContent.Inlines.Add(prefixLabel);
                pContent.Inlines.Add(priceVal);
                pContent.Inlines.Add(new Run("  未破: "));
                var runCount = new Run($"{itemCount} 項")
                {
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 162, 237)), // #00A2ED
                    FontWeight = FontWeights.Bold
                };
                runCount.MouseEnter += (s, e) => runCount.TextDecorations = TextDecorations.Underline;
                runCount.MouseLeave += (s, e) => runCount.TextDecorations = null;
                runCount.PreviewMouseLeftButtonDown += (s, e) => 
                {
                    _parentApp?.FocusObserverOnStopLossPrice(price);
                    e.Handled = true;
                };
                pContent.Inlines.Add(runCount);
                pContent.Inlines.Add(new Run($"  ({timeStr})\n"));
            }

            if (shortEntries.Count > 0)
            {
                var run = new Run($"═══ 做空 共 {totalShortIntervals} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)), // 亮綠
                    FontWeight = FontWeights.Bold
                };
                pContent.Inlines.Add(run);

                foreach (var item in shortEntries)
                {
                    bool isMaxCount = item.count == maxShortCount;
                    AppendUnbrokenEntry(pContent, item.price, item.timeStr, item.count, isMaxCount);
                }
            }

            if (longEntries.Count > 0)
            {
                if (shortEntries.Count > 0)
                {
                    pContent.Inlines.Add(new Run("\n")); // 做空與做多之間空一行
                }
                var run = new Run($"═══ 做多 共 {totalLongIntervals} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75)), // 亮紅
                    FontWeight = FontWeights.Bold
                };
                pContent.Inlines.Add(run);

                foreach (var item in longEntries)
                {
                    bool isMaxCount = item.count == maxLongCount;
                    AppendUnbrokenEntry(pContent, item.price, item.timeStr, item.count, isMaxCount);
                }
            }

            if (shortEntries.Count == 0 && longEntries.Count == 0)
            {
                pContent.Inlines.Add(new Run("所有分 K 的停損價均已顯示「已破」或目前無觀察訊號。") { Foreground = Brushes.Gray });
            }

            txtDisplay.Document.Blocks.Add(pContent);

            // --- 趨勢方向表單邏輯 ---
            if (newHistory != null && newDir != null)
            {
                // 防止舊的背景計算快照覆蓋了 UI 執行緒的樂觀更新
                bool isStale = false;
                if (_trendHistory.Count > 0 && newHistory.Count > 0)
                {
                    double currentLatest = ParseTimeStr(_trendHistory[^1].EstablishedTime);
                    double incomingLatest = ParseTimeStr(newHistory[^1].EstablishedTime);
                    if (incomingLatest < currentLatest)
                    {
                        isStale = true;
                    }
                }

                if (!isStale)
                {
                    int oldDir = _trendDirection;
                    _trendDirection = newDir.Value;

                    bool hasNewTrendEvent = false;
                    TrendEvent? latestNewEvent = null;
                    if (newHistory.Count > 0)
                    {
                        latestNewEvent = newHistory[^1];
                        if (_trendHistory.Count == 0)
                        {
                            hasNewTrendEvent = true;
                        }
                        else
                        {
                            var lastOldEvent = _trendHistory[^1];
                            if (lastOldEvent.EstablishedTime != latestNewEvent.EstablishedTime || 
                                lastOldEvent.Direction != latestNewEvent.Direction)
                            {
                                hasNewTrendEvent = true;
                            }
                        }
                    }

                    bool wasEmpty = _trendHistory.Count == 0;
                    _trendHistory.Clear();
                    _trendHistory.AddRange(newHistory);

                    if (hasNewTrendEvent && latestNewEvent != null)
                    {
                        var evt = latestNewEvent;
                        string dirStr = evt.Direction == 1 ? "多方 📈" : "空方 📉";
                        string op = evt.ShortCount > evt.LongCount ? ">" : (evt.ShortCount < evt.LongCount ? "<" : "=");
                        string statusStr = $"做空 {evt.ShortCount} 項 {op} 做多 {evt.LongCount} 項";
                        string cpStr = evt.EstablishedPrice.HasValue ? evt.EstablishedPrice.Value.ToString() : "--";
                        string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                        string msgTitle = wasEmpty ? "【趨勢確立】" : "【趨勢轉向】";
                        string msg = $"{msgTitle}{dirStr}\n" +
                                     $"時間：{evt.EstablishedTime}{reasonStr}\n" +
                                     $"觸發價位：{cpStr}\n" +
                                     $"未破狀態：{statusStr}";
                        
                        _parentApp?.PushTelegramMessage(msg);
                    }
                }
            }
            else
            {
                int shortCount = totalShortIntervals; // 做空
                int longCount = totalLongIntervals;   // 做多
    
                int newDirection = _trendDirection;
                if (longCount > shortCount)
                {
                    newDirection = 1;
                }
                else if (shortCount > longCount)
                {
                    newDirection = -1;
                }
                else if (longCount == 0 && shortCount == 0)
                {
                    newDirection = 0;
                }
    
                if (newDirection != _trendDirection)
                {
                    int oldDir = _trendDirection;
                    _trendDirection = newDirection;
                    if (_trendDirection != 0)
                    {
                        int lastAddedDir = _trendHistory.Count > 0 ? _trendHistory[^1].Direction : 0;
                        if (_trendDirection != lastAddedDir)
                        {
                            int? currentP = int.TryParse(currentPrice, out int cp) ? cp : (int?)null;
                            var evt = new TrendEvent 
                            { 
                                Direction = _trendDirection, 
                                EstablishedTime = FormatTimeStr(tradeTimeStr),
                                EstablishedPrice = currentP,
                                LongCount = longCount,
                                ShortCount = shortCount,
                                Reason = "即時破位"
                            };
                            _trendHistory.Add(evt);
                            if (_trendHistory.Count > 1000) _trendHistory.RemoveAt(0);

                            // Trigger Telegram push
                            string dirStr = _trendDirection == 1 ? "多方 📈" : "空方 📉";
                            string op = shortCount > longCount ? ">" : (shortCount < longCount ? "<" : "=");
                            string statusStr = $"做空 {shortCount} 項 {op} 做多 {longCount} 項";
                            string cpStr = currentP.HasValue ? currentP.Value.ToString() : "--";
                            string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                            string msgTitle = lastAddedDir == 0 ? "【趨勢確立】" : "【趨勢轉向】";
                            string msg = $"{msgTitle}{dirStr}\n" +
                                         $"時間：{evt.EstablishedTime}{reasonStr}\n" +
                                         $"觸發價位：{cpStr}\n" +
                                         $"未破狀態：{statusStr}";
                            
                            _parentApp?.PushTelegramMessage(msg);
                        }
                    }
                }
            }

            // 渲染趨勢歷史 (改用增量渲染，避免破壞選取反白與滾動狀態)
            int existingCount = txtTrendHistory.Document.Blocks.Count;
            bool hasNoDataPara = existingCount == 1 && ((txtTrendHistory.Document.Blocks.FirstBlock as Paragraph)?.Inlines.FirstInline as Run)?.Text == "-- 無資料 --";

            if (_trendHistory.Count == 0)
            {
                if (!hasNoDataPara)
                {
                    txtTrendHistory.Document.Blocks.Clear();
                    txtTrendHistory.Document.Blocks.Add(new Paragraph(new Run("-- 無資料 --") { Foreground = Brushes.Gray }));
                }
            }
            else
            {
                if (hasNoDataPara)
                {
                    txtTrendHistory.Document.Blocks.Clear();
                    existingCount = 0;
                }

                bool hasNewTrend = false;

                Paragraph CreateTrendParagraph(TrendEvent evt, string signature, bool isSelected, int seqIndex)
                {
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, 4), Tag = signature };
                    para.Background = isSelected ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : Brushes.Transparent;
                    para.Cursor = System.Windows.Input.Cursors.Hand;

                    para.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        if (_selectedTrendTime == evt.EstablishedTime)
                        {
                            _selectedTrendTime = null;
                            para.Background = Brushes.Transparent;
                            _parentApp?.ClearChartCrosshair();
                        }
                        else
                        {
                            _selectedTrendTime = evt.EstablishedTime;
                            foreach (var block in txtTrendHistory.Document.Blocks)
                            {
                                if (block is Paragraph p) p.Background = Brushes.Transparent;
                            }
                            para.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                            _parentApp?.FocusChartOnTime(evt.EstablishedTime, evt.EstablishedPrice, evt.Direction);
                        }
                        e.Handled = true;
                    };

                    para.Inlines.Add(new Run($"{seqIndex} ") { Foreground = Brushes.White });

                    string op = evt.ShortCount > evt.LongCount ? ">" : (evt.ShortCount < evt.LongCount ? "<" : "=");
                    string pStr = evt.EstablishedPrice.HasValue ? $" {evt.EstablishedPrice.Value}" : "";
                    string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                    string contentStr = $"做空 {evt.ShortCount} 項 {op} 做多 {evt.LongCount} 項 {evt.EstablishedTime}{pStr}{reasonStr}";

                    if (evt.Direction == 1)
                    {
                        para.Inlines.Add(new Run(contentStr)
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75))
                        });
                    }
                    else if (evt.Direction == -1)
                    {
                        para.Inlines.Add(new Run(contentStr)
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69))
                        });
                    }
                    return para;
                }

                for (int i = 0; i < _trendHistory.Count; i++)
                {
                    var evt = _trendHistory[i];
                    string signature = $"{evt.EstablishedTime}_{evt.Direction}_{evt.LongCount}_{evt.ShortCount}_{evt.Reason}_{evt.EstablishedPrice}";
                    bool isSelected = _selectedTrendTime == evt.EstablishedTime;

                    if (i < existingCount)
                    {
                        var para = (Paragraph)txtTrendHistory.Document.Blocks.ElementAt(i);
                        string currentTag = para.Tag as string ?? "";
                        
                        if (currentTag != signature)
                        {
                            // 內容實質改變，取代舊行
                            var newPara = CreateTrendParagraph(evt, signature, isSelected, i + 1);
                            txtTrendHistory.Document.Blocks.InsertBefore(para, newPara);
                            txtTrendHistory.Document.Blocks.Remove(para);
                        }
                        else
                        {
                            // 內容未變，僅更新選取背景（若有改變）
                            var expectedBg = isSelected ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : Brushes.Transparent;
                            if (para.Background is SolidColorBrush solidBg && expectedBg is SolidColorBrush expectedSolid)
                            {
                                if (solidBg.Color != expectedSolid.Color) para.Background = expectedBg;
                            }
                            else
                            {
                                para.Background = expectedBg;
                            }
                            
                            // 更新序號 (如果序號改變的話)
                            if (para.Inlines.FirstInline is Run runSeq && runSeq.Text != $"{i + 1} ")
                            {
                                runSeq.Text = $"{i + 1} ";
                            }
                        }
                    }
                    else
                    {
                        // 新增的行
                        var newPara = CreateTrendParagraph(evt, signature, isSelected, i + 1);
                        txtTrendHistory.Document.Blocks.Add(newPara);
                        hasNewTrend = true;
                    }
                }

                // 移除多餘的行（如有）
                while (txtTrendHistory.Document.Blocks.Count > _trendHistory.Count)
                {
                    txtTrendHistory.Document.Blocks.Remove(txtTrendHistory.Document.Blocks.LastBlock);
                }

                // 如果有最新方向新增，強制標記為在底部，確保後續會捲動到最新的一列
                if (hasNewTrend)
                {
                    isTrendAtBottom = true;
                }
            }

            // 2. 還原滾動條位置
            txtDisplay.ScrollToVerticalOffset(vOffset);
            txtDisplay.ScrollToHorizontalOffset(hOffset);

            if (isTrendAtBottom)
            {
                txtTrendHistory.ScrollToEnd();
            }
            else
            {
                txtTrendHistory.ScrollToVerticalOffset(trendVOffset);
                txtTrendHistory.ScrollToHorizontalOffset(trendHOffset);
            }
        }

        /// <summary>
        /// 安全釋放資源（停止 Timer 並清空所有狀態）。
        /// 視窗關閉時呼叫。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
                _brokenTimestamps.Clear();
            }
            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
            lblSummaryShort.Text = "做空共有 0 項";
            lblSummaryLong.Text = "做多共有 0 項";
            
            _trendDirection = 0;
            _selectedTrendTime = null;
            _trendHistory.Clear();
            if (txtTrendHistory != null)
                txtTrendHistory.Document.Blocks.Clear();
        }

        /// <summary>
        /// 清空所有內部狀態與 UI 顯示，但保持 Timer 運行。
        /// 供「停止」按鈕呼叫，恢復到初始化剛完成的狀態。
        /// </summary>
        public void Clear()
        {

            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
                _brokenTimestamps.Clear();
            }

            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
            lblSummaryShort.Text = "做空共有 0 項";
            lblSummaryLong.Text = "做多共有 0 項";
            
            _trendDirection = 0;
            _selectedTrendTime = null;
            _trendHistory.Clear();
            if (txtTrendHistory != null)
                txtTrendHistory.Document.Blocks.Clear();
        }

        private void MenuCopyDisplay_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = new System.Windows.Documents.TextRange(txtDisplay.Document.ContentStart, txtDisplay.Document.ContentEnd).Text;
            System.Windows.Clipboard.SetText(text);
        }

        private void MenuCopyTrend_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = new System.Windows.Documents.TextRange(txtTrendHistory.Document.ContentStart, txtTrendHistory.Document.ContentEnd).Text;
            System.Windows.Clipboard.SetText(text);
        }
    }
}
