using System;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExtremeSignalAppCS.Helper;
using ExtremeSignalAppCS.Models;
using ExtremeSignalAppCS.Services;
using ExtremeSignalAppCS.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace ExtremeSignalAppCS
{
    /// <summary>
    /// 專業交易系統主介面控制中心。
    /// 負責雙軌行情資料流調度、COM 行情連接點事件監聽、背景非同步計算 Debounce 與頂級 UI 著色更新。
    /// </summary>
    public partial class MainWindow : Window
    {
        // 核心運算與推播服務
        private readonly TradingEngine _engine;
        private readonly TelegramService _tgService;
        // 原生元大 COM 行情連線相關欄位
        private AxYuantaQuoteLib.AxYuantaQuote? _axHost;
        private YuantaQuoteWrapper? _yuantaQuote;
        private string[] _symbolsToRegister = [];

        // 原生元大 COM 下單/帳務連線相關欄位
        private YuantaOrdLib.YuantaOrdClass? _yuantaOrd;
        private string _currentBranch = string.Empty;
        private string _currentAccount = string.Empty;
        private readonly Services.PnLCalculator _pnlCalculator = new();
        private readonly object _logLock = new();
        private string _mktUser = string.Empty;
        private string _mktPwd = string.Empty;
        private bool _isOrdLoggedIn = false;
        private bool _isOrdLoggingIn = false;

        // 新增：即時部位與狀態
        private volatile int _currentPositionLots = 0;
        private double _currentPositionCost = 0;

        // 雙軌行情資料快取 (大臺/小臺, 日盤/夜盤)
        private readonly Dictionary<string, Dictionary<string, ConcurrentAppendOnlyList<TradeTick>>> _liveSymbolTrades;
        private readonly Dictionary<string, Dictionary<string, ConcurrentAppendOnlyList<TradeTick>>> _replaySymbolTrades;

        // O(1) 累計狀態 (實時與回放雙軌隔離)
        private readonly Dictionary<string, Dictionary<string, TradingState>> _rtState;
        private readonly Dictionary<string, Dictionary<string, TradingState>> _replayRtState;

        // 【新增】實時狀態機快取 (與 Python 的 _rt_triggers 等效)
        private readonly Dictionary<string, Dictionary<string, List<PendingTrigger>>> _rtTriggers;
        private readonly Dictionary<string, Dictionary<string, List<CompletedTrigger>>> _rtCompletedDetails;

        // 新增：自動交易全局開關與快取變數
        private volatile bool _isAutoTradeBuyEnabled = false;
        private volatile bool _isAutoTradeSellEnabled = false;
        private volatile bool _isTxfSelected = false; // 是否選中大臺
        private volatile bool _isBuyLocked = false;
        private volatile bool _isSellLocked = false;
        private volatile int _lastBuyStopLossPrice = 0;
        private volatile int _lastSellStopLossPrice = 0;
        private readonly HashSet<(int Price, string ATime, int ObsN)> _bgProcessedKeys = new();
        private readonly Dictionary<string, (int Price, string Type, string Symbol, string Status)> _unboundOrderReplies = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Price, string ATime, int ObsN), AutoTradeState> _autoTradeStates = new();

        private readonly System.Threading.Lock _rtLock = new(); // C# 13 新型執行緒同步安全鎖

        // 靜態編譯期 Regex 產生器
        [System.Text.RegularExpressions.GeneratedRegex(@"Symbol=([^, \t\r\n]+)")]
        private static partial System.Text.RegularExpressions.Regex SymbolRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"mattime=([^, \t\r\n]+)")]
        private static partial System.Text.RegularExpressions.Regex MatTimeRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"matpri=([-]?\d+)")]
        private static partial System.Text.RegularExpressions.Regex MatPriRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"tmatqty=([-]?\d+)")]
        private static partial System.Text.RegularExpressions.Regex TMatQtyRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"bestbp=([\d,]*)")]
        private static partial System.Text.RegularExpressions.Regex BestBpRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"bestsp=([\d,]*)")]
        private static partial System.Text.RegularExpressions.Regex BestSpRegex();

        // 實時行情背景 Debounce 計算執行緒相關
        private readonly AutoResetEvent _analysisEvent = new(false);
        private CancellationTokenSource? _analysisCts;
        private Task? _analysisTask;
        
        // --- 新增：Market Data Channel 與 Background Worker ---
        private readonly Channel<RawTickData> _tickChannel = Channel.CreateUnbounded<RawTickData>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private Task? _marketDataTask;
        private CancellationTokenSource? _marketDataCts;
        private volatile int _uiGeneration = 0; // UI 世代計數器，防止過期 Dispatcher 任務覆蓋已清空的 UI
        private DispatcherTimer? _sessionTimer; // 盤別自動切換計時器

        // 復盤回放背景執行緒相關
        private CancellationTokenSource? _replayCts;
        private Task? _replayTask;
        private List<TradeTick> _allParsedTicks = [];
        private bool _isReplaying;
        private bool _isPreloading; // 預載今日歷史 Tick 標記，在此期間暫停 Tick 的背景計算
        private bool _isRecovering; // 標記網路中斷重新連線後的歷史行情回補，避免重發推播
        private bool _wasPlayingBeforeDrag;
        private bool _skipNextPreload; // 標記自動換盤後跳過預載

        // 快取與狀態變數
        private readonly HashSet<(string Symbol, string Session, string Type, int Price, string ATime)> _rtNotifiedKeys = [];
        private readonly Dictionary<string, double?> _rtLastNetSpeedsTop = new() { { "TXF", null }, { "MXF", null } };
        private readonly Dictionary<string, double?> _rtLastNetSpeedsBot = new() { { "TXF", null }, { "MXF", null } };
        
        // 無鎖即時行情過濾與防呆狀態 (Lock-Free Fallbacks)
        private volatile int _txfLastMatchQty = -1;
        private volatile int _mxfLastMatchQty = -1;
        
        // 智慧操作感知型降頻快取 (Interaction-Aware Throttle)
        private string _lastRenderedContent = string.Empty;
        private double _lastTxtRenderTime;
        private int _replayThrottleCounter;

        // 即時渲染用 (Render Loop) 無鎖快取變數
        private volatile int _renderTxfPrice;
        private volatile int _renderMxfPrice;
        private volatile int _renderTxfBestBp;
        private volatile int _renderTxfBestSp;
        private volatile int _renderMxfBestBp;
        private volatile int _renderMxfBestSp;
        private string _renderTxfTime = string.Empty;
        private string _renderMxfTime = string.Empty;
        
        private int _lastRenderedMxfPrice = 0;
        private string _lastRenderedMxfTime = "";
        private int _lastRenderedTxfPrice = 0;
        private string _lastRenderedTxfTime = "";

        // 參數與狀態
        private string _currentSessionName = "日盤";
        private int _currentRealtimePort = 443;
        private int _currentTargetDays = 60;
        private int _currentKlineInterval = 1;
        private int _currentObsN = 25;
        private int? _lastMxfPrice;
        private int? _lastTxfPrice;
        private string _lastMxfTime = "";
        private string? _currentReplayDir;
        private string? _lastSelectedReplayDir;
        
        // 用於「未破分K」點選後，跨執行緒狀態傳遞以便在更新完成後反白特定停損價位
        private string? _targetHighlightStopLossPrice;
        private bool _isProgrammaticSelection;
        private bool _isNavigatingToHighlight;
        private bool _isRestoringSelection;
        private string _currentReplaySession = "日盤";
        private string? _lastAutofillKlineTime;
        private List<(string Symbol, string Session, int DayMin, int DayMax, List<SimulationResult> Details)> _lastRtStatusSnapshot = [];
        private bool _isRealtimeUIEnabled = true;
        private readonly bool _isInitialized = false; // WPF 視窗初始化安全防護標記

        // UI 表格資料繫結 Observable 容器
        private readonly ObservableCollection<KlineBar> _klineCollection = [];
        private readonly ObservableCollection<SimulationResult> _obsCollection = [];
        private readonly ObservableCollection<IntervalStat> _intervalStatsCollection = new();
        private readonly int[] _allIntervals = { 1, 2, 3, 4, 5, 10, 15, 30, 60 };

        // 歷史狀態機回測降頻快取
        private double _lastHeavyCalcTimeMs = 0;
        private List<KlineBar> _lastKlineData = [];
        private List<SimulationResult> _lastSimulationResults = [];
        private Dictionary<int, List<SimulationResult>>? _lastSharedResultsMap = null;

        public MainWindow()
        {
            _engine = new TradingEngine();
            _tgService = new TelegramService();

            // 1. 初始化資料結構
            _liveSymbolTrades = new Dictionary<string, Dictionary<string, ConcurrentAppendOnlyList<TradeTick>>>
            {
                { "TXF", new Dictionary<string, ConcurrentAppendOnlyList<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } },
                { "MXF", new Dictionary<string, ConcurrentAppendOnlyList<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } }
            };

            _replaySymbolTrades = new Dictionary<string, Dictionary<string, ConcurrentAppendOnlyList<TradeTick>>>
            {
                { "TXF", new Dictionary<string, ConcurrentAppendOnlyList<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } },
                { "MXF", new Dictionary<string, ConcurrentAppendOnlyList<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } }
            };

            _rtState = new Dictionary<string, Dictionary<string, TradingState>>
            {
                { "TXF", new Dictionary<string, TradingState> { { "日盤", new() }, { "夜盤", new() } } },
                { "MXF", new Dictionary<string, TradingState> { { "日盤", new() }, { "夜盤", new() } } }
            };

            _replayRtState = new Dictionary<string, Dictionary<string, TradingState>>
            {
                { "TXF", new Dictionary<string, TradingState> { { "日盤", new() }, { "夜盤", new() } } },
                { "MXF", new Dictionary<string, TradingState> { { "日盤", new() }, { "夜盤", new() } } }
            };

            _rtTriggers = new Dictionary<string, Dictionary<string, List<PendingTrigger>>>
            {
                { "TXF", new Dictionary<string, List<PendingTrigger>> { { "日盤", [] }, { "夜盤", [] } } },
                { "MXF", new Dictionary<string, List<PendingTrigger>> { { "日盤", [] }, { "夜盤", [] } } }
            };

            _rtCompletedDetails = new Dictionary<string, Dictionary<string, List<CompletedTrigger>>>
            {
                { "TXF", new Dictionary<string, List<CompletedTrigger>> { { "日盤", [] }, { "夜盤", [] } } },
                { "MXF", new Dictionary<string, List<CompletedTrigger>> { { "日盤", [] }, { "夜盤", [] } } }
            };

            InitializeComponent();

            // 2. 繫結 WPF DataGrid 容器
            dgKline.ItemsSource = _klineCollection;
            dgObserver.ItemsSource = _obsCollection;
            _obsCollection.CollectionChanged += ObsCollection_CollectionChanged;
            if (icIntervalStats != null) icIntervalStats.ItemsSource = _intervalStatsCollection;

            // 3. 註冊視窗載入與關閉事件
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            
            // 4. 註冊 WPF 原生 60/120 FPS 畫面更新迴圈 (極致流暢報價)
            CompositionTarget.Rendering += OnCompositionTargetRendering;

            _isInitialized = true; // 所有元件與事件綁定完成，正式開放事件處理
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 載入設定檔
            LoadConfig();
            
            // 檢查量化分析報告並顯示於狀態列
            lblStatus.Content = GetQuantReportStatus();

            // 初始化未破停損分K監控與 K線圖
            wndUnbrokenK.Initialize(_engine, this);

            // 啟動非同步背景 Telegram 傳送
            _tgService.Start();

            // 啟動實時行情背景 Debounce 分析 Task
            _analysisCts = new CancellationTokenSource();
            _analysisTask = Task.Run(() => AnalysisWorkerLoopAsync(_analysisCts.Token));

            // 啟動 Market Data Worker 處理即時 Tick 解析
            _marketDataCts = new CancellationTokenSource();
            _marketDataTask = Task.Run(() => MarketDataWorkerLoopAsync(_marketDataCts.Token));

            AppendLog("【系統】C# WPF 高性能交易看盤軟體載入成功。", forceScrollToEnd: true);

            // 啟動自動連線與訂閱商品 (開機即時載入)
            StartRealtime();

            // 啟動盤別自動切換檢查計時器 (每 60 秒檢查一次)
            _sessionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _sessionTimer.Tick += CheckSessionChange;
            _sessionTimer.Start();
        }

        private void CheckSessionChange(object? sender, EventArgs e)
        {
            if (_yuantaQuote == null && _axHost == null) return; // 若未連線則不處理

            var (port, session) = CheckSessionPort();
            if (session != _currentSessionName)
            {
                // 清空未破分 K 與趨勢表單資料
                wndUnbrokenK.Clear();

                // 透過 clear: true 參數清空主表單日誌，並印出切換提示
                AppendLog($"\n--- 偵測到盤別切換：重啟連線至 {port} ({session}) ---", clear: true);
                StopRealtime();
                
                // 延遲 2 秒後重新連線並自動清空資料
                Task.Delay(2000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _skipNextPreload = true;
                    StartRealtime();
                })));
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            StopReplay();
            StopRealtime();

            _analysisCts?.Cancel();
            _analysisCts?.Dispose();
            
            _tickChannel.Writer.TryComplete();
            _marketDataCts?.Cancel();
            _marketDataCts?.Dispose();

            _tgService.Dispose();
            _sessionTimer?.Stop();
            CompositionTarget.Rendering -= OnCompositionTargetRendering;

            wndUnbrokenK.Reset();
            klineChart.Reset();
        }

        // 60/120 FPS 畫面渲染迴圈 (Render Loop)
        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            if (!_isInitialized) return;
            
            // 每幀無鎖撈取最新的 volatile 變數
            int mxfPrice = _renderMxfPrice;
            string mxfTime = Volatile.Read(ref _renderMxfTime) ?? "";
            int txfPrice = _renderTxfPrice;
            string txfTime = Volatile.Read(ref _renderTxfTime) ?? "";

            bool mxfUpdated = mxfPrice > 0 && (mxfPrice != _lastRenderedMxfPrice || mxfTime != _lastRenderedMxfTime);
            bool txfUpdated = txfPrice > 0 && (txfPrice != _lastRenderedTxfPrice || txfTime != _lastRenderedTxfTime);

            if (mxfUpdated || txfUpdated)
            {
                if (mxfUpdated)
                {
                    _lastRenderedMxfPrice = mxfPrice;
                    _lastRenderedMxfTime = mxfTime;
                    
                    // 瞬間更新 UI 文字，無 150ms 延遲
                    lblLivePrice.Text = $"| 價: {mxfPrice}";

                    // 用最新的小台價格更新相關 UI (以小台為主要顯示商品)
                    if (wndUnbrokenK != null)
                    {
                        // 瞬間觸發停損反向偵測，速度極大化
                        wndUnbrokenK.CheckInstantUnbrokenBreakout(mxfPrice, mxfTime);
                    }
                    
                    if (klineChart != null)
                    {
                        // 瞬間更新 K 線圖最後一根 K 棒，繞過 100ms Debounce
                        klineChart.UpdateLastCandleInstant(mxfPrice, mxfTime);
                    }

                    // --- 新增：DataGrid K 線表格瞬間閃爍差量更新 ---
                    if (_klineCollection.Count > 0)
                    {
                        var lastBar = _klineCollection[^1];
                        if (mxfPrice > lastBar.High)
                        {
                            lastBar.High = mxfPrice;
                        }
                        if (mxfPrice < lastBar.Low)
                        {
                            lastBar.Low = mxfPrice;
                        }
                        if (mxfPrice != lastBar.Close)
                        {
                            lastBar.Close = mxfPrice;
                            
                            // 動態修改顏色標籤
                            if (lastBar.Close > lastBar.Open) lastBar.Tag = "up";
                            else if (lastBar.Close < lastBar.Open) lastBar.Tag = "down";
                            else lastBar.Tag = "flat";
                        }
                    }
                }

                if (txfUpdated)
                {
                    _lastRenderedTxfPrice = txfPrice;
                    _lastRenderedTxfTime = txfTime;
                }

                // --- 新增：即時計算未實現損益並更新圖表 ---
                if (klineChart != null)
                {
                    // 若有持倉，根據當前最新的小台/大台價來計算損益
                    int currentPrice = mxfPrice > 0 ? mxfPrice : (txfPrice > 0 ? txfPrice : 0);
                    if (_currentPositionLots != 0 && _currentPositionCost > 0 && currentPrice > 0)
                    {
                        // 假設主要以小台 (MXF) 為主，乘數為 50
                        int multiplier = 50; 
                        double pnl = (currentPrice - _currentPositionCost) * _currentPositionLots * multiplier;
                        klineChart.UpdatePositionInfo(_currentPositionLots, _currentPositionCost, pnl);
                    }
                    else
                    {
                        klineChart.UpdatePositionInfo(0, 0, 0);
                    }
                }
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isInitialized) return;

            // 基準寬度為 1300，計算縮放比例
            double scale = Math.Max(0.5, this.ActualWidth / 1300.0);
            
            // 更新全域動態字型大小資源
            double baseFontSize = 12.0;
            Application.Current.Resources["GlobalFontSize"] = baseFontSize * scale;
        }

        // ==================== 0. 設定載入 ====================

        private void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("telegram_token", out var tokenProp))
                        _tgService.Token = tokenProp.GetString() ?? "";
                    if (root.TryGetProperty("telegram_chat_id", out var chatProp))
                        _tgService.ChatId = chatProp.GetString() ?? "";
                    
                    _tgService.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"【系統】載入 config.json 出錯: {ex.Message}");
            }
        }

        // ==================== 1. 元大 COM API 連接與斷開 ====================

        private void StartRealtime()
        {
            if (_yuantaQuote != null) return;

            AppendLog("【行情】正在嘗試加載原生 32-bit 元大行情 COM 控制項...");
            btnRealtime.Content = "停止即時行情";
            btnRealtime.Background = System.Windows.Media.Brushes.Orange;

            // 1. 智慧檢測與 Port 判定 (日盤443/夜盤442)
            var (port, session) = CheckSessionPort();
            _currentRealtimePort = port;
            _currentSessionName = session;
            lblRealtimeStatus.Content = "加載元件中...";
            lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Orange;
            AppendLog($"\n--- 啟動即時行情 ({session} Port:{port}) ---");

            lock (_rtLock)
            {
                _liveSymbolTrades["TXF"]["日盤"].Clear();
                _liveSymbolTrades["TXF"]["夜盤"].Clear();
                _liveSymbolTrades["MXF"]["日盤"].Clear();
                _liveSymbolTrades["MXF"]["夜盤"].Clear();

                _rtState["TXF"]["日盤"].Reset();
                _rtState["TXF"]["夜盤"].Reset();
                _rtState["MXF"]["日盤"].Reset();
                _rtState["MXF"]["夜盤"].Reset();
                
                _rtTriggers["TXF"]["日盤"].Clear();
                _rtTriggers["TXF"]["夜盤"].Clear();
                _rtTriggers["MXF"]["日盤"].Clear();
                _rtTriggers["MXF"]["夜盤"].Clear();

                _rtCompletedDetails["TXF"]["日盤"].Clear();
                _rtCompletedDetails["TXF"]["夜盤"].Clear();
                _rtCompletedDetails["MXF"]["日盤"].Clear();
                _rtCompletedDetails["MXF"]["夜盤"].Clear();

                _rtNotifiedKeys.Clear();
                _rtLastNetSpeedsTop["TXF"] = null;
                _rtLastNetSpeedsTop["MXF"] = null;
                _rtLastNetSpeedsBot["TXF"] = null;
                _rtLastNetSpeedsBot["MXF"] = null;
                _txfLastMatchQty = -1;
                _mxfLastMatchQty = -1;
                _isRecovering = true;

                // 【新增】徹底清空量化引擎與背景執行緒快取，防止日盤極值與 K 線污染夜盤
                _engine.ClearCache();
                _lastHeavyCalcTimeMs = 0;
                _lastKlineData.Clear();
                _lastSimulationResults.Clear();
                _lastSharedResultsMap = null;
                _lastRtStatusSnapshot.Clear();

                _uiGeneration++; // 阻斷過期背景任務的 UI 推送

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _klineCollection.Clear();
                    _obsCollection.Clear();
                    _intervalStatsCollection.Clear();
                    klineChart?.Reset();

                    // 清空底部觀察狀態設定值
                    txtObsHigh.Text = "";
                    txtObsLow.Text = "";
                    cboObsHigh.Text = "";
                    cboObsHigh.Items.Clear();
                    cboObsLow.Text = "";
                    cboObsLow.Items.Clear();
                }));
            }

            try
            {
                // 2. 讀取商品合約代碼 (優先從 code.json 讀取)
                string symbolsArg = "";
                string codeJsonPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "code.json");
                if (!System.IO.File.Exists(codeJsonPath))
                {
                    codeJsonPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "code.json");
                }
                
                if (System.IO.File.Exists(codeJsonPath))
                {
                    try
                    {
                        string json = System.IO.File.ReadAllText(codeJsonPath, System.Text.Encoding.UTF8);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("future", out var futureArray) && futureArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var item in futureArray.EnumerateArray())
                            {
                                string? sym = item.GetString();
                                if (!string.IsNullOrEmpty(sym)) list.Add(sym);
                            }
                            symbolsArg = string.Join("|", list);
                            AppendLog($"【系統】成功從 code.json 載入自動註冊商品清單: {symbolsArg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"【系統】讀取 code.json 商品代碼出錯: {ex.Message}，將回退至月份推算代碼。");
                    }

                    // === 結算日自動換約檢查 ===
                    // 比對 code.json 中合約的月份碼（最後兩字元）與 GetMonthCode() 推算結果，
                    // 若不一致代表合約已過期（結算日 14:50 後 GetMonthCode 自動切至下月），
                    // 自動替換為新合約代碼並回寫 code.json。
                    if (!string.IsNullOrEmpty(symbolsArg))
                    {
                        string currentMonthCode = _engine.GetMonthCode();
                        var updatedSymbols = new System.Collections.Generic.List<string>();
                        bool contractExpired = false;

                        foreach (var sym in symbolsArg.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (sym.Length >= 3)
                            {
                                // 合約代碼格式：基底(TXF/MXF) + 月份碼(F6) → 最後兩字元為月份碼
                                string symMonthCode = sym.Substring(sym.Length - 2);
                                if (symMonthCode != currentMonthCode)
                                {
                                    string baseSym = sym.Substring(0, sym.Length - 2);
                                    updatedSymbols.Add($"{baseSym}{currentMonthCode}");
                                    contractExpired = true;
                                }
                                else
                                {
                                    updatedSymbols.Add(sym);
                                }
                            }
                        }

                        if (contractExpired)
                        {
                            string oldArg = symbolsArg;
                            symbolsArg = string.Join("|", updatedSymbols);
                            AppendLog($"【換約】偵測到合約到期！{oldArg} → {symbolsArg}，已自動更新 code.json");

                            // 回寫 code.json，確保下次啟動也使用新合約
                            try
                            {
                                var jsonObj = new { future = updatedSymbols };
                                string newJson = System.Text.Json.JsonSerializer.Serialize(jsonObj,
                                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                System.IO.File.WriteAllText(codeJsonPath, newJson, System.Text.Encoding.UTF8);
                                AppendLog($"【換約】code.json 已成功回寫: {string.Join(", ", updatedSymbols)}");
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"【換約】回寫 code.json 失敗: {ex.Message}");
                            }
                        }
                    }
                }

                // 備用機制：若 code.json 讀取失敗或無商品，以近月月份月份代碼推算 TXF/MXF
                if (string.IsNullOrEmpty(symbolsArg))
                {
                    string monthCode = _engine.GetMonthCode();
                    symbolsArg = $"TXF{monthCode}|MXF{monthCode}";
                    AppendLog($"【系統】月份代碼推算商品清單: {symbolsArg}");
                }

                _symbolsToRegister = symbolsArg.Split('|', StringSplitOptions.RemoveEmptyEntries);

                // 3. 載入帳號密碼
                string user = "";
                string pwd = "";
                string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (System.IO.File.Exists(configPath))
                {
                    try
                    {
                        string json = System.IO.File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("username", out var u)) user = u.GetString() ?? "";
                        if (root.TryGetProperty("password", out var p)) pwd = p.GetString() ?? "";
                        _mktUser = user;
                        _mktPwd = pwd;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"【系統】讀取 config.json 帳密出錯: {ex.Message}");
                    }
                }

                // 4. 動態加載 32-bit 元大行情 COM API 元件
                // 4. 加載 32-bit 元大行情 COM API 元件 (強型別)
                try
                {
                    _axHost = new AxYuantaQuoteLib.AxYuantaQuote();
                }
                catch (Exception ex)
                {
                    AppendLog($"【系統錯誤】無法載入元大行情 COM 元件！原因: {ex.Message}。請確保以系統管理員身分執行 regsvr32 註冊元件。");
                    lblRealtimeStatus.Content = "元件未註冊";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Red;
                    StopRealtime();
                    return;
                }

                // 將 _axHost 直接掛載到 WPF 內置的 WindowsFormsHost 控制項
                comHost.Child = _axHost;

                _yuantaQuote = new YuantaQuoteWrapper(_axHost);
                _yuantaQuote.MktStatusChanged += OnMktStatusChanged;
                _yuantaQuote.GetMktAllReceived += OnGetMktAll;

                AppendLog($"【行情】原生 32-bit COM 控制項加載成功。正在呼叫 SetMktLogon (伺服器: 203.66.93.84, Port: {port})...");


                // 5. 呼叫元大登入
                int res = _yuantaQuote.SetMktLogon(user, pwd, "203.66.93.84", port.ToString(), 1, 0);
                AppendLog($"【行情】SetMktLogon 呼叫完成，回傳結果代碼: {res} (0 代表送出登入成功)");
                
                if (res != 0)
                {
                    AppendLog($"【系統】呼叫元大登入介面失敗，回傳代碼: {res}");
                    lblRealtimeStatus.Content = "登入呼叫失敗";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Red;
                    StopRealtime();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"【行情】連接行情元件出錯: {ex.Message}");
                StopRealtime();
            }
        }

        private void StopRealtime()
        {
            if (_yuantaQuote == null && _axHost == null) return;

            AppendLog("\n--- 停止即時行情與釋放 COM 資源 ---");
            btnRealtime.Content = "連接即時行情";
            btnRealtime.Background = (System.Windows.Media.Brush)Application.Current.Resources["SciFiActiveBg"];
            lblRealtimeStatus.Content = "未連線";
            lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Red;

            // 登出下單/帳務 API 並註銷事件
            try
            {
                if (_yuantaOrd != null)
                {
                    _yuantaOrd.DoLogout();
                    _yuantaOrd.OnLogonS -= OnOrdLogonS;
                    _yuantaOrd.OnUserDefinsFuncResult -= OnOrdUserDefinsFuncResult;
                    _yuantaOrd.OnOrdMatF -= OnOrdMatF;
                    _yuantaOrd.OnOrdResult -= OnOrdResult;
                    _yuantaOrd.OnOrdRptF -= OnOrdRptF;
                    AppendLog("【帳務】DoLogout 呼叫完成，註銷事件。");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"【系統】釋放下單 API 時發生異常: {ex.Message}");
            }
            finally
            {
                _yuantaOrd = null;
                _currentBranch = string.Empty;
                _currentAccount = string.Empty;
                _isOrdLoggedIn = false;
                _isOrdLoggingIn = false;
                lblPnLAccount.Text = "帳號: 未登入";
                lblTodayPnL.Text = "NT$ 0";
                lblTodayPnL.Foreground = System.Windows.Media.Brushes.LightGray;
            }

            // 安全斬斷事件連接，徹底防範幽靈 Tick 發生
            try
            {
                if (_yuantaQuote != null)
                {
                    _yuantaQuote.MktStatusChanged -= OnMktStatusChanged;
                    _yuantaQuote.GetMktAllReceived -= OnGetMktAll;
                    _yuantaQuote.DisconnectEvents();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"【系統】斷開 COM 事件時發生異常: {ex.Message}");
            }
            finally
            {
                _yuantaQuote = null;
            }

            // 安全卸載 WinFormsHost 子控制項並銷毀元件
            try
            {
                if (_axHost != null)
                {
                    comHost.Child = null; // 卸載
                    _axHost.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"【系統】銷毀 AxHost 時發生異常: {ex.Message}");
            }
            finally
            {
                _axHost = null;
            }
        }

        private void ToggleRealtime()
        {
            if (_yuantaQuote == null)
            {
                _isRealtimeUIEnabled = true;
                StartRealtime();
            }
            else
            {
                if (_isRealtimeUIEnabled)
                {
                    _isRealtimeUIEnabled = false;
                    btnRealtime.Content = "連接即時行情";
                    btnRealtime.Background = (System.Windows.Media.Brush)Application.Current.Resources["SciFiActiveBg"];
                    lblRealtimeStatus.Content = "暫停更新 (背景接收中)";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Yellow;
                    AppendLog("\n--- 暫停即時行情更新 (後台持續接收) ---");
                }
                else
                {
                    _isRealtimeUIEnabled = true;
                    btnRealtime.Content = "停止即時行情";
                    btnRealtime.Background = System.Windows.Media.Brushes.Orange;
                    lblRealtimeStatus.Content = $"已連線 ({_currentRealtimePort})";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    AppendLog("\n--- 恢復即時行情更新並執行預載 ---");
                    
                    lock (_rtLock)
                    {
                        _isPreloading = true;
                    }
                    PreloadTodayLog();
                }
            }
        }

        // ==================== 2. 元大 COM 事件回呼 (由橋樑發送並由主程式分發) ====================

        private void OnMktStatusChanged(int status, string msg, int reqType)
        {
            // 回到 UI 執行緒 safe 執行
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"【行情狀態】: {status} {msg}");
                
                if (status == 2)
                {
                    // 標註目前連線線路 (日盤443，夜盤442，或其它自選)
                    lblRealtimeStatus.Content = $"已連線 ({_currentRealtimePort})";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

                    // 延遲連線下單 API，避免行情與下單連續登入導致元大 API 報錯 -101
                    if (_isOrdLoggedIn || _isOrdLoggingIn)
                    {
                        AppendLog($"【行情】已連線。帳務 API 已在登入狀態或正在登入，跳過重複呼叫。已登入: {_isOrdLoggedIn}, 登入中: {_isOrdLoggingIn}");
                    }
                    else
                    {
                        AppendLog("【行情】已連線。3 秒後自動啟動下單/帳務 API 連線登入...");
                        DispatcherTimerExtensions.RunOnce(() =>
                        {
                            try
                            {
                                if (_isOrdLoggedIn || _isOrdLoggingIn) return;
                                if (!string.IsNullOrEmpty(_mktUser) && !string.IsNullOrEmpty(_mktPwd))
                                {
                                    InitYuantaOrd();
                                    int ordRes = LoginYuantaOrd(_mktUser, _mktPwd);
                                    AppendLog($"【帳務】SetFutOrdConnection 呼叫完成，回傳結果代碼: {ordRes} (2 代表已連線成功，其他代表送出中或失敗)");
                                }
                            }
                            catch (Exception ordEx)
                            {
                                AppendLog($"【帳務錯誤】自動登入帳務 API 失敗: {ordEx.Message}");
                            }
                        }, TimeSpan.FromMilliseconds(3000));
                    }
                    
                    // 1. 設定預載標記，在此期間 Tick 將靜默寫入快取，不觸發背景量化計算
                    lock (_rtLock)
                    {
                        _isPreloading = true;
                    }

                    // 2. 先完成商品註冊 (確保即時 Tick 開始接收與寫入快取，防漏 Tick)
                    if (_yuantaQuote != null && _symbolsToRegister.Length > 0)
                    {
                        foreach (var code in _symbolsToRegister)
                        {
                            int res = _yuantaQuote.AddMktReg(code, 4, reqType, 0); // Mode 4 = Snapshot+Update
                            AppendLog($"【行情】已自動註冊近月合約 {code} (結果代碼: {res})");
                        }
                    }

                    // 3. 接著進行背景非同步預載與合併 (預載完成後會解鎖 _isPreloading 並觸發 Tick 計算)
                    if (_skipNextPreload)
                    {
                        _skipNextPreload = false;
                        AppendLog("【系統】自動換盤完成，跳過歷史日誌預載。");
                        lock (_rtLock)
                        {
                            _isPreloading = false;
                        }
                        _analysisEvent.Set();
                    }
                    else
                    {
                        PreloadTodayLog();
                    }
                }
                else if (status == 1)
                {
                    lblRealtimeStatus.Content = $"連線中 ({_currentRealtimePort})";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else if (status < 0)
                {
                    lblRealtimeStatus.Content = $"連線異常 ({status})";
                    lblRealtimeStatus.Foreground = System.Windows.Media.Brushes.Red;

                    // 檢查是否為精確盤中時間
                    DateTime now = DateTime.UtcNow.AddHours(8); // 台北時間
                    int weekday = (int)now.DayOfWeek;
                    int timeVal = now.Hour * 3600 + now.Minute * 60 + now.Second;

                    // 檢查是否為週末休盤 (週日全天，或週六早上 5 點後)
                    bool isWeekendClosed = (weekday == 0) || (weekday == 6 && timeVal > 5 * 3600);
                    if (isWeekendClosed)
                    {
                        AppendLog("【系統】目前為週末休盤期間，停止自動重新連線。");
                        if (chkTelegram.IsChecked == true)
                        {
                            _tgService.PushMessage($"🚨 台指極值元大行情網路中斷！目前為週末休盤，已停止自動重連 ({status})");
                        }
                        StopRealtime();
                        return;
                    }

                    bool isDaySession = (1 <= weekday && weekday <= 5) && (timeVal >= 8 * 3600 + 45 * 60 && timeVal <= 13 * 3600 + 45 * 60);
                    bool isNightSession1 = (1 <= weekday && weekday <= 5) && (timeVal >= 15 * 3600);
                    bool isNightSession2 = (2 <= weekday && weekday <= 6) && (timeVal <= 5 * 3600);
                    
                    bool isStrictTradingSession = isDaySession || isNightSession1 || isNightSession2;
                    int delayMs = isStrictTradingSession ? 2000 : 60000;

                    if (chkTelegram.IsChecked == true)
                    {
                        _tgService.PushMessage($"🚨 台指極值元大行情網路中斷！嘗試於 {(delayMs / 1000)} 秒後重連 ({status})");
                    }

                    // 依據盤中與否設定重連延遲
                    System.Threading.Tasks.Task.Delay(delayMs).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopRealtime();
                        StartRealtime();
                    })));
                }
            }));
        }

        private void OnGetMktAll(
            string symbol, string refPri, string openPri, string highPri, string lowPri,
            string upPri, string dnPri, string matchTime, string matchPri, string matchQty,
            string tolMatchQty, string bestBuyQty, string bestBuyPri, string bestSellQty, string bestSellPri,
            string fdbPri, string fdbQty, string fdsPri, string fdsQty, int reqType)
        {
            if (_yuantaQuote == null) return;
            if (tolMatchQty == "-1") return;

            // Zero UI Blocking: 將字串封裝後直接丟入 Lock-Free Channel，交由背景 Market Data Thread 進行 Parse 與增量計算
            var rawTick = new RawTickData(symbol, matchTime, matchPri, tolMatchQty, bestBuyPri, bestSellPri);
            _tickChannel.Writer.TryWrite(rawTick);
        }

        // ==================== 2.5 Market Data Background Worker ====================
        
        private async Task MarketDataWorkerLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (var rawTick in _tickChannel.Reader.ReadAllAsync(token))
                {
                    ProcessRawTick(rawTick);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AppendLog($"🚨【行情處理崩潰】Market Data Thread 發生未知錯誤: {ex.Message}\n{ex.StackTrace}");
                }));
            }
        }

        private void ProcessRawTick(RawTickData raw)
        {
            try
            {
                string symbol = raw.Symbol;
                string tolMatchQty = raw.TolMatchQty;
                string matchPri = raw.MatchPri;
                string matchTime = raw.MatchTime;
                string bestBuyPri = raw.BestBuyPri;
                string bestSellPri = raw.BestSellPri;

                int currentQty = int.Parse(tolMatchQty);
                // Zero Allocation 字串比對
                string baseSymbol = symbol.StartsWith("TXF") ? "TXF" : (symbol.StartsWith("MXF") ? "MXF" : "");
                if (string.IsNullOrEmpty(baseSymbol)) return;

                int tickQty = 1;
                // 重複/滯後 Tick 行情過濾防線 (Lock-Free)
                if (baseSymbol == "TXF")
                {
                    if (currentQty <= _txfLastMatchQty) return;
                    tickQty = _txfLastMatchQty > 0 ? (currentQty - _txfLastMatchQty) : 1;
                    _txfLastMatchQty = currentQty;
                }
                else
                {
                    if (currentQty <= _mxfLastMatchQty) return;
                    tickQty = _mxfLastMatchQty > 0 ? (currentQty - _mxfLastMatchQty) : 1;
                    _mxfLastMatchQty = currentQty;
                }

                int price = (int)double.Parse(matchPri.AsSpan());
                
                // Zero Allocation 解析買一賣一 (取代 Split(',') 避免產生陣列與字串垃圾)
                int bestBp = ParseFirstPrice(bestBuyPri);
                int bestSp = ParseFirstPrice(bestSellPri);

                if (price <= 0 || bestBp <= 0 || bestSp <= 0) return;

                TradeSide side = TradeSide.Unknown;
                if (price >= bestSp) side = TradeSide.Outer;
                else if (price <= bestBp) side = TradeSide.Inner;

                string mt = matchTime.Trim();
                if (mt.Length < 6) return;

                double tValRaw = TimeParser.ParseTime(mt);

                // 交易空檔時間過濾
                if ((tValRaw >= 30600 && tValRaw < 31500) || (tValRaw >= 52200 && tValRaw < 54000))
                    return;

                string session = "";
                double tVal = tValRaw;

                if (tValRaw >= 30600 && tValRaw <= 49500)
                {
                    session = "日盤";
                }
                else if (tValRaw >= 52200 || tValRaw <= 18000)
                {
                    session = "夜盤";
                    if (tValRaw <= 18000) tVal += 86400.0; // 跨日秒數加算
                }
                else
                {
                    return;
                }

                // 買賣盤方向Fallback處理 (Lock-Free)
                if (side == TradeSide.Unknown)
                {
                    var prevTrades = _liveSymbolTrades[baseSymbol][session];
                    side = prevTrades.Count > 0 ? prevTrades[^1].Side : TradeSide.Outer;
                }

                byte symId = (byte)(baseSymbol == "MXF" ? 1 : 0);
                byte sessId = (byte)(session == "夜盤" ? 1 : 0);
                var tick = new TradeTick(symId, sessId, tVal, price, tickQty, side, bestBp, bestSp);
                
                // 完全 Lock-Free 的資料結構寫入 (Zero GC Allocation & No OS Lock)
                _liveSymbolTrades[baseSymbol][session].Add(tick);

                if (baseSymbol == "TXF")
                {
                    _lastTxfPrice = price;
                }
                else if (baseSymbol == "MXF")
                {
                    _lastMxfPrice = price;
                }

                // 實時 Render Loop 快取 (單向寫入)
                if (!_isReplaying)
                {
                    if (baseSymbol == "TXF")
                    {
                        _renderTxfPrice = price;
                        _renderTxfBestBp = bestBp;
                        _renderTxfBestSp = bestSp;
                        Interlocked.Exchange(ref _renderTxfTime, mt);
                    }
                    else if (baseSymbol == "MXF")
                    {
                        _renderMxfPrice = price;
                        _renderMxfBestBp = bestBp;
                        _renderMxfBestSp = bestSp;
                        Interlocked.Exchange(ref _renderMxfTime, mt);
                    }
                }
                // 價格觸發下單監控
                if (!_isReplaying && !_isPreloading)
                {
                    string monthCode = _engine.GetMonthCode();
                    string completeSymbol = baseSymbol + monthCode;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MonitorPositionsForStopLossAndTakeProfit(completeSymbol, price);
                    }));
                }

                // 與 UI 分離，延遲處理：背景即時 Tick 的寫入，不應被 UI 的更新拖慢
                if (!_isReplaying && !_isPreloading && _isRealtimeUIEnabled)
                {
                    _analysisEvent.Set();
                }
            }
            catch { }
        }

        // Zero Allocation 字串數字提取 (避免 Split(',') 造成的字串與陣列配置)
        private int ParseFirstPrice(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int commaIdx = s.IndexOf(',');
            var span = commaIdx < 0 ? s.AsSpan() : s.AsSpan(0, commaIdx);
            if (double.TryParse(span, out double val)) return (int)val;
            return 0;
        }

        // ==================== 3. 實時行情背景 Debounce 計算 Task 迴圈 ====================

        private async Task AnalysisWorkerLoopAsync(CancellationToken token)
        {
            // 將 CancellationToken 的 WaitHandle 與 _analysisEvent 組合，實現真正的零 CPU 阻塞等待
            var handles = new WaitHandle[] { _analysisEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                // 真正的零 CPU 阻塞：沒有 Tick 行情就完全不消耗，從根本消滅每秒 1 次的空轉唤醒
                int which = WaitHandle.WaitAny(handles);
                if (token.IsCancellationRequested || which == 1) break;

                // 恢復適度的節流 (Debounce)：避免行情過於密集時 (10,000 Ticks/sec) 導致 UI Thread 癱瘓與 CPU 100% 滿載
                // 背景執行緒依然會一次性批次處理 (Batch Process) 這期間累積的所有 Ticks，達成高吞吐量且極低耗電。
                _analysisEvent.Reset(); // 清除事件狀態

                try
                {
                    // 先捕獲世代再計算：若計算期間 _uiGeneration 被遞增（載入復盤/停止復盤），結果即失效
                    int gen = _uiGeneration;

                    // 在背景執行緒執行高密度狀態機計算 (會一口氣處理累積的所有 Ticks)
                    var result = RunRealtimeAnalysisCompute();
                    
                    // 安全分發至主介面更新 UI (WPF RichTextBox, DataGrids, Labels)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_uiGeneration != gen) return; // 世代不符，丟棄過期的分析結果
                        ApplyRealtimeAnalysisUI(result);
                    }));
                }
                catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "index")
                {
                    // 由於背景計算期間 UI 觸發了資料清空或切換 (例如載入新復盤)，導致背景正在遍歷的索引越界。
                    // 這是無鎖設計下的預期重置競態現象 (Lock-Free Reset Race)，予以安全忽略，下次迴圈即會恢復。
                }
                catch (Exception ex)
                {
                    // 升級：將背景計算例外直接安全輸出至主日誌看板，杜絕 Silent Crash
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"🚨【量化計算崩潰】核心計算背景執行緒發生未知錯誤: {ex.Message}\n{ex.StackTrace}");
                    }));
                }

                // 執行完一次全量運算與 UI 推送後，強迫休息 100 毫秒 (約 10 FPS)
                // 此舉可節省 90% 以上的 CPU 運算資源，徹底解決發熱與耗電問題。
                try { await Task.Delay(100, token); } catch (TaskCanceledException) { break; }
            }
        }

        private static string FormatExtremeTime(string? t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 6) return t ?? string.Empty;
            return $"{t[..2]}:{t[2..4]}:{t[4..6]}";
        }

        private string GetZoneStr(string side, string bestATime, int bestAPrice, string activeSession, Dictionary<string, object> quantParams)
        {
            string zoneStr = "N/A";
            try
            {
                var tDict = (Dictionary<string, (int p50, int p75, int p90)>)quantParams[side == "top" ? "time_top" : "time_bottom"];
                int totalM = ParseTimeToMinutes(bestATime);
                if (totalM < 0) return "N/A";
                if (activeSession == "夜盤" && totalM < 900) totalM += 1440;

                int? p50 = null, p75 = null, p90 = null;
                foreach (var kvp in tDict)
                {
                    if (kvp.Key.Contains(activeSession))
                    {
                        string timePart = kvp.Key.Split(' ')[1].Trim();
                        if (timePart.Contains('-'))
                        {
                            var sStr = timePart.Split('-')[0];
                            var eStr = timePart.Split('-')[1];
                            int sMins = int.Parse(sStr.Split(':')[0]) * 60 + int.Parse(sStr.Split(':')[1]);
                            int eMins = int.Parse(eStr.Split(':')[0]) * 60 + int.Parse(eStr.Split(':')[1]);
                            if (activeSession == "夜盤")
                            {
                                if (sMins < 900) sMins += 1440;
                                if (eMins < 900) eMins += 1440;
                            }
                            if (sMins <= totalM && totalM <= eMins)
                            {
                                p50 = kvp.Value.p50;
                                p75 = kvp.Value.p75;
                                p90 = kvp.Value.p90;
                                break;
                            }
                        }
                    }
                }

                if (p50.HasValue && p75.HasValue && p90.HasValue)
                {
                    if (side == "top")
                        zoneStr = $"區:{bestAPrice + p50.Value}~{bestAPrice + p75.Value} 損:{bestAPrice + p90.Value}";
                    else
                        zoneStr = $"區:{bestAPrice - p50.Value}~{bestAPrice - p75.Value} 損:{bestAPrice - p90.Value}";
                }
            }
            catch { }
            return zoneStr;
        }

        private void ProcessTriggerBlock(
            PendingTrigger item,
            bool isTrigH,
            int price,
            double tVal,
            int rMax,
            int rMin,
            string symbol,
            string activeSession,
            int currentTradesCount,
            IReadOnlyList<TradeTick> trades,
            Dictionary<string, IReadOnlyList<TradeTick>> tradesSnapshot,
            int nTicks)
        {
            var side1 = isTrigH ? TradeSide.Outer : TradeSide.Inner;
            var side2 = isTrigH ? TradeSide.Inner : TradeSide.Outer;

            _engine.GetDurations(trades, nTicks, item, side1, side2, currentTradesCount);
            double? pre = item.PreAvg;
            int preVol = item.PreVol;
            double? post = item.ActualPostN >= nTicks ? (item.PostSum / item.ActualPostN) : null;
            int postVol = item.PostVol;
            int bIdx = item.ScanIndex - 1;

            string status = _engine.GetStatusStr(pre, post, item.ActualPreN, item.ActualPostN, nTicks);
            
            bool isDead = item.ActualPostN < nTicks && bIdx < currentTradesCount - 1;

            if (status == " [達標]" || status == " [邊界達標]" || status == " [未達標]" || isDead)
            {
                if (isDead && status.Contains("邊界未達標")) status = " [未達標]";

                int amp = 0;
                if (isTrigH)
                {
                    amp = rMin != 999999 ? (rMax - rMin) : 0;
                }
                else
                {
                    amp = rMax != -999999 ? (rMax - rMin) : 0;
                }

                double? baseNet = null;
                double? otherNet = null;
                var (oAvg, iAvg, dDir) = TradingEngine.CalcSideSpeedFromState(item.BaseStateSnapshot);
                if (oAvg.HasValue && iAvg.HasValue) baseNet = iAvg.Value - oAvg.Value;
                
                long sumPrice = item.BaseStateSnapshot.SumPrice;
                int avgPri = item.BaseStateSnapshot.Count > 0 ? (int)Math.Round((double)sumPrice / item.BaseStateSnapshot.Count) : 0;

                string oSym = symbol == "TXF" ? "MXF" : "TXF";
                if (tradesSnapshot.TryGetValue(oSym, out var otherTrades))
                {
                    double targetTVal = trades[bIdx].TimeVal;
                    int otherCount = _engine.FindTickCountByTime(otherTrades, targetTVal);
                    otherNet = _engine.CalcNetSpeed(otherTrades, otherCount);
                }

                var completed = new CompletedTrigger
                {
                    TVal = tVal, StatusOnly = status, ATime = trades[item.Index].Time, PriceVal = price,
                    TrigTime = item.TrigTime ?? "N/A", TrigPrice = item.TrigPrice ?? 0,
                    Pre = pre, PreVol = preVol, Post = post, PostVol = postVol, AmpVal = amp, BIdx = bIdx, IsTrigH = isTrigH,
                    BaseNet = baseNet, OtherNet = otherNet, DStr = dDir, AvgPri = avgPri
                };

                lock (_rtLock)
                {
                    _rtTriggers[symbol][activeSession].Remove(item);
                    _rtCompletedDetails[symbol][activeSession].Add(completed);
                }
            }
        }

        private Dictionary<string, object> RunRealtimeAnalysisCompute()
        {
            string activeSession;
            if (_isReplaying)
                activeSession = _currentReplaySession;
            else
                activeSession = _currentRealtimePort == 442 ? "夜盤" : "日盤";

            var tradesSnapshot = new Dictionary<string, IReadOnlyList<TradeTick>>();
            var stateSnapshot = new Dictionary<string, TradingState>();

            var tradesSource = _isReplaying ? _replaySymbolTrades : _liveSymbolTrades;
            var stateSource = _isReplaying ? _replayRtState : _rtState;

            foreach (var symbol in new[] { "TXF", "MXF" })
            {
                // Lock-Free 參考拷貝 (Zero Allocation)
                tradesSnapshot[symbol] = tradesSource[symbol][activeSession]; 
            }

            var currentStatusSnapshot = new List<(string Symbol, string Session, int DayMin, int DayMax, List<SimulationResult> Details)>();
            var telegramMessages = new List<string>();
            int nTicks = _engine.AbsNTicks;

            bool isRecovering;
            lock (_rtLock)
            {
                isRecovering = _isRecovering;
                if (_isRecovering) _isRecovering = false;
            }

            foreach (var symbol in new[] { "TXF", "MXF" })
            {
                var trades = tradesSnapshot[symbol];
                if (trades.Count == 0) continue;

                // 背景單一寫入者 (Disruptor) 直接操作真正的狀態物件
                var state = stateSource[symbol][activeSession];

                var quantParams = _engine.LoadQuantParams(symbol, _currentTargetDays);

                var absDetails = new List<CompletedTrigger>();
                
                int runningMax = state.RunningMax;
                int runningMin = state.RunningMin;
                int? lastPrice = state.LastPrice;
                double lastCheckTimeH = state.LastCheckTimeH;
                double lastCheckTimeB = state.LastCheckTimeB;
                int scanIdx = state.ScanIdx;

                if (scanIdx == 0)
                {
                    lock (_rtLock)
                    {
                        _rtTriggers[symbol][activeSession].Clear();
                        _rtCompletedDetails[symbol][activeSession].Clear();
                    }
                }

                // 增量時序對比運算與單一寫入者狀態聚合 (Disruptor / Single Writer)
                int currentTradesCount = trades.Count;
                for (int i = scanIdx; i < currentTradesCount; i++)
                {
                    var tick = trades[i];
                    int price = tick.Price;
                    double tVal = tick.TimeVal;
                    TradeSide side = tick.Side;
                    string tStr = tick.Time; // Zero Alloc property GetTimeStr
                    
                    // --- 1. 狀態累計 (原 COM Thread 移交過來) ---
                    state.Count++;
                    state.SumPrice += price;
                    
                    if (price > state.DayMax)
                    {
                        state.DayMax = price;
                        state.MaxTime = tStr;
                    }
                    if (price < state.DayMin)
                    {
                        state.DayMin = price;
                        state.MinTime = tStr;
                    }

                    if (side == TradeSide.Outer)
                    {
                        state.OuterCount++;
                        state.FirstOuterTime ??= tVal;
                        state.LastOuterTime = tVal;
                    }
                    else if (side == TradeSide.Inner)
                    {
                        state.InnerCount++;
                        state.FirstInnerTime ??= tVal;
                        state.LastInnerTime = tVal;
                    }

                    if (symbol == "MXF")
                    {
                        _lastMxfPrice = price;
                        _lastMxfTime = tStr;
                    }
                    else if (symbol == "TXF")
                    {
                        _lastTxfPrice = price;
                    }

                    // --- 2. 原本的極值掃描 ---
                    bool isTrigH = false;
                    bool isTrigB = false;

                    if (price > runningMax)
                    {
                        runningMax = price;
                        isTrigH = true;
                    }
                    else if (price == runningMax)
                    {
                        if ((lastPrice.HasValue && lastPrice.Value < price) || (tVal - lastCheckTimeH >= 30.0))
                        {
                            isTrigH = true;
                        }
                    }

                    if (price < runningMin)
                    {
                        runningMin = price;
                        isTrigB = true;
                    }
                    else if (price == runningMin)
                    {
                        if ((lastPrice.HasValue && lastPrice.Value > price) || (tVal - lastCheckTimeB >= 30.0))
                        {
                            isTrigB = true;
                        }
                    }

                    if (isTrigH) lastCheckTimeH = tVal;
                    if (isTrigB) lastCheckTimeB = tVal;

                    if (isTrigH)
                    {
                        lock (_rtLock)
                        {
                            _rtTriggers[symbol][activeSession].Add(new PendingTrigger(i, price, true, false, runningMax, runningMin, state.Clone()));
                        }
                    }
                    if (isTrigB)
                    {
                        lock (_rtLock)
                        {
                            _rtTriggers[symbol][activeSession].Add(new PendingTrigger(i, price, false, true, runningMax, runningMin, state.Clone()));
                        }
                    }

                    lastPrice = price;
                }

                // 回寫更新快取的 O(1) Scan 游標
                lock (_rtLock)
                {
                    state.RunningMax = runningMax;
                    state.RunningMin = runningMin;
                    state.LastPrice = lastPrice;
                    state.LastCheckTimeH = lastCheckTimeH;
                    state.LastCheckTimeB = lastCheckTimeB;
                    state.ScanIdx = currentTradesCount;
                }

                // 更新完畢後，提供一份最新的 Snapshot 供主 UI 使用，並抓取當前 DayMax/DayMin 進行後續運算
                int dayMax = state.DayMax;
                int dayMin = state.DayMin;
                stateSnapshot[symbol] = state.Clone();

                List<PendingTrigger> currentTriggers;
                lock (_rtLock)
                {
                    currentTriggers = [.. _rtTriggers[symbol][activeSession]];
                }

                foreach (var item in currentTriggers)
                {
                    int i = item.Index;
                    int price = item.Price;
                    bool isTrigH = item.IsTrigH;
                    bool isTrigB = item.IsTrigB;
                    int rMax = item.RunningMax;
                    int rMin = item.RunningMin;
                    double tVal = trades[i].TimeVal;

                    if (isTrigH)
                    {
                        ProcessTriggerBlock(item, true, price, tVal, rMax, rMin, symbol, activeSession, currentTradesCount, trades, tradesSnapshot, nTicks);
                    }

                    if (isTrigB)
                    {
                        ProcessTriggerBlock(item, false, price, tVal, rMax, rMin, symbol, activeSession, currentTradesCount, trades, tradesSnapshot, nTicks);
                    }
                }

                lock (_rtLock)
                {
                    foreach (var cached in _rtCompletedDetails[symbol][activeSession])
                    {
                        absDetails.Add(cached);
                    }
                }

                absDetails.Sort((x, y) => x.TVal.CompareTo(y.TVal));
                
                var filteredPre = new List<CompletedTrigger>();
                var seen = new HashSet<(bool IsTop, int Price)>();
                foreach (var d in absDetails)
                {
                    var key = (d.IsTrigH, d.PriceVal);
                    if (seen.Add(key))
                    {
                        filteredPre.Add(d);
                    }
                }

                // 實時第二階段：Immutable 歷史速差重算 (O(1))
                var rtLastNetSpeedsTop = new Dictionary<string, double?> { { "TXF", null }, { "MXF", null } };
                var rtLastNetSpeedsBot = new Dictionary<string, double?> { { "TXF", null }, { "MXF", null } };
                
                var otherSym = symbol == "TXF" ? "MXF" : "TXF";
                string baseSym = symbol.Contains("TXF") ? "TXF" : "MXF";
                
                string FormatNet(string sym, double? currVal, Dictionary<string, double?> lastNetSpeeds)
                {
                    if (currVal == null) return "--       ";
                    string baseStr = $"{currVal.Value:+0.0000;-0.0000;+0.0000}s";
                    string suffix = "";
                    double? prevVal = lastNetSpeeds.ContainsKey(sym) ? lastNetSpeeds[sym] : null;
                    if (prevVal.HasValue && Math.Abs(currVal.Value - prevVal.Value) > 0.00001)
                    {
                        if (currVal.Value > prevVal.Value)
                            suffix = currVal.Value > 0 ? " 多速增" : " 空速減";
                        else
                            suffix = currVal.Value < 0 ? " 空速增" : " 多速減";
                    }
                    lastNetSpeeds[sym] = currVal;
                    if (string.IsNullOrEmpty(suffix)) suffix = "       ";
                    return baseStr + suffix;
                }

                var filteredDetails = new List<SimulationResult>();
                foreach (var d in filteredPre)
                {
                    string prefix;
                    if (d.IsTrigH) prefix = (d.PriceVal == dayMax) ? "時段最高" : (d.StatusOnly.Contains("未達標") ? "曾未達標最高" : "曾達標最高");
                    else prefix = (d.PriceVal == dayMin) ? "時段最低" : (d.StatusOnly.Contains("未達標") ? "曾未達標最低" : "曾達標最低");
                    
                    string displayTitle = prefix + d.StatusOnly;

                    if (!d.Post.HasValue)
                    {
                        var copy = new SimulationResult
                        {
                            Type = d.IsTrigH ? "最高" : "最低",
                            DisplayTitle = displayTitle,
                            BestATime = d.ATime,
                            BestAPrice = d.PriceVal,
                            TrigTime = d.TrigTime,
                            TrigPrice = d.TrigPrice.ToString(),
                            Pre = d.Pre.HasValue ? $"{d.Pre.Value:F4}s" : "N/A",
                            Post = "N/A",
                            StopLossDisplay = "N/A"
                        };
                        filteredDetails.Add(copy);
                        continue;
                    }

                    string speedStr = "";
                    if (d.BaseNet.HasValue && d.OtherNet.HasValue)
                    {
                        var lastSpeeds = d.IsTrigH ? rtLastNetSpeedsTop : rtLastNetSpeedsBot;
                        string baseNetStr = FormatNet(baseSym, d.BaseNet, lastSpeeds);
                        string otherNetStr = FormatNet(otherSym, d.OtherNet, lastSpeeds);
                        double aTimeRaw = ExtremeSignalAppCS.Helper.TimeParser.ParseTime(d.ATime);
                        if (activeSession == "夜盤" && aTimeRaw <= 18000.0) aTimeRaw += 86400.0;
                        int aIdx = Math.Max(0, _engine.FindTickCountByTime(trades, aTimeRaw) - 1);

                        TradeSide preSide = d.IsTrigH ? TradeSide.Outer : TradeSide.Inner;
                        TradeSide postSide = d.IsTrigH ? TradeSide.Inner : TradeSide.Outer;

                        int windowSize = nTicks;
                        int preTicksCollected = 0, preVolume = 0;
                        for (int i = aIdx; i >= 0; i--)
                        {
                            if (trades[i].Side == preSide)
                            {
                                preTicksCollected++;
                                preVolume += trades[i].Qty;
                                if (preTicksCollected >= windowSize) break;
                            }
                        }

                        int postTicksCollected = 0, postVolume = 0;
                        for (int i = aIdx; i <= d.BIdx && i < trades.Count; i++)
                        {
                            if (trades[i].Side == postSide)
                            {
                                postTicksCollected++;
                                postVolume += trades[i].Qty;
                                if (postTicksCollected >= windowSize) break;
                            }
                        }

                        int windowICnt = d.IsTrigH ? postVolume : preVolume;
                        int windowOCnt = d.IsTrigH ? preVolume : postVolume;

                        string op = windowICnt > windowOCnt ? ">" : (windowICnt < windowOCnt ? "<" : "=");
                        string ioCompare = $" 內:{windowICnt} {op} 外:{windowOCnt}";

                        speedStr = $"    成交速度: {d.DStr} | 大台速差: {(baseSym == "TXF" ? baseNetStr : otherNetStr)}  小台速差: {(baseSym == "MXF" ? baseNetStr : otherNetStr)} | 均價:{d.AvgPri}{ioCompare}";
                    }

                    var res = new SimulationResult
                    {
                        Type = d.IsTrigH ? "做空" : "做多",
                        DisplayTitle = displayTitle,
                        BestATime = d.ATime,
                        BestAPrice = d.PriceVal,
                        TrigTime = d.TrigTime,
                        TrigPrice = d.TrigPrice.ToString(),
                        Pre = d.Pre.HasValue ? $"{d.Pre.Value:F4}-{d.PreVol}" : "N/A",
                        Post = $"{d.Post.Value:F4}-{d.PostVol}",
                        AmpVal = d.AmpVal,
                        BIndex = d.BIdx,
                        ObsN = nTicks,
                        StopLossDisplay = "N/A"
                    };
                    res.Tags.Add(speedStr);
                    
                    filteredDetails.Add(res);
                }

                // 檢測是否需要觸發 TG 推播與去重防線
                foreach (var d in filteredDetails)
                {
                    if (d.Post == "N/A") continue;

                    string speedInfo = d.Tags.FirstOrDefault() ?? "";
                    var (isNormal, isContradiction) = TradingEngine.ClassifyTrigger(d.DisplayTitle, speedInfo);

                    if (isNormal || isContradiction)
                    {
                        var notifyKey = (symbol, activeSession, d.Type, d.BestAPrice, d.BestATime);
                        if (_rtNotifiedKeys.Add(notifyKey))
                        {
                            string side = d.Type == "做空" ? "top" : "bottom";
                            string zoneStr = GetZoneStr(side, d.BestATime, d.BestAPrice, activeSession, quantParams);

                            string displayTitle = isContradiction ? d.DisplayTitle.Replace("未達標", "矛盾") : d.DisplayTitle;
                            string dirText = d.Type == "做空" ? $"做空  {displayTitle}" : $"做多  {displayTitle}";
                            string msgTitle = isContradiction ? "【極值矛盾】" : "【極值達標】";
                            string msg = $"{msgTitle}{symbol} {activeSession}\n" +
                                         $"方向：{dirText}\n" +
                                         $"A點時間：{d.BestATime}\n" +
                                         $"A點價：{d.BestAPrice}\n" +
                                         $"B點時間：{d.TrigTime}\n" +
                                         $"觸發價：{d.TrigPrice}\n" +
                                         $"進場/停損：{zoneStr}\n" +
                                         $"當下振幅：{d.AmpVal}";
                            
                            if (!isRecovering)
                            {
                                telegramMessages.Add(msg);
                            }
                        }
                    }
                }

                // 由於 _rtTriggers 不再儲存 SimulationResult，我們直接提供 filteredDetails 給 UI 快照
                currentStatusSnapshot.Add((symbol, activeSession, dayMin, dayMax, new List<SimulationResult>(filteredDetails)));
            }

            // ═══ 在背景執行緒非同步組裝 UI 快照物件 ═══
            if (!stateSnapshot.TryGetValue("MXF", out var mxfState))
            {
                mxfState = new TradingState();
            }
            if (!stateSnapshot.TryGetValue("TXF", out var txfState))
            {
                txfState = new TradingState();
            }

            var (maxStr, minStr, ampStr) = TradingEngine.FormatExtremeInfo(mxfState, FormatExtremeTime);

            // 計算即時速差文字與顏色
            var (_, _, dT) = TradingEngine.CalcSideSpeedFromState(txfState);
            var (_, _, dM) = TradingEngine.CalcSideSpeedFromState(mxfState);

            double? netT = TradingEngine.CalcNetSpeedFromState(txfState);
            double? netM = TradingEngine.CalcNetSpeedFromState(mxfState);

            string netTxfStr = netT.HasValue ? $"| 大臺速差: {netT.Value:+0.0000;-0.0000;+0.0000}s" : "| 大臺速差: --";
            string netTxfColor = netT.HasValue ? (netT.Value > 0 ? "#EB4B4B" : netT.Value < 0 ? "#28A745" : "Gray") : "Gray";

            string netMxfStr = netM.HasValue ? $"| 小臺速差: {netM.Value:+0.0000;-0.0000;+0.0000}s" : "| 小臺速差: --";
            string netMxfColor = netM.HasValue ? (netM.Value > 0 ? "#EB4B4B" : netM.Value < 0 ? "#28A745" : "Gray") : "Gray";

            var (consensusStr, consensusColor) = TradingEngine.CalcConsensus(dT, dM);

            // 產生即時極值詳情報告文字 (與離線日誌完全同構)
            string rtReport = "";
            if (!tradesSnapshot.TryGetValue("TXF", out var txfTradesRt))
            {
                txfTradesRt = [];
            }
            if (!tradesSnapshot.TryGetValue("MXF", out var mxfTradesRt))
            {
                mxfTradesRt = [];
            }

            var txfDetailsRt = currentStatusSnapshot.FirstOrDefault(x => x.Symbol == "TXF").Details ?? [];
            var mxfDetailsRt = currentStatusSnapshot.FirstOrDefault(x => x.Symbol == "MXF").Details ?? [];

            // 快照 Tick 總數作為報告生成的絕對邊界
            int txfSnapshotCount = txfTradesRt.Count;
            int mxfSnapshotCount = mxfTradesRt.Count;

            if (txfSnapshotCount > 0)
            {
                rtReport += "═══ 大臺即時極值行情 ═══" + GenerateRealtimeReportStr("TXF", activeSession, txfTradesRt, txfDetailsRt, _engine.LoadQuantParams("TXF", _currentTargetDays), stateSnapshot, txfSnapshotCount);
            }
            if (mxfSnapshotCount > 0)
            {
                if (!string.IsNullOrEmpty(rtReport)) rtReport += "\n";
                rtReport += "═══ 小臺即時極值行情 ═══" + GenerateRealtimeReportStr("MXF", activeSession, mxfTradesRt, mxfDetailsRt, _engine.LoadQuantParams("MXF", _currentTargetDays), stateSnapshot, mxfSnapshotCount);
            }

            // 背景分 K 聚合與 K棒 停損狀態機運算 (超大 CPU 負載完全隔離 + 1000ms 降頻保護)
            double nowMsHeavy = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            if (nowMsHeavy - _lastHeavyCalcTimeMs >= 1000)
            {
                _lastHeavyCalcTimeMs = nowMsHeavy;
                
                int currentMxfTradesCount = mxfTradesRt.Count;
                var (klineData, breakouts) = _engine.CalcKlineData(
                    activeSession, mxfTradesRt,
                    [.. txfDetailsRt.Select(d => ConvertToSimulationResultRaw(d))],
                    [.. mxfDetailsRt.Select(d => ConvertToSimulationResultRaw(d))],
                    _currentKlineInterval,
                    currentMxfTradesCount
                );

                var simulationResults = _engine.CalcSimulationResults(
                    activeSession, mxfTradesRt, klineData, _currentObsN, true, 
                    (dynN) => { Dispatcher.BeginInvoke(new System.Action(() => { lblObsN.Content = $"觀察N: {dynN}"; })); },
                    currentMxfTradesCount
                );

                // 背景瞬間自動下單檢查 (記憶體產生的瞬間)
                if (simulationResults != null && simulationResults.Count > 0)
                {
                    foreach (var item in simulationResults)
                    {
                        if (item.Type == null || item.Tags.Contains("history") || item.Tags.Contains("annotation"))
                            continue;

                        var key = item.ConfirmedKey;
                        AutoTradeState state;
                        if (!_autoTradeStates.TryGetValue(key, out state!))
                        {
                            // ObsN 可能因 Tick 增量而變化，以 Price+ATime 搜尋既有狀態，防止建立新條目覆蓋掉已更新的失敗/成交狀態
                            var existingKv = _autoTradeStates.FirstOrDefault(kv =>
                                kv.Key.Price == key.Price &&
                                kv.Key.ATime == key.ATime &&
                                kv.Value.IsTriggered);
                            if (existingKv.Key != default)
                            {
                                state = existingKv.Value;
                                _autoTradeStates.TryAdd(key, state); // 以新 Key 也指向同一個狀態物件
                            }
                            else
                            {
                                state = _autoTradeStates.GetOrAdd(key, k => new AutoTradeState());
                            }
                        }

                        // 同步快取狀態至當前 item (防止重新計算時被預設值覆蓋)
                        item.TradeStatus = state.TradeStatus;
                        item.TakeProfitPrice = state.TakeProfitPrice;
                        item.OrderedSymbol = state.OrderedSymbol;
                        item.IsTriggered = state.IsTriggered;
                        item.OrderNo = state.OrderNo;
                        item.CloseOrderNo = state.CloseOrderNo;
                        if (state.StopLossPrice != 0)
                        {
                            item.StopLossPrice = state.StopLossPrice;
                            // 若引擎判定已破或顯示字串帶有"已破"，則強制加上"(已破)"標籤並同步更新快取，防止被舊狀態覆蓋
                            if (item.IsBroken || (item.StopLossDisplay != null && item.StopLossDisplay.Contains("已破")))
                            {
                                item.StopLossDisplay = $"{state.StopLossPrice}(已破)";
                                state.StopLossDisplay = item.StopLossDisplay;
                            }
                            else
                            {
                                item.StopLossDisplay = state.StopLossDisplay;
                            }
                        }

                        // 規則 2: N 數值一定要大於 10 (暫時放寬為大於0，測試用)
                        if (item.ObsN <= 0)
                        {
                            if (item.TradeStatus == "未啟用下單")
                            {
                                item.TradeStatus = "N<=10 (不再下單)";
                                state.TradeStatus = "N<=10 (不再下單)";
                            }
                            continue;
                        }

                        // 使用 Price+ATime 檢查是否已處理過（容許 ObsN 變化）
                        bool alreadyProcessed = _bgProcessedKeys.Contains(key) ||
                            _bgProcessedKeys.Any(k => k.Price == key.Price && k.ATime == key.ATime);
                        if (!alreadyProcessed)
                        {
                            _bgProcessedKeys.Add(key);
                            ProcessBackgroundAutoTrade(item);
                        }
                    }
                }

                _lastKlineData = klineData;
                _lastSimulationResults = simulationResults;

                // 產生共用統計快取給 UI
                var sharedResultsMap = ComputeAllIntervalResults(activeSession, mxfTradesRt, [.. txfDetailsRt.Select(d => ConvertToSimulationResultRaw(d))], [.. mxfDetailsRt.Select(d => ConvertToSimulationResultRaw(d))], _currentObsN, currentMxfTradesCount);
                _lastSharedResultsMap = sharedResultsMap;
            }

            return new Dictionary<string, object>
            {
                { "current_status_snapshot", currentStatusSnapshot },
                { "telegram_messages", telegramMessages },
                { "active_session", activeSession },
                { "realtime_extreme_report", rtReport },
                { "kline_data", _lastKlineData },
                { "simulation_results", _lastSimulationResults },
                { "extreme_snapshot", new Dictionary<string, string>
                    {
                        { "max_info", maxStr },
                        { "min_info", minStr },
                        { "amp_info", ampStr },
                        { "consensus_str", consensusStr },
                        { "consensus_color", consensusColor },
                        { "net_txf_str", netTxfStr },
                        { "net_txf_color", netTxfColor },
                        { "net_mxf_str", netMxfStr },
                        { "net_mxf_color", netMxfColor }
                    }
                },
                { "shared_results_map", _lastSharedResultsMap! }
            };
        }

        private static SimulationResult ConvertToSimulationResultRaw(SimulationResult display)
        {
            int prevHigh = display.PrevHigh;
            int prevLow = display.PrevLow;

            return new SimulationResult
            {
                DisplayTitle = display.DisplayTitle,
                BestATime = display.BestATime,
                BestAPrice = display.BestAPrice,
                TrigTime = display.TrigTime,
                TrigPrice = display.TrigPrice,
                Pre = display.Pre,
                Post = display.Post,
                PrevHigh = prevHigh,
                PrevLow = prevLow,
                BIndex = display.BIndex,
                ObsN = display.ObsN,
                StopLossPrice = display.DisplayTitle.Contains("最高") ? prevHigh : prevLow,
                Tags = display.Tags != null ? [.. display.Tags] : []
            };
        }

        private string _latestTriggerLog = "";

        // ==================== 4. 主執行緒 UI 渲染與著色 (ApplyRealtimeAnalysisUI) ====================

        private void ApplyRealtimeAnalysisUI(Dictionary<string, object> result)
        {
            if (result.TryGetValue("current_status_snapshot", out var rtSnapObj) && rtSnapObj is List<(string Symbol, string Session, int DayMin, int DayMax, List<SimulationResult> Details)> rtSnapList)
            {
                _lastRtStatusSnapshot = rtSnapList;
            }

            var snap = (Dictionary<string, string>)result["extreme_snapshot"];
            
            // 1. 渲染頂部極值、速差及多空共識
            runMaxInfo.Text = snap["max_info"];
            runMinInfo.Text = snap["min_info"];
            runAmpInfo.Text = snap["amp_info"];
            lblConsensusDir.Text = snap["consensus_str"];
            SetWidgetStyleLazy(lblConsensusDir, snap["consensus_color"]);
            lblTxfNetSpeed.Text = snap["net_txf_str"];
            SetWidgetStyleLazy(lblTxfNetSpeed, snap["net_txf_color"]);
            lblMxfNetSpeed.Text = snap["net_mxf_str"];
            SetWidgetStyleLazy(lblMxfNetSpeed, snap["net_mxf_color"]);

            // 2. TG 推播發送 (先處理以更新最新觸發狀態)
            var tgMsgs = (List<string>)result["telegram_messages"];
            foreach (var msg in tgMsgs)
            {
                PushTelegramMessage(msg);
            }

            // 3. 智慧操作感知型降頻節流 (Interaction-Aware Throttle) 著色排版
            if (result.TryGetValue("realtime_extreme_report", out var reportObj) && reportObj is string rtReport && !string.IsNullOrEmpty(rtReport))
            {
                if (!App.Current.Resources.Contains("_system_logs"))
                {
                    App.Current.Resources["_system_logs"] = new List<string>();
                }
                var systemLogs = (List<string>)App.Current.Resources["_system_logs"];

                // 判斷交易員是否正在高頻滑動十字游標或縮放 (滑動時 Textbox 重繪延遲改為 2000ms，以 GPU 行情為絕對優先)
                bool isUserInteracting = klineChart.IsMouseOver;
                double renderInterval = isUserInteracting ? 2000.0 : 250.0;
                double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;

                var telegramMessages = (List<string>)result["telegram_messages"];

                string logsStr;
                lock (_logLock)
                {
                    logsStr = string.Join("\n", systemLogs);
                }
                string tgInfo = string.IsNullOrEmpty(_latestTriggerLog) ? "" : _latestTriggerLog + "\n\n";
                string fullContent = "═══ 系統即時監控日誌 ═══\n" + logsStr + "\n\n" + tgInfo + rtReport;

                if (fullContent != _lastRenderedContent && (nowMs - _lastTxtRenderTime >= renderInterval || telegramMessages.Count > 0))
                {
                    _lastRenderedContent = fullContent;
                    _lastTxtRenderTime = nowMs;

                    // O(1) 零開銷增量染色插入
                    LogHighlighter.AppendLog(txtOutput, fullContent, clear: true);
                }
            }

            // 4. 刷新 K線與圖表 (O(1) / O(N) 零 CPU 聚合開銷)
            var klineData = (List<KlineBar>)result["kline_data"];
            UpdateKlineViews(klineData);
            klineChart.UpdateCandles(klineData, currentTickTimeStr: _lastMxfTime);

            // 圖表資料更新完成後，才重新對焦白框（必須在 UpdateCandles 之後）
            if (_lockedFocusTime != null)
            {
                RefocusChartOnTime(_lockedFocusTime, _lockedFocusPrice, false);
            }

            // 5. 派發共用資料至未破分K監控與區間統計 (純渲染)
            if (result.TryGetValue("shared_results_map", out var sharedObj) && sharedObj is Dictionary<int, List<SimulationResult>> sharedMap && sharedMap != null)
            {
                UpdateIntervalStatsUI(sharedMap);
                string priceStr = _lastMxfPrice.HasValue ? _lastMxfPrice.Value.ToString() : "N/A";
                wndUnbrokenK.UpdateFromSharedData(sharedMap, priceStr, _lastMxfTime);
            }

            // 6. 差量更新 DataGrid 行情與選取反白保留
            var simulationResults = (List<SimulationResult>)result["simulation_results"];
            UpdateObserverViews(simulationResults);

            // 7. 更新底部狀態計數面板
            RefreshInfoPanel();
        }

        public void PushTelegramMessage(string msg)
        {
            // 確保必須在 UI 執行緒執行 (因讀取 chkTelegram.IsChecked)
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PushTelegramMessage(msg)));
                return;
            }

            string cleanMsg = msg.Replace('\n', ' ');
            if (chkTelegram.IsChecked == true)
            {
                _latestTriggerLog = $">>>> 最新觸發推播: {cleanMsg}";
                _tgService.PushMessage(msg);
            }
            else
            {
                _latestTriggerLog = ""; // 未啟用 TG 時不再顯示
            }
        }

        private string? _lockedFocusTime;
        private int? _lockedFocusPrice;
        private double _lockedFocusRelativePos = 1.0;
        private int _lockedFocusDirection = 0; // 1=多方, -1=空方, 0=無

        public void ClearChartCrosshair()
        {
            _lockedFocusTime = null;
            _lockedFocusPrice = null;
            _lockedFocusRelativePos = 1.0;
            _lockedFocusDirection = 0;
            klineChart.ClearCrosshair();
        }

        /// <summary>
        /// 將圖表對焦到指定時間的 K 棒，並繪製停損黃線。
        /// </summary>
        /// <param name="timeStr">事件時間字串 (HH:MM:SS)</param>
        /// <param name="price">事件價格 (白色橫線)</param>
        /// <param name="direction">趨勢方向：1=多方, -1=空方, 0=無</param>
        public void FocusChartOnTime(string timeStr, int? price = null, int direction = 0)
        {
            _lockedFocusTime = timeStr;
            _lockedFocusPrice = price;
            _lockedFocusDirection = direction;
            RefocusChartOnTime(timeStr, price, true);
        }

        private void RefocusChartOnTime(string timeStr, int? price = null, bool snapView = true)
        {
            if (_klineCollection == null || _klineCollection.Count == 0 || string.IsNullOrEmpty(timeStr)) return;

            // 先清除舊的高亮，避免切換層級時若找不到匹配時間，會殘留舊的隨機 Index
            klineChart.ClearHighlightOnly();

            // 解析事件時間為「總秒數」（支援 HH:MM:SS 或 HHMMSS 格式）
            string cleanTime = timeStr.Replace(":", "");
            if (cleanTime.Length < 4) return;

            int targetSecs;
            if (cleanTime.Length >= 6 && int.TryParse(cleanTime[..6], out int hmsVal))
            {
                // 有秒數：HHMMSS
                int h = hmsVal / 10000;
                int m = (hmsVal / 100) % 100;
                int s = hmsVal % 100;
                targetSecs = h * 3600 + m * 60 + s;
            }
            else if (int.TryParse(cleanTime[..4], out int hmVal))
            {
                // 無秒數：HHMM（秒數視為 00）
                targetSecs = (hmVal / 100) * 3600 + (hmVal % 100) * 60;
            }
            else
            {
                return;
            }

            List<int> matchedIndices = new List<int>();

            // 收集所有符合時間的 K 棒索引
            // K 棒的 TimeLabel 格式為 "HH:MM~HH:MM"，轉為秒數後以 [startSecs, endSecs) 半開區間判定
            for (int i = 0; i < _klineCollection.Count; i++)
            {
                var parts = _klineCollection[i].TimeLabel.Split('~');
                if (parts.Length == 2)
                {
                    var p0 = parts[0].Split(':');
                    var p1 = parts[1].Split(':');
                    if (p0.Length == 2 && p1.Length == 2 && 
                        int.TryParse(p0[0], out int sH) && int.TryParse(p0[1], out int sM) &&
                        int.TryParse(p1[0], out int eH) && int.TryParse(p1[1], out int eM))
                    {
                        int startSecs = sH * 3600 + sM * 60;
                        int endSecs = eH * 3600 + eM * 60;
                        
                        // 半開區間 [start, end)：事件時間必須 >= 起始秒 且 < 結束秒
                        bool inRange = (startSecs <= endSecs)
                            ? (targetSecs >= startSecs && targetSecs < endSecs)
                            : (targetSecs >= startSecs || targetSecs < endSecs);
                        
                        if (inRange)
                        {
                            matchedIndices.Add(i);
                        }
                    }
                }
            }

            if (matchedIndices.Count > 0)
            {
                // 從所有匹配的 K 棒中，找出相對位置與上次最接近的那根
                // 這確保了在切換時間週期時，即使陣列長度改變，依然會精準對齊同一天的同一根 K 棒
                int bestIndex = matchedIndices[^1]; // 預設拿最後一筆
                double minDiff = double.MaxValue;
                
                foreach (int idx in matchedIndices)
                {
                    double relPos = (double)idx / _klineCollection.Count;
                    double diff = Math.Abs(relPos - _lockedFocusRelativePos);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestIndex = idx;
                    }
                }

                // 記錄新的相對位置供下次切換使用
                _lockedFocusRelativePos = (double)bestIndex / _klineCollection.Count;

                if (snapView)
                    klineChart.FocusCandle(bestIndex, price);
                else
                    klineChart.SetHighlightIndexOnly(bestIndex, price);

                // 計算停損價：比較當時 K 棒與前一根 K 棒，若無上一根則以當時 K 棒為準
                if (_lockedFocusDirection != 0 && bestIndex >= 0)
                {
                    var currBar = klineChart.GetCandle(bestIndex);
                    
                    if (currBar != null)
                    {
                        double stopLoss;
                        if (bestIndex >= 1)
                        {
                            var prevBar = klineChart.GetCandle(bestIndex - 1);
                            if (prevBar != null)
                            {
                                if (_lockedFocusDirection == 1)
                                    stopLoss = Math.Min(currBar.Low, prevBar.Low);
                                else
                                    stopLoss = Math.Max(currBar.High, prevBar.High);
                            }
                            else
                            {
                                stopLoss = _lockedFocusDirection == 1 ? currBar.Low : currBar.High;
                            }
                        }
                        else
                        {
                            // 第 0 根，沒有上一根
                            stopLoss = _lockedFocusDirection == 1 ? currBar.Low : currBar.High;
                        }
                        klineChart.SetStopLossPrice(stopLoss, _lockedFocusDirection);
                    }
                }
                else
                {
                    klineChart.SetStopLossPrice(null, 0);
                }
            }
        }

        private void DiffMerge<T>(ObservableCollection<T> target, List<T> source, Action<T, T> copyFields)
        {
            int oldLen = target.Count;
            int newLen = source.Count;

            if (oldLen == 0 && newLen == 0) return;

            int minLen = Math.Min(oldLen, newLen);
            for (int i = 0; i < minLen; i++)
            {
                copyFields(target[i], source[i]);
            }

            if (newLen > oldLen)
            {
                for (int i = oldLen; i < newLen; i++)
                {
                    target.Add(source[i]);
                }
            }
            else if (newLen < oldLen)
            {
                for (int i = oldLen - 1; i >= newLen; i--)
                {
                    target.RemoveAt(i);
                }
            }
        }

        private void UpdateKlineViews(List<KlineBar> klineData)
        {
            // 差量合併 K線，保留 DataGrid 的 SelectIndex 反白
            DiffMerge(_klineCollection, klineData, (t, s) =>
            {
                t.TimeLabel = s.TimeLabel;
                t.High = s.High;
                t.Low = s.Low;
                t.Open = s.Open;
                t.Close = s.Close;
                t.Signals = s.Signals;
                t.BreakHigh = s.BreakHigh;
                t.BreakLow = s.BreakLow;
                t.Tag = s.Tag;
            });

            // 若沒有手動反白，自動滾動到最末行
            if (dgKline.SelectedIndex == -1 && _klineCollection.Count > 0)
            {
                dgKline.ScrollIntoView(_klineCollection[^1]);
            }

            // ═══ K線轉換自帶參數邏輯 ═══
            if (klineData.Count >= 2)
            {
                string currentKlineTime = klineData[^1].TimeLabel;
                if (_lastAutofillKlineTime != currentKlineTime)
                {
                    _lastAutofillKlineTime = currentKlineTime;
                    
                    var prevKline = klineData[^2];
                    int prevHigh = (int)prevKline.High;
                    int prevLow = (int)prevKline.Low;

                    // 觀察 K 低：自帶前分K最低
                    txtObsHigh.Text = prevLow.ToString();
                    _engine._obs_high_entry_price = prevLow;

                    // 觀察 K 高：自帶前分K最高
                    txtObsLow.Text = prevHigh.ToString();
                    _engine._obs_low_entry_price = prevHigh;
                    
                    // AppendLog($"【自動觀察】分 K 轉換！已自動載入前分 K 最高: {prevHigh} / 最低: {prevLow}。");
                }
            }

        }

        private void UpdateObserverViews(List<SimulationResult> simulationResults)
        {
            int oldCount = _obsCollection.Count;
            RefreshObserverComboboxes();

            SimulationResult? prevSelected = dgObserver.SelectedItem as SimulationResult;
            string? prevSelectedTime = prevSelected?.BestATime;
            string? prevSelectedPrice = prevSelected?.StopLossPrice.ToString();
            bool prevHighlighted = prevSelected?.IsTargetPriceHighlighted ?? false;

            // 在更新列表前，先清空舊的反白狀態，避免被 DiffMerge 殘留導致多個項目同時被反白
            foreach (var obs in _obsCollection)
            {
                obs.IsTargetPriceHighlighted = false;
            }

            DiffMerge(_obsCollection, simulationResults, (t, s) =>
            {
                t.Type = s.Type;
                t.DisplayTitle = s.DisplayTitle;
                t.BestATime = s.BestATime;
                t.BestAPrice = s.BestAPrice;
                t.TrigTime = s.TrigTime;
                t.TrigPrice = s.TrigPrice;
                t.Pre = s.Pre;
                t.Post = s.Post;
                t.StopLossDisplay = s.StopLossDisplay;
                t.IsBroken = s.IsBroken;
                t.StopLossPrice = s.StopLossPrice;
                t.ObsEntry = s.ObsEntry;
                t.PrevHigh = s.PrevHigh;
                t.PrevLow = s.PrevLow;
                t.BIndex = s.BIndex;
                t.ObsN = s.ObsN;
                t.UpdateTags(s.Tags);
                // 複製自動交易屬性
                t.TradeStatus = s.TradeStatus;
                t.TakeProfitPrice = s.TakeProfitPrice;
                t.OrderedSymbol = s.OrderedSymbol;
                t.IsTriggered = s.IsTriggered;
                t.CloseOrderNo = s.CloseOrderNo;
            });

            // 處理在背景下單時非同步回報的 OrderNo 綁定 (解決競態問題)
            foreach (var item in _obsCollection)
            {
                if (item.TradeStatus == "已送出" && string.IsNullOrEmpty(item.OrderNo))
                {
                    string? matchOrderNo = null;
                    string matchStatus = "委託中";
                    lock (_unboundOrderReplies)
                    {
                        var matchKey = _unboundOrderReplies.FirstOrDefault(kv => 
                            kv.Value.Price == item.BestAPrice && 
                            kv.Value.Type == item.Type && 
                            kv.Value.Symbol == item.OrderedSymbol);
                        
                        if (matchKey.Key != null)
                        {
                            matchOrderNo = matchKey.Key;
                            matchStatus = matchKey.Value.Status;
                        }
                    }

                    if (matchOrderNo != null)
                    {
                        item.OrderNo = matchOrderNo;
                        item.TradeStatus = matchStatus;
                        
                        AppendLog($"【自動交易】已成功將暫存的委託書號 {matchOrderNo} 綁定至剛載入的極值列：{item.DisplayTitle} (狀態: {matchStatus})");
                        
                        lock (_unboundOrderReplies)
                        {
                            _unboundOrderReplies.Remove(matchOrderNo);
                        }

                        if (matchStatus == "委託中")
                        {
                            StartOrderTimeoutMonitor(item, matchOrderNo);
                        }
                    }
                }
            }

            RecalculateObserverCheckableStates();

            if (prevSelectedPrice != null && string.IsNullOrEmpty(_targetHighlightStopLossPrice))
            {
                var match = _obsCollection.FirstOrDefault(o => o.BestATime == prevSelectedTime && o.StopLossPrice.ToString() == prevSelectedPrice);
                
                // 當切換分 K 時，BestATime 可能會改變，此時退而求其次只比對停損價
                if (match == null)
                {
                    match = _obsCollection.FirstOrDefault(o => !o.IsBroken && o.StopLossPrice.ToString() == prevSelectedPrice);
                }

                if (match != null)
                {
                    match.IsTargetPriceHighlighted = prevHighlighted;
                    _isRestoringSelection = true;
                    dgObserver.SelectedItem = match;
                    // 背景更新還原選擇時，不強制 ScrollIntoView，讓使用者能自由滾動查看其他資料
                    _isRestoringSelection = false;
                }
            }
            // 註：原先在此處判斷新增列時的滾動邏輯已移除，現由 ObsCollection_CollectionChanged 統一非同步處理強制滾動 (且反白不取消)。

            ApplyObserverHighlightsToKline();

            // 更新底部 ComboBox 壓力支撐下拉選項
            RefreshObserverComboboxes();

            ApplyTargetHighlight();
        }

        /// <summary>
        /// 重新計算極值觀測表中所有列的核取方塊可用狀態 (IsCheckable)。
        /// 規則：
        /// 1. 該列已破 (IsBroken == true) => IsCheckable = false。
        /// 2. 該列未破：
        ///    - 若類型是 "做空" (抓新高反轉)，往後 (往下) 搜尋若有任何 "做多" 且未破的資料，則 IsCheckable = false。
        ///    - 若類型是 "做多" (抓新低反轉)，往後 (往下) 搜尋若有任何 "做空" 且未破的資料，則 IsCheckable = false。
        ///    - 否則 IsCheckable = true。
        /// 3. 若類型既非 "做多" 也非 "做空"，則 IsCheckable = false。
        /// </summary>
        private void RecalculateObserverCheckableStates()
        {
            // 1. 計算大勢順勢方向指標 (排除歷史 history 與註解 annotation 項目)
            var validItems = _obsCollection.Where(o => o.Type != null && !o.Tags.Contains("history") && !o.Tags.Contains("annotation")).ToList();
            int totalShortCount = validItems.Count(o => o.Type == "做空");
            int totalLongCount = validItems.Count(o => o.Type == "做多");
            int unbrokenShortCount = validItems.Count(o => o.Type == "做空" && !o.IsBroken && (o.StopLossDisplay == null || !o.StopLossDisplay.Contains("已破")));
            int unbrokenLongCount = validItems.Count(o => o.Type == "做多" && !o.IsBroken && (o.StopLossDisplay == null || !o.StopLossDisplay.Contains("已破")));

            string totalTrend = totalShortCount > totalLongCount ? "Short" : (totalLongCount > totalShortCount ? "Long" : "Neutral");
            string unbrokenTrend = unbrokenShortCount > unbrokenLongCount ? "Short" : (unbrokenLongCount > unbrokenShortCount ? "Long" : "Neutral");

            bool forceDisableLong = totalTrend == "Short" && unbrokenTrend == "Short";
            bool forceDisableShort = totalTrend == "Long" && unbrokenTrend == "Long";

            // 2. 遍歷並設定每一列的可用性
            int count = _obsCollection.Count;
            for (int i = 0; i < count; i++)
            {
                var current = _obsCollection[i];
                
                // 基礎判定邏輯 (原版)
                bool baseCheckable = true;
                if (current.IsBroken)
                {
                    // 順序優化下單攔截
                    if (current.IsChecked && !current.IsTriggered)
                    {
                        int? latestPrice = current.OrderedSymbol != null && current.OrderedSymbol.StartsWith("TXF") ? _lastTxfPrice : _lastMxfPrice;
                        if (latestPrice.HasValue)
                        {
                            bool isTrigger = false;
                            if (current.Type == "做空" && latestPrice.Value >= current.BestAPrice)
                            {
                                isTrigger = true;
                            }
                            else if (current.Type == "做多" && latestPrice.Value <= current.BestAPrice)
                            {
                                isTrigger = true;
                            }

                            if (isTrigger)
                            {
                                current.IsTriggered = true; // 標記為已觸發
                                string buys = current.Type == "做多" ? "B" : "S";
                                string price = current.BestAPrice.ToString();
                                string qty = "1";

                                AppendLog($"【交易】價格來到 A 點價且判定已破，優先執行下單！商品: {current.OrderedSymbol} 方向: {(buys == "B" ? "買進" : "賣出")} {qty}口 @ {price} (最新成交價: {latestPrice.Value})");

                                if (_yuantaOrd != null && !string.IsNullOrEmpty(_currentAccount))
                                {
                                    string ret = _yuantaOrd.SendOrderF("01", "0", _currentBranch, _currentAccount, "", "", buys, current.OrderedSymbol, price, qty, "0", "L", "R", "", "");
                                    string[] retParts = ret.Split('|');
                                    if (retParts.Length > 0)
                                    {
                                        string oseqNo = retParts[0].Trim();
                                        current.OseqNo = oseqNo;
                                        var key = current.ConfirmedKey;
                                        if (_autoTradeStates.TryGetValue(key, out var state))
                                        {
                                            state.OseqNo = oseqNo;
                                        }
                                    }
                                    AppendLog($"【交易】元大 API 觸發下單回傳結果: {ret}");
                                }
                                else
                                {
                                    AppendLog("【交易】觸發下單失敗：元大交易 API 未登入。");
                                }
                            }
                        }
                    }
                    baseCheckable = false;
                }
                else if (current.Type != "做多" && current.Type != "做空")
                {
                    baseCheckable = false;
                }
                else
                {
                    bool hasActiveOpposite = false;
                    string oppositeType = current.Type == "做多" ? "做空" : "做多";
                    for (int j = i + 1; j < count; j++)
                    {
                        var next = _obsCollection[j];
                        if (next.Type == oppositeType && !next.IsBroken)
                        {
                            hasActiveOpposite = true;
                            break;
                        }
                    }
                    baseCheckable = !hasActiveOpposite;
                }

                // 套用大勢過濾限制
                if (baseCheckable)
                {
                    if (forceDisableLong && current.Type == "做多")
                    {
                        baseCheckable = false;
                    }
                    else if (forceDisableShort && current.Type == "做空")
                    {
                        baseCheckable = false;
                    }
                }

                // 寫入可用性，若導致已勾選的單子被取消，則使用 _isAutoCancelling 包裝以播放 Hand 警告音效與自動撤單
                if (!baseCheckable && current.IsChecked)
                {
                    _isAutoCancelling = true;
                    try
                    {
                        current.IsCheckable = false;
                    }
                    finally
                    {
                        _isAutoCancelling = false;
                    }
                }
                else
                {
                    current.IsCheckable = baseCheckable;
                }
            }
        }

        /// <summary>
        /// 監聽極值觀測集合的元素異動，動態訂閱項目屬性變更，並在有新觀測列加入時自動滾動至最底部。
        /// </summary>
        private void ObsCollection_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (SimulationResult item in e.NewItems)
                {
                    item.PropertyChanged += ObsItem_PropertyChanged;
                }

                // 當有新增極值觀測列時，非同步強制滾動至最底部，確保 UI 排版完成後執行且保留現有反白狀態
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    if (dgObserver.Items.Count > 0)
                    {
                        dgObserver.ScrollIntoView(dgObserver.Items[^1]);
                    }
                }));
            }
            if (e.OldItems != null)
            {
                foreach (SimulationResult item in e.OldItems)
                {
                    item.PropertyChanged -= ObsItem_PropertyChanged;
                }
            }
        }

        private bool _isHandlingPropertyChange = false;
        private bool _isAutoCancelling = false;

        /// <summary>
        /// 處理觀測表項目屬性變更 (已簡化，自動交易不由勾選事件觸發)。
        /// </summary>
        private void ObsItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 自動交易已移除 CheckBox 手動勾選邏輯，此處僅預留作 PropertyChanged 監聽
        }

        /// <summary>
        /// 自動交易持倉監控：當最新成交價更新時，遍歷已成交持倉，判斷是否穿價觸發停損或停利平倉。
        /// </summary>
        private void MonitorPositionsForStopLossAndTakeProfit(string symbol, int currentPrice)
        {
            int count = _obsCollection.Count;
            for (int i = 0; i < count; i++)
            {
                var item = _obsCollection[i];
                if (item.OrderedSymbol != symbol || item.TradeStatus != "已成交")
                {
                    continue;
                }

                bool triggerClose = false;
                string closeType = "";

                if (item.Type == "做空")
                {
                    // 做空停損在上方 (穿價：最新價大於停損價)
                    if (currentPrice > item.StopLossPrice)
                    {
                        triggerClose = true;
                        closeType = "停損";
                    }
                    // 做空停利在下方 (穿價：最新價小於停利價)
                    else if (currentPrice < item.TakeProfitPrice)
                    {
                        triggerClose = true;
                        closeType = "停利";
                    }
                }
                else if (item.Type == "做多")
                {
                    // 做多停損在下方 (穿價：最新價小於停損價)
                    if (currentPrice < item.StopLossPrice)
                    {
                        triggerClose = true;
                        closeType = "停損";
                    }
                    // 做多停利在上方 (穿價：最新價大於停利價)
                    else if (currentPrice > item.TakeProfitPrice)
                    {
                        triggerClose = true;
                        closeType = "停利";
                    }
                }

                if (triggerClose)
                {
                    item.TradeStatus = "平倉中"; // 避免重複平倉

                    var key = item.ConfirmedKey;
                    var state = _autoTradeStates.GetOrAdd(key, k => new AutoTradeState());
                    state.TradeStatus = "平倉中";

                    if (closeType == "停損")
                    {
                        item.StopLossDisplay = $"{item.StopLossPrice} (已停損)";
                        state.StopLossDisplay = $"{item.StopLossPrice} (已停損)";
                    }
                    else if (closeType == "停利")
                    {
                        item.StopLossDisplay = $"{item.StopLossPrice} (已停利)";
                        state.StopLossDisplay = $"{item.StopLossPrice} (已停利)";
                    }

                    string closeBuys = item.Type == "做多" ? "S" : "B"; // 反向平倉

                    AppendLog($"【自動交易】持倉觸發 {closeType} 平倉監控！最新價: {currentPrice}，停損價: {item.StopLossPrice}，停利價: {item.TakeProfitPrice}，送出平倉單！");

                    if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount))
                    {
                        AppendLog("【自動交易】平倉失敗：元大交易 API 未登入。");
                        item.TradeStatus = $"平倉失敗 ({closeType})";
                        if (state != null) state.TradeStatus = $"平倉失敗 ({closeType})";
                        continue;
                    }

                    // 呼叫 API 送出平倉委託，使用市價 (PriType="M")，IOC (OrdCond="I")
                    string ret = _yuantaOrd.SendOrderF("01", "0", _currentBranch, _currentAccount, "", "", closeBuys, item.OrderedSymbol, "0", "1", "0", "M", "I", "", "");
                    AppendLog($"【自動交易】元大 API 平倉回傳結果: {ret}");
                }
            }
        }

        private void ApplyObserverHighlightsToKline()
        {
            if (_klineCollection.Count == 0) return;

            foreach (var k in _klineCollection)
            {
                k.IsObsKLowHighlight = false;
                k.IsObsKHighHighlight = false;
            }

            foreach (var obs in _obsCollection)
            {
                bool isKLow = obs.Type != null && obs.Type.Contains("做空");
                bool isKHigh = obs.Type != null && obs.Type.Contains("做多");

                if (!isKLow && !isKHigh)
                {
                    isKLow = obs.Type == "做空";
                    isKHigh = obs.Type == "做多";
                }
                
                if (!isKLow && !isKHigh) continue;

                string aTimeStr = obs.BestATime;
                if (string.IsNullOrEmpty(aTimeStr) || aTimeStr.Length < 6) continue;

                if (!int.TryParse(aTimeStr.AsSpan(0, 2), out int ah) ||
                    !int.TryParse(aTimeStr.AsSpan(2, 2), out int am) ||
                    !int.TryParse(aTimeStr.AsSpan(4, 2), out int aSec))
                {
                    continue;
                }

                double aTimeVal = (ah * 60 + am) * 60 + aSec;
                if (_currentSessionName == "夜盤" && (ah * 60 + am) < 900)
                {
                    aTimeVal += 86400; // 夜盤跨日
                }

                int targetIdx = -1;
                for (int i = 0; i < _klineCollection.Count; i++)
                {
                    string timeLabel = _klineCollection[i].TimeLabel;
                    try
                    {
                        var times = timeLabel.Split('~');
                        var startParts = times[0].Split(':');
                        int sh = int.Parse(startParts[0]);
                        int sm = int.Parse(startParts[1]);
                        double startT = (sh * 60 + sm) * 60.0;
                        if (_currentSessionName == "夜盤" && (sh * 60 + sm) < 900) startT += 86400.0;

                        var endParts = times[1].Split(':');
                        int eh = int.Parse(endParts[0]);
                        int em = int.Parse(endParts[1]);
                        double endT = (eh * 60 + em) * 60.0;
                        if (_currentSessionName == "夜盤" && (eh * 60 + em) < 900) endT += 86400.0;

                        if (aTimeVal >= startT && aTimeVal < endT)
                        {
                            targetIdx = i;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetIdx > 0)
                {
                    int prevIdx = targetIdx - 1;
                    if (isKLow)
                        _klineCollection[prevIdx].IsObsKLowHighlight = true;
                    else if (isKHigh)
                        _klineCollection[prevIdx].IsObsKHighHighlight = true;
                }
            }

        }

        private void RefreshInfoPanel()
        {
            string activeSession = _isReplaying ? _currentReplaySession : (_currentRealtimePort == 442 ? "夜盤" : "日盤");
            
            if (_isReplaying)
            {
                lblLivePrice.Text = _lastMxfPrice.HasValue ? $"| 價: {_lastMxfPrice.Value}" : "| 價: --";

                if (wndUnbrokenK != null && _lastMxfPrice.HasValue)
                {
                    wndUnbrokenK.CheckInstantUnbrokenBreakout(_lastMxfPrice.Value, _lastMxfTime);
                }
            }

            var counts = new List<string>();
            var tradesSource = _isReplaying ? _replaySymbolTrades : _liveSymbolTrades;
            var statesSource = _isReplaying ? _replayRtState : _rtState;

            foreach (var sym in new[] { "TXF", "MXF" })
            {
                int count;
                lock (_rtLock)
                {
                    count = tradesSource[sym][activeSession].Count;
                }
                counts.Add($"{sym}({activeSession[0]}:{count})");
            }

            string statusPrefix = _isReplaying ? "復盤回放" : $"已連線({_currentRealtimePort})";
            lblRealtimeStatus.Content = $"{statusPrefix} | {string.Join(" | ", counts)}";

            // 大台小台買賣速度顯示
            var symLabels = new[] { ("TXF", lblSpeedTxf, "大臺"), ("MXF", lblSpeedMxf, "小臺") };
            foreach (var (sym, lbl, name) in symLabels)
            {
                TradingState state;
                lock (_rtLock)
                {
                    state = statesSource[sym][activeSession].Clone();
                }

                if (state.Count > 0)
                {
                    var (oAvg, iAvg, dStr) = TradingEngine.CalcSideSpeedFromState(state);
                    string oS = oAvg.HasValue ? $"{oAvg.Value:F4}s/{state.OuterCount,5}筆" : "--/筆";
                    string iS = iAvg.HasValue ? $"{iAvg.Value:F4}s/{state.InnerCount,5}筆" : "--/筆";
                    int avgPri = (int)Math.Round((double)state.SumPrice / state.Count);

                    lbl.Text = $"{name}: 外盤(買) {oS} | 內盤(賣) {iS} → {dStr} | 均價:{avgPri}";
                    
                    var color = dStr.Contains("多方") ? Color.FromRgb(235, 75, 75) : dStr.Contains("空方") ? Color.FromRgb(40, 167, 69) : Color.FromRgb(160, 160, 160);
                    lbl.Foreground = new SolidColorBrush(color);
                }
                else
                {
                    lbl.Text = $"{name}: 外盤(買) --/筆 | 內盤(賣) --/筆 → 資料不足 | 均價:--";
                    lbl.Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160));
                }
            }
        }

        // ==================== 5. 離線 .log 載入與增量解析 (RunAnalysisSync/Async) ====================

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                Title = "開啟日誌檔 (.log)"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string path = openFileDialog.FileName;
                _currentReplayDir = null; // 清除回放目錄

                btnOpen.IsEnabled = false;
                btnUpdate.IsEnabled = false;
                btnFill.IsEnabled = false;
                
                lblStatus.Content = $"正在處理: {Path.GetFileName(path)}...";
                AppendLog($"\n--- 開始分析 {path} ---");

                klineChart.Reset(); // 載入新檔案強制重算可見視界範圍
                klineChart.UpdateCandles([], forceAutoRange: true);

                Task.Run(() =>
                {
                    try
                    {
                        var (success, result, status) = RunAnalysisSync([(path, t => true)], "MXF", _currentTargetDays, ignoreTimeCheck: true);
                        
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            btnOpen.IsEnabled = true;
                            btnUpdate.IsEnabled = true;
                            btnFill.IsEnabled = true;

                            if (success && result is Dictionary<string, string> reports)
                            {
                                OnAnalysisCompletedSuccess(reports);
                            }
                            else
                            {
                                AppendLog($"分析失敗: {result}");
                                lblStatus.Content = "分析出錯";
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            btnOpen.IsEnabled = true;
                            btnUpdate.IsEnabled = true;
                            btnFill.IsEnabled = true;
                            AppendLog($"分析例外失敗: {ex.Message}");
                            lblStatus.Content = "分析出錯";
                        }));
                    }
                });
            }
        }

        private void OnAnalysisCompletedSuccess(Dictionary<string, string> reports)
        {
            string finalContent = "";
            var allOfflineKlineData = new List<KlineBar>();
            
            // 處理大台大小台共識推播
            for (int i = 0; i < 2; i++)
            {
                string session = i == 0 ? "日盤" : "夜盤";
                if (reports.TryGetValue(session, out var repText) && !string.IsNullOrEmpty(repText))
                {
                    finalContent += repText + "\n";
                }

                ConcurrentAppendOnlyList<TradeTick>? txfT = null;
                ConcurrentAppendOnlyList<TradeTick>? mxfT = null;
                var txfSigs = new List<SimulationResult>();
                var mxfSigs = new List<SimulationResult>();

                lock (_rtLock)
                {
                    string keyTxf = $"TXF ({session})";
                    string keyMxf = $"MXF ({session})";
                    _liveSymbolTrades["TXF"].TryGetValue(session, out txfT);
                    _liveSymbolTrades["MXF"].TryGetValue(session, out mxfT);
                    if (_rtState["TXF"].ContainsKey(session)) txfSigs = GetSimResultsFromSnapshot("TXF", session);
                    if (_rtState["MXF"].ContainsKey(session)) mxfSigs = GetSimResultsFromSnapshot("MXF", session);
                }

                if (txfT != null && mxfT != null && txfT.Count > 0 && mxfT.Count > 0)
                {
                    var pushes = _engine.SimulateSpeedPushesDual(txfT, mxfT);
                    if (pushes.Count > 0)
                    {
                        finalContent += $"\n    [{session} 大小臺共識推播歷程]\n";
                        foreach (var p in pushes)
                        {
                            finalContent += p + "\n";
                        }
                    }
                }

                if (mxfT != null && mxfT.Count > 0)
                {
                    var (klineData, breakouts) = _engine.CalcKlineData(session, mxfT, txfSigs, mxfSigs, _currentKlineInterval);
                    
                    // 文字版K線報表
                    string klineStr = _engine._generate_kline_text(session, [.. klineData.Select(k => (object)k)], [.. breakouts.Select(b => (object)b)], _currentKlineInterval.ToString());
                    if (!string.IsNullOrEmpty(klineStr))
                    {
                        finalContent += "\n" + klineStr + "\n";
                    }

                    allOfflineKlineData.AddRange(klineData);
                    
                    // 預載停損極值對比表
                    wndUnbrokenK.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var results = _engine.CalcSimulationResults(session, mxfT, klineData, _currentObsN, true, (dynN) => { Dispatcher.BeginInvoke(new System.Action(() => { lblObsN.Content = $"觀察N: {dynN}"; })); });
                        UpdateObserverViews(results);
                    }));
                }
            }

            AppendLog(finalContent, clear: true, forceScrollToEnd: true);

            if (allOfflineKlineData.Count > 0)
            {
                UpdateKlineViews(allOfflineKlineData);
                klineChart.UpdateCandles(allOfflineKlineData);

                // 圖表資料更新完成後，才重新對焦白框
                if (_lockedFocusTime != null)
                {
                    RefocusChartOnTime(_lockedFocusTime, _lockedFocusPrice, false);
                }
            }

            lblStatus.Content = $"更新完成 | {GetQuantReportStatus()}";

            // 更新頂部大臺/小臺極值行情面板
            UpdateOfflineExtremePanel();

            // 更新共用區間統計並派發至未破監控 (離線模式)
            UpdateOfflineSharedData();
        }

        private void UpdateOfflineSharedData()
        {
            string session = "日盤";
            lock (_rtLock)
            {
                if (_liveSymbolTrades["MXF"]["夜盤"].Count > 0) session = "夜盤";
            }

            IReadOnlyList<TradeTick>? mxfT = null;
            var txfSigs = new List<SimulationResult>();
            var mxfSigs = new List<SimulationResult>();

            lock (_rtLock)
            {
                _liveSymbolTrades["MXF"].TryGetValue(session, out var list);
                mxfT = list;
                if (_rtState["TXF"].ContainsKey(session)) txfSigs = GetSimResultsFromSnapshot("TXF", session);
                if (_rtState["MXF"].ContainsKey(session)) mxfSigs = GetSimResultsFromSnapshot("MXF", session);
            }

            if (mxfT != null && mxfT.Count > 0)
            {
                var sharedMap = ComputeAllIntervalResults(session, mxfT, txfSigs, mxfSigs, _currentObsN);
                UpdateIntervalStatsUI(sharedMap);

                string priceStr = mxfT[mxfT.Count - 1].Price.ToString();
                string timeStr = mxfT[mxfT.Count - 1].Time;
                
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    wndUnbrokenK.UpdateFromSharedData(sharedMap, priceStr, timeStr);
                }));
            }
        }

        /// <summary>
        /// 離線模式下，讀取已載入的 TradingState 快照，更新頂部大臺/小臺極值行情面板。
        /// 對應 Python 版本中離線分析完成後的面板刷新邏輯。
        /// </summary>
        private void UpdateOfflineExtremePanel()
        {
            // 優先使用當前啟用 session（離線模式通常日盤與夜盤都可能有資料）
            // 取最後一個有資料的 session 進行顯示
            TradingState mxfState = new();
            TradingState txfState = new();

            foreach (var sess in new[] { "日盤", "夜盤" })
            {
                TradingState ms, ts;
                lock (_rtLock)
                {
                    ms = _rtState["MXF"][sess].Clone();
                    ts = _rtState["TXF"][sess].Clone();
                }
                // 以有資料的 session 覆蓋
                if (ms.Count > 0) mxfState = ms;
                if (ts.Count > 0) txfState = ts;
            }

            // 組合極值資訊字串
            var (maxStr, minStr, ampStr) = TradingEngine.FormatExtremeInfo(mxfState, FormatExtremeTime);

            // 計算速差方向
            var (_, _, dT) = TradingEngine.CalcSideSpeedFromState(txfState);
            var (_, _, dM) = TradingEngine.CalcSideSpeedFromState(mxfState);

            double? netT = TradingEngine.CalcNetSpeedFromState(txfState);
            double? netM = TradingEngine.CalcNetSpeedFromState(mxfState);

            string netTxfStr = netT.HasValue ? $"| 大臺速差: {netT.Value:+0.0000;-0.0000;+0.0000}s" : "| 大臺速差: --";
            string netTxfColor = netT.HasValue ? (netT.Value > 0 ? "#EB4B4B" : netT.Value < 0 ? "#28A745" : "Gray") : "Gray";
            string netMxfStr = netM.HasValue ? $"| 小臺速差: {netM.Value:+0.0000;-0.0000;+0.0000}s" : "| 小臺速差: --";
            string netMxfColor = netM.HasValue ? (netM.Value > 0 ? "#EB4B4B" : netM.Value < 0 ? "#28A745" : "Gray") : "Gray";

            var (consensusStr, consensusColor) = TradingEngine.CalcConsensus(dT, dM);

            // 套用至頂部標籤
            runMaxInfo.Text = maxStr;
            runMinInfo.Text = minStr;
            runAmpInfo.Text = ampStr;
            lblConsensusDir.Text = consensusStr;
            SetWidgetStyleLazy(lblConsensusDir, consensusColor);
            lblTxfNetSpeed.Text = netTxfStr;
            SetWidgetStyleLazy(lblTxfNetSpeed, netTxfColor);
            lblMxfNetSpeed.Text = netMxfStr;
            SetWidgetStyleLazy(lblMxfNetSpeed, netMxfColor);
        }

        /// <summary>
        /// 離線模式下，變更「小臺 K 線分」或「觀察 N」後的全量資料重新聚合與重繪。
        /// 以現有 _liveSymbolTrades 資料為基礎，重新計算 K 棒、停損觀測表並重繪圖表。
        /// </summary>
        private void ReplotOfflineData()
        {
            // 非離線模式（有即時行情）則交給正常的 _analysisEvent 處理
            if (_yuantaQuote != null || _isReplaying) return;

            // 確認確實有離線資料可以使用
            bool hasData = false;
            lock (_rtLock)
            {
                foreach (var sess in new[] { "日盤", "夜盤" })
                {
                    if (_liveSymbolTrades["MXF"][sess].Count > 0) { hasData = true; break; }
                }
            }
            if (!hasData) return;

            // 清除舊的 K棒快取，強制重新聚合
            _engine.ClearCache();

            var allOfflineKlineData = new List<KlineBar>();

            foreach (var session in new[] { "日盤", "夜盤" })
            {
                IReadOnlyList<TradeTick> mxfT;
                lock (_rtLock)
                {
                    mxfT = _liveSymbolTrades["MXF"][session];
                }

                if (mxfT.Count == 0) continue;

                var txfSigs = GetSimResultsFromSnapshot("TXF", session);
                var mxfSigs = GetSimResultsFromSnapshot("MXF", session);

                // 重新聚合 K線
                var (klineData, _) = _engine.CalcKlineData(session, mxfT, txfSigs, mxfSigs, _currentKlineInterval);
                allOfflineKlineData.AddRange(klineData);

                // 重新計算停損觀測表
                var results = _engine.CalcSimulationResults(session, mxfT, klineData, _currentObsN, true, (dynN) => { Dispatcher.BeginInvoke(new System.Action(() => { lblObsN.Content = $"觀察N: {dynN}"; })); });
                UpdateObserverViews(results);
            }

            if (allOfflineKlineData.Count > 0)
            {
                UpdateKlineViews(allOfflineKlineData);
                klineChart.UpdateCandles(allOfflineKlineData);

                // 圖表資料更新完成後，才重新對焦白框
                if (_lockedFocusTime != null)
                {
                    RefocusChartOnTime(_lockedFocusTime, _lockedFocusPrice, false);
                }
            }

            // 觸發未破停損監控重新計算 (離線模式共用資料派發)
            UpdateOfflineSharedData();
        }

        private List<SimulationResult> GetSimResultsFromSnapshot(string sym, string session)
        {
            // 優先由當前實時計算的 snapshot 提取
            if (_lastRtStatusSnapshot != null)
            {
                var (s, sess, _, _, details) = _lastRtStatusSnapshot.FirstOrDefault(x => x.Symbol == sym && x.Session == session);
                if (s != null)
                {
                    return details;
                }
            }

            // 由臨時載入的資料庫快取提取
            if (App.Current.Resources.Contains("_temp_offline_signals"))
            {
                var dict = (Dictionary<string, List<SimulationResult>>)App.Current.Resources["_temp_offline_signals"];
                string key = $"{sym} ({session})";
                if (dict.TryGetValue(key, out var list))
                    return list;
            }
            return [];
        }

        /// <summary>
        /// 100% 移植 _analyze_file_logic 核心離線分析流程。
        /// </summary>
        private (bool Success, object Result, string? Status) RunAnalysisSync(
            (string Path, Func<double, bool> Filter)[] filePaths, string targetSymbol, int targetDays = 60, bool ignoreTimeCheck = true)
        {
            var quantParams = _engine.LoadQuantParams(targetSymbol, targetDays);

            var pattern = SymbolRegex();
            var mattimePat = MatTimeRegex();
            var matPriPat = MatPriRegex();
            var tmatqtyPat = TMatQtyRegex();
            var bestbpPat = BestBpRegex();
            var bestspPat = BestSpRegex();

            var allSymbolTrades = new Dictionary<string, Dictionary<string, List<TradeTick>>>
            {
                { "TXF", new Dictionary<string, List<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } },
                { "MXF", new Dictionary<string, List<TradeTick>> { { "日盤", new() }, { "夜盤", new() } } }
            };

            var lastTmatqty = new Dictionary<(string, string, string), int>();

            try
            {
                foreach (var (path, timeFilter) in filePaths)
                {
                    if (!File.Exists(path)) continue;

                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.GetEncoding("big5"));
                    string? line;

                    while ((line = sr.ReadLine()) != null)
                    {
                        var tick = TickParser.ParseLogLine(line, lastTmatqty, null, timeFilter, path);
                        if (tick == null) continue;

                        if (tick.Value.Side == TradeSide.Unknown)
                        {
                            var prevTrades = allSymbolTrades[tick.Value.Symbol][tick.Value.Session];
                            TradeSide fallbackSide = prevTrades.Count > 0 ? prevTrades[^1].Side : TradeSide.Outer;
                            tick = new TradeTick(tick.Value.Symbol, tick.Value.Time, tick.Value.TimeVal, tick.Value.Price, tick.Value.Qty, fallbackSide, tick.Value.BestBp, tick.Value.BestSp, tick.Value.Session);
                        }

                        allSymbolTrades[tick.Value.Symbol][tick.Value.Session].Add(tick.Value);
                    }
                }

                // 複製至全域快取並與預載期間即時 Tick 去重合併 (執行緒安全)
                lock (_rtLock)
                {
                    foreach (var sym in new[] { "TXF", "MXF" })
                    {
                        foreach (var sess in new[] { "日盤", "夜盤" })
                        {
                            var historyList = allSymbolTrades[sym][sess];
                            var liveList = _liveSymbolTrades[sym][sess]; // 預載期間可能已接收之即時 Tick
                            
                            var mergedList = new ConcurrentAppendOnlyList<TradeTick>();
                            mergedList.AddRange(historyList);
                            
                            if (liveList.Count > 0)
                            {
                                if (historyList.Count > 0)
                                {
                                    // 取歷史 Tick 尾部 (例如最後 200 個) 的特徵做去重雜湊
                                    var lastHistoryTicks = new HashSet<(double TimeVal, int Price, TradeSide Side, int BestBp, int BestSp)>();
                                    int skipCount = Math.Max(0, historyList.Count - 200);
                                    for (int k = skipCount; k < historyList.Count; k++)
                                    {
                                        var hTick = historyList[k];
                                        lastHistoryTicks.Add((hTick.TimeVal, hTick.Price, hTick.Side, hTick.BestBp, hTick.BestSp));
                                    }
                                    
                                    double lastHistoryTimeVal = historyList[^1].TimeVal;
                                    
                                    foreach (var lTick in liveList)
                                    {
                                        // 1. 若即時 Tick 時間早於歷史最後一個 Tick 減 5 秒，視為歷史重複，直接過濾
                                        if (lTick.TimeVal < lastHistoryTimeVal - 5.0)
                                            continue;
                                            
                                        // 2. 檢查是否與歷史尾部重複
                                        var key = (lTick.TimeVal, lTick.Price, lTick.Side, lTick.BestBp, lTick.BestSp);
                                        if (lastHistoryTicks.Contains(key))
                                            continue;
                                            
                                        // 3. 通過檢查，將即時 Tick 追加至合併列表
                                        mergedList.Add(lTick);
                                    }
                                }
                                else
                                {
                                    mergedList.AddRange(liveList);
                                }
                            }
                            
                            _liveSymbolTrades[sym][sess] = mergedList;
                            
                            // 更新 O(1) 狀態
                            var state = _rtState[sym][sess];
                            state.Reset();
                            var list = _liveSymbolTrades[sym][sess];
                            if (list.Count > 0)
                            {
                                state.Count = list.Count;
                                state.SumPrice = list.Sum(t => (long)t.Price);
                                state.DayMax = list.Max(t => t.Price);
                                state.DayMin = list.Min(t => t.Price);
                                state.MaxTime = list.Last(t => t.Price == state.DayMax).Time;
                                state.MinTime = list.Last(t => t.Price == state.DayMin).Time;
                                
                                var outerList = list.Where(t => t.Side == TradeSide.Outer).ToList();
                                var innerList = list.Where(t => t.Side == TradeSide.Inner).ToList();
                                state.OuterCount = outerList.Count;
                                state.InnerCount = innerList.Count;
                                if (outerList.Count > 0)
                                {
                                    state.FirstOuterTime = outerList[0].TimeVal;
                                    state.LastOuterTime = outerList[^1].TimeVal;
                                }
                                if (innerList.Count > 0)
                                {
                                    state.FirstInnerTime = innerList[0].TimeVal;
                                    state.LastInnerTime = innerList[^1].TimeVal;
                                }
                            }
                        }
                    }
                }

                var aggregatedTrades = new Dictionary<string, List<TradeTick>>();
                foreach (var symbol in new[] { "TXF", "MXF" })
                {
                    foreach (var sessionName in new[] { "日盤", "夜盤" })
                    {
                        var trades = allSymbolTrades[symbol][sessionName];
                        if (trades.Count == 0) continue;

                        double startTime = trades[0].TimeVal;
                        double endTime = trades[^1].TimeVal;

                        if (!ignoreTimeCheck)
                        {
                            if (sessionName == "日盤" && (startTime > 32400.0 || endTime < 46800.0)) continue;
                            if (sessionName == "夜盤" && (startTime > 63000.0 || endTime < 103500.0)) continue;
                        }

                        aggregatedTrades[$"{symbol} ({sessionName})"] = trades;
                    }
                }

                if (aggregatedTrades.Count == 0)
                    return (false, "未找到有效數據。", null);

                var reportsBySession = new Dictionary<string, string> { { "日盤", "" }, { "夜盤", "" } };
                var sessionSpeeds = new Dictionary<string, Dictionary<string, string>>
                {
                    { "日盤", new() { { "TXF", "--" }, { "MXF", "--" } } },
                    { "夜盤", new() { { "TXF", "--" }, { "MXF", "--" } } }
                };

                // 計算日夜速差
                foreach (var ss in aggregatedTrades)
                {
                    string sName = ss.Key.Contains("日盤") ? "日盤" : "夜盤";
                    string symCode = ss.Key.Contains("TXF") ? "TXF" : "MXF";
                    var (oa, ia, _) = _engine.CalcSideSpeed(ss.Value);
                    if (oa.HasValue && ia.HasValue)
                        sessionSpeeds[sName][symCode] = $"{ia.Value - oa.Value:+0.0000;-0.0000;+0.0000}s";
                }

                var tempOfflineSignals = new Dictionary<string, List<SimulationResult>>();

                foreach (var entry in aggregatedTrades)
                {
                    string symbolSession = entry.Key;
                    var trades = entry.Value;
                    string currSymbol = symbolSession.Contains("TXF") ? "TXF" : "MXF";
                    string currSession = symbolSession.Contains("日盤") ? "日盤" : "夜盤";
                    string otherSymCode = currSymbol == "TXF" ? "MXF" : "TXF";
                    var otherTradesAll = allSymbolTrades[otherSymCode][currSession];

                    var qParams = _engine.LoadQuantParams(currSymbol, targetDays);

                    int dayMax = trades.Max(t => t.Price);
                    int dayMin = trades.Min(t => t.Price);
                    int finalClose = trades[^1].Price;

                    var absDetails = new List<(double TVal, string StatusStr, string ATime, int PriceVal, string TrigTime, int TrigPrice, double? Pre, int PreVol, double? Post, int PostVol, int AmpVal, int BIdx)>();
                    int runningMax = -999999;
                    int runningMin = 999999;
                    int? lastPrice = null;
                    double lastCheckTimeH = -999999.0;
                    double lastCheckTimeB = -999999.0;

                    for (int i = 0; i < trades.Count; i++)
                    {
                        int price = trades[i].Price;
                        double tVal = trades[i].TimeVal;
                        bool isTrigH = false;
                        bool isTrigB = false;

                        if (price > runningMax) { runningMax = price; isTrigH = true; }
                        else if (price == runningMax)
                        {
                            if ((lastPrice.HasValue && lastPrice.Value < price) || (tVal - lastCheckTimeH >= 30.0)) isTrigH = true;
                        }

                        if (price < runningMin) { runningMin = price; isTrigB = true; }
                        else if (price == runningMin)
                        {
                            if ((lastPrice.HasValue && lastPrice.Value > price) || (tVal - lastCheckTimeB >= 30.0)) isTrigB = true;
                        }

                        if (price == dayMax)
                        {
                            var (pre, preVol, post, postVol, threshold, trigTime, trigPrice, actPre, actPost, bIdx) = _engine.GetDurationsFull(trades, _engine.AbsNTicks, i, TradeSide.Outer, TradeSide.Inner);
                            string status = _engine.GetStatusStr(pre, post, actPre, actPost, _engine.AbsNTicks);
                            int amp = runningMin != 999999 ? (runningMax - runningMin) : 0;
                            absDetails.Add((tVal, "時段最高" + status, trades[i].Time, price, trigTime ?? "N/A", trigPrice ?? 0, pre, preVol, post, postVol, amp, bIdx));
                        }
                        else if (isTrigH)
                        {
                            var (pre, preVol, post, postVol, threshold, trigTime, trigPrice, actPre, actPost, bIdx) = _engine.GetDurationsFull(trades, _engine.AbsNTicks, i, TradeSide.Outer, TradeSide.Inner);
                            string status = _engine.GetStatusStr(pre, post, actPre, actPost, _engine.AbsNTicks);
                            if (status.Contains("達標") || status.Contains("未達標"))
                            {
                                int amp = runningMin != 999999 ? (runningMax - runningMin) : 0;
                                string prefix = status.Contains("未達標") ? "曾未達標最高" : "曾達標最高";
                                absDetails.Add((tVal, prefix + status, trades[i].Time, price, trigTime ?? "N/A", trigPrice ?? 0, pre, preVol, post, postVol, amp, bIdx));
                            }
                        }

                        if (price == dayMin)
                        {
                            var (pre, preVol, post, postVol, threshold, trigTime, trigPrice, actPre, actPost, bIdx) = _engine.GetDurationsFull(trades, _engine.AbsNTicks, i, TradeSide.Inner, TradeSide.Outer);
                            string status = _engine.GetStatusStr(pre, post, actPre, actPost, _engine.AbsNTicks);
                            int amp = runningMax != -999999 ? (runningMax - runningMin) : 0;
                            absDetails.Add((tVal, "時段最低" + status, trades[i].Time, price, trigTime ?? "N/A", trigPrice ?? 0, pre, preVol, post, postVol, amp, bIdx));
                        }
                        else if (isTrigB)
                        {
                            var (pre, preVol, post, postVol, threshold, trigTime, trigPrice, actPre, actPost, bIdx) = _engine.GetDurationsFull(trades, _engine.AbsNTicks, i, TradeSide.Inner, TradeSide.Outer);
                            string status = _engine.GetStatusStr(pre, post, actPre, actPost, _engine.AbsNTicks);
                            if (status.Contains("達標") || status.Contains("未達標"))
                            {
                                int amp = runningMax != -999999 ? (runningMax - runningMin) : 0;
                                string prefix = status.Contains("未達標") ? "曾未達標最低" : "曾達標最低";
                                absDetails.Add((tVal, prefix + status, trades[i].Time, price, trigTime ?? "N/A", trigPrice ?? 0, pre, preVol, post, postVol, amp, bIdx));
                            }
                        }

                        if (isTrigH) lastCheckTimeH = tVal;
                        if (isTrigB) lastCheckTimeB = tVal;
                        lastPrice = price;
                    }

                    absDetails.Sort((x, y) => x.TVal.CompareTo(y.TVal));
                    var filteredPre = new List<(double TVal, string StatusStr, string ATime, int PriceVal, string TrigTime, int TrigPrice, double? Pre, int PreVol, double? Post, int PostVol, int AmpVal, int BIdx)>();
                    var seenKeys = new HashSet<(string Type, int Price)>();
                    foreach (var (tVal, statusStr, aTime, priceVal, trigTime, trigPrice, pre, preVol, post, postVol, ampVal, bIdx) in absDetails)
                    {
                        var key = (statusStr.Contains("最高") ? "最高" : "最低", priceVal);
                        if (seenKeys.Add(key))
                        {
                            filteredPre.Add((tVal, statusStr, aTime, priceVal, trigTime, trigPrice, pre, preVol, post, postVol, ampVal, bIdx));
                        }
                    }

                    // 重新做速差 Immutable
                    var lastNetSpeedsTop = new Dictionary<string, double?> { { "TXF", null }, { "MXF", null } };
                    var lastNetSpeedsBot = new Dictionary<string, double?> { { "TXF", null }, { "MXF", null } };
                    var finalAbsWithSpeeds = new List<SimulationResult>();

                    foreach (var (tVal, statusStr, aTime, priceVal, trigTime, trigPrice, pre, preVol, post, postVol, ampVal, bIdx) in filteredPre)
                    {
                        if (!post.HasValue)
                        {
                            var copy = new SimulationResult { DisplayTitle = statusStr, BestATime = aTime, BestAPrice = priceVal, StopLossDisplay = "N/A" };
                            finalAbsWithSpeeds.Add(copy);
                            continue;
                        }

                        bool isTop = statusStr.Contains("最高");
                        bool isDayExtreme = statusStr.Contains("時段最高") || statusStr.Contains("時段最低");
                        bool needSpeed = !(isDayExtreme && !statusStr.Contains("達標"));

                        string speedStr = "";
                        if (needSpeed)
                        {
                            speedStr = isTop
                                ? _engine.GetSpeedSnapshotStr(currSymbol, trades, bIdx, otherTradesAll, lastNetSpeedsTop)
                                : _engine.GetSpeedSnapshotStr(currSymbol, trades, bIdx, otherTradesAll, lastNetSpeedsBot);
                        }

                        var res = new SimulationResult
                        {
                            Type = isTop ? "做空" : "做多",
                            DisplayTitle = statusStr,
                            BestATime = aTime,
                            BestAPrice = priceVal,
                            TrigTime = trigTime,
                            TrigPrice = trigPrice.ToString(),
                            Pre = pre.HasValue ? $"{pre.Value:F4}-{preVol}" : "N/A",
                            Post = post.HasValue ? $"{post.Value:F4}-{postVol}" : "N/A",
                            AmpVal = ampVal,
                            BIndex = bIdx,
                            ObsN = _engine.AbsNTicks
                        };
                        res.Tags.Add(speedStr);
                        finalAbsWithSpeeds.Add(res);
                    }

                    tempOfflineSignals[symbolSession] = finalAbsWithSpeeds;

                    // 組裝輸出文字 (對齊 Python 格式)
                    // 使用純歷史資料建構 stateSnapshot，確保報表速差的 Immutability
                    // (不可引用 _rtState，因其包含即時 Tick 合併結果，每次更新數量不同導致速差不穩定)
                    var stateSnapshot = new Dictionary<string, TradingState>();
                    foreach (var sym in new[] { "TXF", "MXF" })
                    {
                        var histTrades = allSymbolTrades[sym][currSession];
                        var st = new TradingState();
                        if (histTrades.Count > 0)
                        {
                            st.Count = histTrades.Count;
                            st.SumPrice = histTrades.Sum(t => (long)t.Price);
                            st.DayMax = histTrades.Max(t => t.Price);
                            st.DayMin = histTrades.Min(t => t.Price);
                            st.MaxTime = histTrades.Last(t => t.Price == st.DayMax).Time;
                            st.MinTime = histTrades.Last(t => t.Price == st.DayMin).Time;
                            var outerList = histTrades.Where(t => t.Side == TradeSide.Outer).ToList();
                            var innerList = histTrades.Where(t => t.Side == TradeSide.Inner).ToList();
                            st.OuterCount = outerList.Count;
                            st.InnerCount = innerList.Count;
                            if (outerList.Count > 0)
                            {
                                st.FirstOuterTime = outerList[0].TimeVal;
                                st.LastOuterTime = outerList[^1].TimeVal;
                            }
                            if (innerList.Count > 0)
                            {
                                st.FirstInnerTime = innerList[0].TimeVal;
                                st.LastInnerTime = innerList[^1].TimeVal;
                            }
                        }
                        stateSnapshot[sym] = st;
                    }
                    string report = GenerateRealtimeReportStr(currSymbol, currSession, trades, finalAbsWithSpeeds, qParams, stateSnapshot);
                    reportsBySession[currSession] += report + "\n";
                }

                // 緩存以供 UI 讀取
                App.Current.Resources["_temp_offline_trades"] = _liveSymbolTrades.ToDictionary(k => k.Key, v => v.Value.ToDictionary(kk => kk.Key, vv => new List<TradeTick>(vv.Value)));
                App.Current.Resources["_temp_offline_signals"] = tempOfflineSignals;

                return (true, reportsBySession, "OK");
            }
            catch (Exception ex)
            {
                return (false, ex.Message, null);
            }
        }

        private string GenerateRealtimeReportStr(
            string symbol, string session, IReadOnlyList<TradeTick> trades, List<SimulationResult> filteredDetails, 
            Dictionary<string, object> quantParams, Dictionary<string, TradingState> stateSnapshot, int maxCount = -1)
        {
            // 快照邊界：確保報告生成過程中不會因其他執行緒新增 Tick 而產生不一致
            int tradesCount = maxCount < 0 ? trades.Count : maxCount;
            if (tradesCount == 0) return "";
            int lastIdx = tradesCount - 1;
            int finalClose = trades[lastIdx].Price;

            var (oAvg, iAvg, dStr) = _engine.CalcSideSpeed(trades, tradesCount);
            int oCnt = 0, iCnt = 0;
            long sumPrice = 0;
            for (int i = 0; i < tradesCount; i++)
            {
                if (trades[i].Side == TradeSide.Outer) oCnt++;
                else if (trades[i].Side == TradeSide.Inner) iCnt++;
                sumPrice += trades[i].Price;
            }
            string outerS = oAvg.HasValue ? $"{oAvg.Value:F4}s/{oCnt}筆" : "資料不足";
            string innerS = iAvg.HasValue ? $"{iAvg.Value:F4}s/{iCnt}筆" : "資料不足";
            int avgPri = tradesCount > 0 ? (int)Math.Round((double)sumPrice / tradesCount) : 0;

            string txfN = "--", mxfN = "--";
            if (stateSnapshot.TryGetValue("TXF", out var stT) && stateSnapshot.TryGetValue("MXF", out var stM))
            {
                var (_, _, dT) = TradingEngine.CalcSideSpeedFromState(stT);
                var (_, _, dM) = TradingEngine.CalcSideSpeedFromState(stM);
                
                double? netT = TradingEngine.CalcNetSpeedFromState(stT);
                double? netM = TradingEngine.CalcNetSpeedFromState(stM);

                txfN = netT.HasValue ? $"{netT.Value:+0.0000;-0.0000;+0.0000}s" : "--";
                mxfN = netM.HasValue ? $"{netM.Value:+0.0000;-0.0000;+0.0000}s" : "--";
            }

            static string FmtPadLeft(string text, int width)
            {
                if (text == null) text = "";
                int actualW = text.Sum(c => c > 127 ? 2 : 1);
                return text + new string(' ', Math.Max(0, width - actualW));
            }

            static string FmtPadRight(string text, int width)
            {
                if (text == null) text = "";
                int actualW = text.Sum(c => c > 127 ? 2 : 1);
                return new string(' ', Math.Max(0, width - actualW)) + text;
            }

            string hType = FmtPadLeft("類型", 22);
            string hZone = FmtPadLeft("進場區/停損", 24);
            string hATime = FmtPadLeft("A點時間", 9);
            string hAPri = FmtPadLeft("A點價", 6);
            string hBTime = FmtPadLeft("B點時間", 9);
            string hTrig = FmtPadLeft("觸發價", 6);
            string hPre = FmtPadLeft("前向平均", 8);
            string hPost = FmtPadLeft("後向平均", 8);
            string hAmp = FmtPadLeft("振幅", 5);

            string header = $"{hType}|{hZone}|{hATime}|{hAPri} |{hBTime}|{hTrig} |{hPre}|{hPost} | {hAmp}";
            string sep = "    " + new string('-', header.Sum(c => c > 127 ? 2 : 1));

            var sb = new StringBuilder();
            sb.AppendLine($"\n    [{symbol} 即時極值詳情 (平均每筆間隔)]  最新價: {finalClose}");
            int windowSize = _engine.AbsNTicks;
            int startIdx = Math.Max(0, tradesCount - windowSize);
            int windowOCnt = 0, windowICnt = 0;
            for (int i = startIdx; i < tradesCount; i++)
            {
                if (trades[i].Side == TradeSide.Outer) windowOCnt++;
                else if (trades[i].Side == TradeSide.Inner) windowICnt++;
            }
            string ioCompare = windowICnt > windowOCnt ? $" 內:{windowICnt} > 外:{windowOCnt}" :
                               (windowOCnt > windowICnt ? $" 外:{windowOCnt} > 內:{windowICnt}" : $" 內:{windowICnt} = 外:{windowOCnt}");

            sb.AppendLine($"    ● 成交速度: {dStr} | 大台速差: {txfN,-15} 小台速差: {mxfN,-15} | 均價:{avgPri}{ioCompare}");
            sb.AppendLine($"    {header}");
            sb.AppendLine(sep);

            string? lastAbsType = null;
            foreach (var d in filteredDetails)
            {
                if (d.Post == "N/A") continue;

                string currentType = d.DisplayTitle.Contains("最高") ? "最高" : "最低";
                if (lastAbsType != null && currentType != lastAbsType)
                {
                    sb.AppendLine(sep);
                }
                lastAbsType = currentType;

                string bTimeVal = d.TrigTime;
                if (!string.IsNullOrEmpty(bTimeVal))
                {
                    var c = bTimeVal.Replace(":", "");
                    if (c.Length >= 6) bTimeVal = $"{c.Substring(0, 2)}:{c.Substring(2, 2)}:{c.Substring(4, 2)}";
                }

                string aTimeVal = d.BestATime;
                if (!string.IsNullOrEmpty(aTimeVal))
                {
                    var c = aTimeVal.Replace(":", "");
                    if (c.Length >= 6) aTimeVal = $"{c.Substring(0, 2)}:{c.Substring(2, 2)}:{c.Substring(4, 2)}";
                }

                string bPriVal = d.TrigPrice;
                string preS = FmtPadRight(d.Pre, 8);
                string postS = FmtPadRight(d.Post, 8);

                string side = currentType == "最高" ? "top" : "bottom";
                string speedInfo = d.Tags.FirstOrDefault() ?? "";

                bool isUnmet = d.DisplayTitle.Contains(" [未達標]");
                bool forceShowUnmet = false;
                string displayTypeStr = d.DisplayTitle;
                
                var (isNormal, isContradiction) = TradingEngine.ClassifyTrigger(d.DisplayTitle, speedInfo);
                
                if (isUnmet && isContradiction)
                {
                    forceShowUnmet = true;
                    displayTypeStr = displayTypeStr.Replace("未達標", "矛盾");
                }

                string zoneStr = "";
                if (d.DisplayTitle.Contains(" [達標]") || (isUnmet && forceShowUnmet))
                {
                    zoneStr = GetZoneStr(side, d.BestATime, d.BestAPrice, session, quantParams);
                    if (zoneStr == "N/A") zoneStr = "";
                }

                sb.AppendLine($"    {FmtPadLeft(displayTypeStr, 22)}|{FmtPadLeft(zoneStr, 24)}|{FmtPadRight(aTimeVal, 9)}|{FmtPadRight(d.BestAPrice.ToString(), 6)}|{FmtPadRight(bTimeVal, 9)}|{FmtPadRight(bPriVal, 6)}|{preS}|{postS}|{d.AmpVal,5}");
                if (!string.IsNullOrEmpty(speedInfo))
                {
                    bool isKLow = currentType == "最低";
                    bool isKHigh = currentType == "最高";
                    bool isIlessO = speedInfo.Contains(" < 外:");
                    bool isIgreaterO = speedInfo.Contains(" > 外:");

                    if (isKLow && (isNormal || isContradiction) && isIlessO)
                    {
                        speedInfo = "[C:RED]" + speedInfo;
                    }
                    else if (isKHigh && (isNormal || isContradiction) && isIgreaterO)
                    {
                        speedInfo = "[C:GREEN]" + speedInfo;
                    }

                    sb.AppendLine($"    {speedInfo.Trim()}");
                }
            }

            return sb.ToString() + sep + "\n";
        }

        /// <summary>
        /// 智慧型自動探測並獲取今日歷史日誌 (Logs) 之專案根目錄或執行目錄路徑。
        /// </summary>
        private static string GetLogsDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
        {
            // 1. 優先探測當前執行程式目錄下的 Logs (適用於部署發布環境)
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (Directory.Exists(path)) return path;

            // 2. 如果找不到，再往存放 MainWindow.xaml.cs 檔案的本地目錄尋找
            if (!string.IsNullOrEmpty(sourceFilePath))
            {
                string? sourceDir = Path.GetDirectoryName(sourceFilePath);
                if (sourceDir != null)
                {
                    string sourceLogsPath = Path.Combine(sourceDir, "Logs");
                    if (Directory.Exists(sourceLogsPath)) return sourceLogsPath;
                }
            }

            // 3. 回退至預設執行目錄
            return path;
        }

        private void PreloadTodayLog()
        {
            DateTime now = DateTime.Now;
            string targetDateStr = now.ToString("yyyyMMdd");

            // 夜盤跨日預載修正：若當前時間早於早上 08:30，夜盤資料通常存在於昨日的日誌資料夾中
            if (now.TimeOfDay < new TimeSpan(8, 30, 0))
            {
                string yesterdayStr = now.AddDays(-1).ToString("yyyyMMdd");
                string yesterdayLog = Path.Combine(GetLogsDirectory(), yesterdayStr, "event.log");
                if (File.Exists(yesterdayLog))
                {
                    targetDateStr = yesterdayStr;
                }
            }

            string todayLog = Path.Combine(GetLogsDirectory(), targetDateStr, "event.log");

            if (!File.Exists(todayLog))
            {
                AppendLog($"【預載】未找到今日歷史日誌: {todayLog}，將直接由即時 Tick 開始。", forceScrollToEnd: true);
                lock (_rtLock)
                {
                    _isPreloading = false;
                }
                _analysisEvent.Set();
                return;
            }

            AppendLog($"【預載】偵測到今日歷史日誌，開始背景非同步載入: {Path.GetFileName(todayLog)}...", forceScrollToEnd: true);
            btnRealtime.IsEnabled = false; // 預載時先鎖定按鈕，防止二次進入
            btnUpdate.IsEnabled = false;

            string activeSession = _currentRealtimePort == 442 ? "夜盤" : "日盤";

            Task.Run(() =>
            {
                try
                {
                    var filePaths = new[] { (todayLog, (Func<double, bool>)(t => true)) };
                    var (success, result, status) = RunAnalysisSync(filePaths, "MXF", _currentTargetDays, ignoreTimeCheck: true);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        btnRealtime.IsEnabled = true;
                        btnUpdate.IsEnabled = true;
                        klineChart.EnableAutoRange(); // 預載成功強制 autoRange 重對焦

                        if (success && result is Dictionary<string, string> reports)
                        {
                            int txfCount = 0, mxfCount = 0;
                            lock (_rtLock)
                            {
                                _isPreloading = false; // 預載正式完成，解鎖計算
                                txfCount = _liveSymbolTrades["TXF"][activeSession].Count;
                                mxfCount = _liveSymbolTrades["MXF"][activeSession].Count;
                                
                                // 強置 scan 進度歸零以從頭開始計算極值演變軌跡
                                // 必須先完整 Reset state，否則增量掃描從 Tick 0 再次累加時，
                                // Count/SumPrice/OuterCount/InnerCount 等欄位會與 RunAnalysisSync 設定的值疊加導致翻倍，
                                // 造成歷史速差每次更新都不一樣的 Bug
                                _rtState["TXF"][activeSession].Reset();
                                _rtState["MXF"][activeSession].Reset();
                            }

                            AppendLog($"【預載】今日日誌預載成功！大臺: {txfCount} 筆, 小臺: {mxfCount} 筆。即時分析已啟動！", forceScrollToEnd: true);
                            
                            // 觸發重新分析計算
                            _analysisEvent.Set();
                        }
                        else
                        {
                            lock (_rtLock)
                            {
                                _isPreloading = false; // 預載失敗，解鎖計算
                            }
                            AppendLog("【預載】今日日誌預載失敗，將直接由即時 Tick 行情開始。", forceScrollToEnd: true);
                            _analysisEvent.Set();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        btnRealtime.IsEnabled = true;
                        lock (_rtLock)
                        {
                            _isPreloading = false; // 異常，解鎖計算
                        }
                        AppendLog($"【預載】今日日誌預載例外失敗: {ex.Message}，直接由即時 Tick 開始。", forceScrollToEnd: true);
                        _analysisEvent.Set();
                    }));
                }
            });
        }

        // ==================== 6. 復盤播放與進度控制 (ReplayThread 轉化) ====================

        private void BtnSelectReplayDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "選擇 event.log 所在的日期資料夾"
            };

            string defaultPath = !string.IsNullOrEmpty(_lastSelectedReplayDir) && Directory.Exists(_lastSelectedReplayDir) 
                ? _lastSelectedReplayDir 
                : GetLogsDirectory();

            if (Directory.Exists(defaultPath))
            {
                dialog.InitialDirectory = defaultPath;
            }

            if (dialog.ShowDialog() == true)
            {
                string path = dialog.FolderName;
                string logFile = Path.Combine(path, "event.log");
                
                if (File.Exists(logFile))
                {
                    _currentReplayDir = path;
                    _lastSelectedReplayDir = path;
                    txtReplayPath.Text = Path.GetFileName(path);
                    lblStatus.Content = $"已選定復盤目錄: {Path.GetFileName(path)}";
                }
                else
                {
                    MessageBox.Show("所選資料夾中沒有找到 event.log 檔案！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnLoadReplay_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentReplayDir))
            {
                lblStatus.Content = "請先點選選擇資料夾";
                return;
            }

            string logFile = Path.Combine(_currentReplayDir, "event.log");
            if (!File.Exists(logFile))
            {
                lblStatus.Content = $"找不到檔案: {Path.GetFileName(logFile)}";
                return;
            }

            // 1. 徹底停止上一日期的播放執行緒
            StopReplay();

            lblStatus.Content = "載入復盤檔案中...";
            btnLoadReplay.IsEnabled = false;
            btnPlayPause.IsEnabled = false;
            
            // 修正 WPF ComboBox.Text 抓取空值 Bug，改以 SelectedItem 安全取得
            string activeSession = (cboReplaySession.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "日盤";

            // 2. 先鎖定復盤旗標，立即阻擋 Live Tick 在背景解析期間觸發分析迴圈覆蓋 UI
            _isReplaying = true;
            _uiGeneration++; // 遞增世代，讓所有已排入 Dispatcher 佇列的過期分析結果自動失效

            // 3. 立即清空底部所有觀察欄位，防止 Live 分析迴圈的殘留 Dispatcher 任務回寫
            txtObsHigh.Text = "";
            txtObsLow.Text = "";
            cboObsHigh.Text = "";
            cboObsHigh.Items.Clear();
            cboObsLow.Text = "";
            cboObsLow.Items.Clear();
            lblObsN.Content = "觀測N: --";
            lblObsStatus.Text = "觀察: 待設定";
            _lastAutofillKlineTime = null;
            _engine._obs_high_entry_price = null;
            _engine._obs_low_entry_price = null;
            _engine._obs_high_price = null;
            _engine._obs_low_price = null;

            // 清空分析快照，防止 RefreshObserverComboboxes 從殘留的 Live 資料重新填充下拉選單
            _lastRtStatusSnapshot = [];

            // 4. 清空大小臺速度標籤，避免殘留即時行情數據
            lblSpeedTxf.Text = "大臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → 資料不足 | 均價:--";
            lblSpeedTxf.Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160));
            lblSpeedMxf.Text = "小臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → 資料不足 | 均價:--";
            lblSpeedMxf.Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160));

            // 5. 背景非同步解析 log 檔案成 Ticks 陣列 ( parser_worker 轉 C#背景 Task)
            Task.Run(() =>
            {
                try
                {
                    var parsedTicks = new List<TradeTick>();
                    var lastTmatqty = new Dictionary<(string, string, string), int>();

                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.GetEncoding("big5"));
                    string? line;

                    while ((line = sr.ReadLine()) != null)
                    {
                        var tick = TickParser.ParseLogLine(line, lastTmatqty, activeSession, null, logFile);
                        if (tick == null) continue;

                        parsedTicks.Add(tick.Value);
                    }

                    // 安全回到 UI 執行緒
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        btnLoadReplay.IsEnabled = true;
                        _allParsedTicks = parsedTicks;
                        _currentReplaySession = activeSession;

                        if (_allParsedTicks.Count == 0)
                        {
                            lblStatus.Content = $"無 [{activeSession}] Tick 資料！";
                            btnPlayPause.IsEnabled = false;
                            sldProgress.IsEnabled = false;
                            // 無資料時解除復盤旗標，讓 Live Tick 恢復正常運作
                            _isReplaying = false;
                        }
                        else
                        {
                            lblStatus.Content = $"載入 {parsedTicks.Count} 筆 Tick。";
                            btnPlayPause.IsEnabled = true;
                            btnStopReplay.IsEnabled = true;
                            sldProgress.IsEnabled = true;
                            
                            sldProgress.Minimum = 0;
                            sldProgress.Maximum = _allParsedTicks.Count - 1;
                            sldProgress.Value = 0;

                            ResetReplayTrack();
                            ReconstructReplayUpTo(0);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        btnLoadReplay.IsEnabled = true;
                        lblStatus.Content = $"載入出錯: {ex.Message}";
                    }));
                }
            });
        }

        private void ResetReplayTrack()
        {
            klineChart?.Reset();

            lock (_rtLock)
            {
                _replaySymbolTrades["TXF"]["日盤"].Clear();
                _replaySymbolTrades["TXF"]["夜盤"].Clear();
                _replaySymbolTrades["MXF"]["日盤"].Clear();
                _replaySymbolTrades["MXF"]["夜盤"].Clear();

                _replayRtState["TXF"]["日盤"].Reset();
                _replayRtState["TXF"]["夜盤"].Reset();
                _replayRtState["MXF"]["日盤"].Reset();
                _replayRtState["MXF"]["夜盤"].Reset();

                _rtTriggers["TXF"]["日盤"].Clear();
                _rtTriggers["TXF"]["夜盤"].Clear();
                _rtTriggers["MXF"]["日盤"].Clear();
                _rtTriggers["MXF"]["夜盤"].Clear();

                // 【修正】必須同步清空已完成的極值詳情，防止舊的極值訊號殘留進新的分析迴圈，
                // 導致「未破分K停損監控」在復盤路徑與預載路徑產生不一致的結果
                _rtCompletedDetails["TXF"]["日盤"].Clear();
                _rtCompletedDetails["TXF"]["夜盤"].Clear();
                _rtCompletedDetails["MXF"]["日盤"].Clear();
                _rtCompletedDetails["MXF"]["夜盤"].Clear();

                _rtNotifiedKeys.Clear();
                _rtLastNetSpeedsTop["TXF"] = null;
                _rtLastNetSpeedsTop["MXF"] = null;
                _rtLastNetSpeedsBot["TXF"] = null;
                _rtLastNetSpeedsBot["MXF"] = null;
                _txfLastMatchQty = -1;
                _mxfLastMatchQty = -1;
                
                _engine.ClearCache();
            }

            if (wndUnbrokenK != null)
            {
                if (wndUnbrokenK.Dispatcher.CheckAccess())
                {
                    wndUnbrokenK.Clear();
                }
                else
                {
                    wndUnbrokenK.Dispatcher.BeginInvoke(new Action(() => wndUnbrokenK.Clear()));
                }
            }
        }

        private void ProcessReplayTick(TradeTick tick, string activeSession)
        {
            string baseSym = tick.Symbol;
            int price = tick.Price;
            string mt = tick.Time;
            double tVal = tick.TimeVal;
            TradeSide side = tick.Side;

            if (side == TradeSide.Unknown)
            {
                var prevTrades = _replaySymbolTrades[baseSym][activeSession];
                side = prevTrades.Count > 0 ? prevTrades[^1].Side : TradeSide.Outer;
            }

            _replaySymbolTrades[baseSym][activeSession].Add(new TradeTick(baseSym, mt, tVal, price, tick.Qty, side, tick.BestBp, tick.BestSp, activeSession));

            if (baseSym == "MXF")
            {
                _renderMxfPrice = price;
                Volatile.Write(ref _renderMxfTime, mt);
                _lastMxfPrice = price;
                _lastMxfTime = mt;
            }
            else if (baseSym == "TXF")
            {
                _renderTxfPrice = price;
                Volatile.Write(ref _renderTxfTime, mt);
                _lastTxfPrice = price;
            }

            // 復盤價格觸發下單監控
            string monthCode = _engine.GetMonthCode();
            string completeSymbol = baseSym + monthCode;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MonitorPositionsForStopLossAndTakeProfit(completeSymbol, price);
            }));
        }

        /// <summary>
        /// 瞬間 O(1) 反射重組 0 到給定索引的歷史狀態與 UI ( Slider 拖曳核心)。
        /// </summary>
        private void ReconstructReplayUpTo(int index)
        {
            if (_allParsedTicks.Count == 0 || index < 0 || index >= _allParsedTicks.Count)
                return;

            string activeSession = _currentReplaySession;
            ResetReplayTrack();

            var subset = _allParsedTicks.Take(index + 1).ToList();
            if (subset.Count == 0) return;

            lock (_rtLock)
            {
                foreach (var tick in subset)
                {
                    ProcessReplayTick(tick, activeSession);
                }

            }

            lblVirtualTime.Content = $"復盤時間: {subset[^1].Time}";
            
            // 【修正】強制突破 1000ms 降頻限制，讓使用者拖曳進度條時必定能觸發一次完整的 K 線重繪
            _lastHeavyCalcTimeMs = 0;

            // 觸發非同步重繪與分析
            _analysisEvent.Set();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_allParsedTicks.Count == 0) return;

            if (_replayTask != null && !_replayCts!.IsCancellationRequested)
            {
                // 目前正在播放，執行暫停
                StopReplayTaskOnly();
                btnPlayPause.Content = "▶ 播放";
                lblStatus.Content = "復盤暫停";
            }
            else
            {
                // 目前是暫停/停止，執行播放
                _isReplaying = true;
                btnPlayPause.Content = "⏸ 暫停";
                lblStatus.Content = "復盤回放中...";
                klineChart?.EnableAutoRange();

                _replayCts = new CancellationTokenSource();
                int startIdx = (int)sldProgress.Value;
                if (startIdx >= _allParsedTicks.Count - 1)
                {
                    startIdx = 0;
                    sldProgress.Value = 0;
                }

                _replayTask = Task.Run(() => ReplayLoopAsync(startIdx, _replayCts.Token));
            }
        }

        private void StopReplayTaskOnly()
        {
            if (_replayCts != null)
            {
                _replayCts.Cancel();
                try
                {
                    _replayTask?.Wait(500);
                }
                catch { }
                _replayCts.Dispose();
                _replayCts = null;
                _replayTask = null;
            }
        }

        private void BtnStopReplay_Click(object sender, RoutedEventArgs e)
        {
            StopReplay();
            lblStatus.Content = "已無縫切回盤中實時行情";

            if (_yuantaQuote != null && _isRealtimeUIEnabled)
            {
                lock (_rtLock)
                {
                    _isPreloading = true;
                }
                AppendLog("\n--- 復盤結束，執行預載與補齊背景 Tick ---");
                PreloadTodayLog();
            }
        }

        private void StopReplay()
        {
            _isReplaying = false;
            _uiGeneration++; // 遞增世代，讓復盤期間排入的過期分析結果自動失效
            StopReplayTaskOnly();

            // ── UI 控制項狀態全面還原 ──
            btnPlayPause.IsEnabled = false;
            btnPlayPause.Content = "▶ 播放";
            btnStopReplay.IsEnabled = false;
            sldProgress.IsEnabled = false;

            // 復盤日期清空
            txtReplayPath.Text = "";
            _currentReplayDir = null;

            // 速度歸回一倍速
            cboReplaySpeed.SelectedIndex = 0;

            // 進度橫桿拉回原點
            sldProgress.Value = 0;
            sldProgress.Maximum = 100;
            sldProgress.Minimum = 0;

            // 復盤時間清空
            lblVirtualTime.Content = "復盤時間: --:--:--";

            // 觀察 K 高 / 觀察 K 低 欄位清空
            txtObsHigh.Text = "";
            txtObsLow.Text = "";
            _engine._obs_high_entry_price = null;
            _engine._obs_low_entry_price = null;

            // 最高觀察 / 最低觀察 ComboBox 清空
            cboObsHigh.Text = "";
            cboObsHigh.Items.Clear();
            cboObsLow.Text = "";
            cboObsLow.Items.Clear();
            _engine._obs_high_price = null;
            _engine._obs_low_price = null;

            // 觀察狀態文字重設
            lblObsStatus.Text = "觀察: 待設定";

            // 重設 K線自動填入的快取時間戳，讓下次重新觸發自動填入
            _lastAutofillKlineTime = null;

            // 未破分 K 停損監控表單清空（不停止 Timer，保持監控待命）
            wndUnbrokenK.Clear();

            // 回放 Tick 陣列也清空
            _allParsedTicks = [];

            ResetReplayTrack();

            // 觸發重新分析，切回實時行情
            _analysisEvent.Set();
        }

        /// <summary>
        /// 背景回放高精播放 Task。
        /// 100% 還原「超過 60 秒的空檔自動瞬間跳過」、「20ms微型chunk非同步 sleep 響應」。
        /// </summary>
        private async Task ReplayLoopAsync(int startIndex, CancellationToken token)
        {
            try
            {
                int idx = startIndex;

            while (idx < _allParsedTicks.Count && !token.IsCancellationRequested)
            {
                var tick = _allParsedTicks[idx];
                string activeSession = _currentReplaySession;

                // 餵入回放 Tick
                lock (_rtLock)
                {
                    ProcessReplayTick(tick, activeSession);
                }

                // 批次計數器節流 (Batching)，高頻行情下合併重繪
                double speed = GetCurrentReplaySpeed();
                int batchLimit = speed >= 10.0 ? 30 : 5;
                _replayThrottleCounter++;
                if (_replayThrottleCounter >= batchLimit)
                {
                    _replayThrottleCounter = 0;
                    _analysisEvent.Set(); // 觸發背景分析重繪
                }

                // 異步安全分發更新進度與時間 label
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    lblVirtualTime.Content = $"復盤時間: {tick.Time}";
                    
                    // 暫停 Slide 觸發 ValueChanged 事件以防迴鎖
                    sldProgress.ValueChanged -= SldProgress_ValueChanged;
                    sldProgress.Value = idx;
                    sldProgress.ValueChanged += SldProgress_ValueChanged;
                }));

                // 計算延遲間隔
                if (idx + 1 < _allParsedTicks.Count)
                {
                    var nextTick = _allParsedTicks[idx + 1];
                    double timeDiff = nextTick.TimeVal - tick.TimeVal;

                    // 實作：超過 60 秒的空檔自動縮短至 3 秒等待
                    if (timeDiff > 60.0)
                    {
                        timeDiff = 3.0;
                    }

                    double delay = timeDiff / speed;

                    // 採用 20ms 微型 chunk 分段 await，確保暫停拖曳時瞬間回應
                    double elapsed = 0.0;
                    while (elapsed < delay && !token.IsCancellationRequested)
                    {
                        double sleepLen = Math.Min(0.02, delay - elapsed);
                        await Task.Delay((int)(sleepLen * 1000), token);
                        elapsed += sleepLen;
                        
                        // 支援播放中動態調整速度
                        speed = GetCurrentReplaySpeed();
                        delay = timeDiff / speed;
                    }
                }

                idx++;
            }

            // 播畢處理
            if (!token.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isReplaying = false;
                    btnPlayPause.Content = "▶ 播放";
                    lblStatus.Content = "復盤播畢";
                    _analysisEvent.Set();
                }));
            }
            }
            catch (OperationCanceledException)
            {
                // 暫停或停止播放時 Task 被取消是預期行為，靜默處理，完美避免報錯
            }
            catch (Exception ex)
            {
                // 升級：回放執行緒異常崩潰時，直接將詳細 Call Stack 輸出至監控日誌看板
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    AppendLog($"🚨【回放播放崩潰】播放背景執行緒發生未知錯誤: {ex.Message}\n{ex.StackTrace}");
                }));
            }
        }

        private double GetCurrentReplaySpeed()
        {
            return Dispatcher.Invoke(() =>
            {
                if (cboReplaySpeed.SelectedItem is not ComboBoxItem item) return 1.0;
                string text = item.Content.ToString() ?? "1x";

            if (text == "自訂")
            {
                if (double.TryParse(txtMaxSpeed.Text, out double val)) return val;
                return 100.0;
            }

            return text switch
            {
                "1x" => 1.0,
                "2x" => 2.0,
                "5x" => 5.0,
                "10x" => 10.0,
                "20x" => 20.0,
                "50x" => 50.0,
                _ => 1.0
            };
            });
        }

        // ==================== 7. Slider 拖曳與 ValueChanged 互動事件 ====================

        private void SldProgress_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_replayTask != null && !_replayCts!.IsCancellationRequested)
            {
                _wasPlayingBeforeDrag = true;
                StopReplayTaskOnly();
            }
            else
            {
                _wasPlayingBeforeDrag = false;
            }
        }

        private void SldProgress_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            int val = (int)sldProgress.Value;
            ReconstructReplayUpTo(val);

            if (_wasPlayingBeforeDrag)
            {
                btnPlayPause.Content = "⏸ 暫停";
                lblStatus.Content = "復盤回放中...";

                _replayCts = new CancellationTokenSource();
                _replayTask = Task.Run(() => ReplayLoopAsync(val, _replayCts.Token));
            }
        }

        private void SldProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 只有在非自動播放（拖曳中）時才執行實體狀態重構
            if (_replayTask == null)
            {
                ReconstructReplayUpTo((int)e.NewValue);
            }
        }

        // ==================== 8. UI 連動與表格選取交互事件 ====================

        private void DgObserver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (!_isProgrammaticSelection && !_isRestoringSelection)
                {
                    // 使用者手動選擇，清除先前的所有特別反白
                    foreach (var obs in _obsCollection)
                    {
                        obs.IsTargetPriceHighlighted = false;
                    }
                }

                // 如果使用者手動（或系統成功）選取了某個項目，就清除待處理的目標
                _targetHighlightStopLossPrice = null;
            }

            if (dgObserver.SelectedItem is not SimulationResult selected || selected.BestATime == "N/A" || string.IsNullOrEmpty(selected.BestATime))
            {
                dgKline.SelectedIndex = -1;
                klineChart?.SetObserverStopLossPrice(null);
                return;
            }

            int direction = 0;
            if (selected.Type != null)
            {
                if (selected.Type.Contains("做多")) direction = 1; // 多 (預設停損低)
                else if (selected.Type.Contains("做空")) direction = -1; // 空
            }

            if (selected.StopLossDisplay != "N/A" && selected.StopLossPrice > 0)
            {
                klineChart?.SetObserverStopLossPrice(selected.StopLossPrice, direction);
            }
            else
            {
                klineChart?.SetObserverStopLossPrice(null);
            }

            double aTVal = -1;
            
            if (selected.StopLossPrice > 0)
            {
                // 找出該停損價的源頭 A 點 (在當前趨勢中，最早出現此停損價的觀測項目)
                SimulationResult? sourceObs = null;
                int selectedIdx = _obsCollection.IndexOf(selected);
                
                if (selectedIdx >= 0)
                {
                    // 從當前選取的項目往前倒推，確保只在同一個連續趨勢內尋找
                    for (int i = selectedIdx; i >= 0; i--)
                    {
                        var o = _obsCollection[i];
                        
                        // 若遇到不同方向，或停損價已經改變，代表跨越到了別的趨勢，停止往前找
                        if (o.Type != selected.Type || o.StopLossPrice != selected.StopLossPrice)
                        {
                            break;
                        }
                        
                        // 只要這個項目的 A 點價等於目標停損價，就暫存為可能的源頭
                        // 因為是往前找，最後存下來的一定是這個連續區塊裡「最早」發生的一筆
                        if (o.BestAPrice == selected.StopLossPrice)
                        {
                            sourceObs = o;
                        }
                    }
                }

                // 防呆機制：若上述連續趨勢回溯找不到(例如跨日重啟或特殊邊界條件)
                // 退回舊邏輯，但強制限制時間不能晚於選取項目，並改由近到遠尋找最接近的那個 A 點
                if (sourceObs == null)
                {
                    double selectedTime = TimeParser.ParseTime(selected.BestATime);
                    if (_currentSessionName == "夜盤" && selectedTime <= 18000.0) selectedTime += 86400.0;

                    sourceObs = _obsCollection
                        .Where(o => o.BestAPrice == selected.StopLossPrice && o.Type == selected.Type)
                        .Where(o => {
                            double t = TimeParser.ParseTime(o.BestATime);
                            if (_currentSessionName == "夜盤" && t <= 18000.0) t += 86400.0;
                            return t <= selectedTime;
                        })
                        .OrderByDescending(o => {
                            double t = TimeParser.ParseTime(o.BestATime);
                            if (_currentSessionName == "夜盤" && t <= 18000.0) t += 86400.0;
                            return t;
                        })
                        .FirstOrDefault();
                }

                if (sourceObs != null && !string.IsNullOrEmpty(sourceObs.BestATime))
                {
                    aTVal = TimeParser.ParseTime(sourceObs.BestATime);
                }
                else
                {
                    aTVal = TimeParser.ParseTime(selected.BestATime);
                }
            }
            else
            {
                aTVal = TimeParser.ParseTime(selected.BestATime);
            }

            if (_currentSessionName == "夜盤" && aTVal <= 18000.0) aTVal += 86400.0;
            if (aTVal <= 0) return;

            int targetRowIdx = -1;

            // 尋找 K線 繫結資料中對應時間範圍的 K棒 索引
            for (int i = 0; i < _klineCollection.Count; i++)
            {
                string label = _klineCollection[i].TimeLabel;
                try
                {
                    var times = label.Split('~');
                    var startParts = times[0].Split(':');
                    var endParts = times[1].Split(':');
                    
                    int sh = int.Parse(startParts[0]);
                    int sm = int.Parse(startParts[1]);
                    int eh = int.Parse(endParts[0]);
                    int em = int.Parse(endParts[1]);

                    double kStart = (sh * 60 + sm) * 60.0;
                    double kEnd = (eh * 60 + em) * 60.0;

                    if (_currentSessionName == "夜盤")
                    {
                        if (kStart <= 18000.0) kStart += 86400.0;
                        if (kEnd <= 18000.0) kEnd += 86400.0;
                    }

                    if (kStart <= aTVal && aTVal < kEnd)
                    {
                        targetRowIdx = i;
                        break;
                    }
                }
                catch { }
            }

            if (targetRowIdx >= 0)
            {
                // 因為我們現在 A 點是盤中即時偵測，所以直接對準當前這根 K 棒
                int targetKlineRow = targetRowIdx;
                
                // 暫時抑制 DgKline 的預設選擇連動，讓我們可以手動指定要對焦的價格 (停損價)
                bool wasProgrammatic = _isProgrammaticSelection;
                _isProgrammaticSelection = true;
                dgKline.SelectedIndex = targetKlineRow;
                _isProgrammaticSelection = wasProgrammatic;
                
                int? priceToHighlight = (selected.StopLossPrice > 0) ? selected.StopLossPrice : null;
                
                // 如果是系統自動背景還原（例如即時行情更新），不要強制捲動畫面，讓使用者自由瀏覽
                // 如果是使用者點擊未破分K監控觸發的導航 (_isNavigatingToHighlight)，則強制捲動與聚焦
                if (!_isRestoringSelection && (!_isProgrammaticSelection || _isNavigatingToHighlight))
                {
                    dgKline.ScrollIntoView(_klineCollection[targetKlineRow]);
                    klineChart?.FocusCandle(targetKlineRow, priceToHighlight);
                }
                else
                {
                    klineChart?.SetHighlightIndexOnly(targetKlineRow, priceToHighlight);
                }
            }
        }

        private void DgKline_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgKline.SelectedIndex == -1 || _klineCollection.Count == 0)
            {
                klineChart.ClearCrosshair();
                return;
            }
            int idx = dgKline.SelectedIndex;
            if (idx >= 0 && idx < _klineCollection.Count)
            {
                if (_isRestoringSelection || _isProgrammaticSelection)
                {
                    klineChart.SetHighlightIndexOnly(idx);
                }
                else
                {
                    klineChart.FocusCandle(idx);
                }
            }
        }

        /// <summary>
        /// 點選已被選取的行時手動取消反白選取，防護點點點狀態異常。
        /// 若點擊的是 CheckBox 則不予攔截，讓勾選狀態能一次到位切換。
        /// </summary>
        private void DataGrid_CancelSelection(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid dg) return;

            // 1. 檢查滑鼠點擊的元素及其父元素是否包含 CheckBox，若是則直接豁免，讓事件穿透
            DependencyObject? currentDep = e.OriginalSource as DependencyObject;
            while (currentDep != null)
            {
                if (currentDep is System.Windows.Controls.CheckBox)
                {
                    return; // 點擊的是 CheckBox，直接返回，不攔截且不取消反白
                }
                currentDep = VisualTreeHelper.GetParent(currentDep);
            }

            // 2. 獲取滑鼠點擊下的資料行物件
            DependencyObject dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow)
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (dep is DataGridRow row)
            {
                if (row.IsSelected)
                {
                    dg.SelectedIndex = -1; // 清空選取 (取消反白)
                    e.Handled = true;     // 攔截事件，防護原生再次將其自動選取
                }
            }
        }

        private void TxtOutput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 移除強制選擇到文件開頭的邏輯，避免滑鼠選取複製時畫面跳到最上方
            // 讓 RichTextBox 保留原生的選取行為
        }

        private void IntervalStat_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Models.IntervalStat stat)
            {
                string name = stat.IntervalName.Replace(" 分K", "").Replace("分K", "").Trim();
                foreach (ComboBoxItem item in cboKlineInterval.Items)
                {
                    if (item.Content?.ToString() == name)
                    {
                        cboKlineInterval.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        // ==================== 9. 底部參數更改同步槽 ====================

        

        private void CboKlineInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (cboKlineInterval.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out int val))
            {
                _currentKlineInterval = val;
                klineChart.EnableAutoRange(); // K棒間隔變更強制 autoRange 重對焦
                _analysisEvent.Set();

                // 離線模式下直接重繪
                ReplotOfflineData();
            }
        }

        private void CboBacktestDays_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            // 動態調整天數後重新進行回測
            if (cboBacktestDays?.SelectedItem is ComboBoxItem item)
            {
                string text = item.Content.ToString() ?? "60";
                _currentTargetDays = text == "全部" ? 0 : int.Parse(text);
                
                klineChart?.EnableAutoRange();
                _analysisEvent.Set();
            }
        }

        private void CboReplaySpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 即時反應速度變更，不需要重啟
        }

        private void TxtMaxSpeed_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtMaxSpeed.Text, out _))
            {
                txtMaxSpeed.Text = "100";
            }
        }

        private void CboObsHigh_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (int.TryParse(cboObsHigh.Text, out int val))
            {
                _engine._obs_high_price = val;
                lblObsStatus.Text = $"觀察: 最高觀察 {val}";
            }
        }

        private void CboObsLow_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (int.TryParse(cboObsLow.Text, out int val))
            {
                _engine._obs_low_price = val;
                lblObsStatus.Text = $"觀察: 最低觀察 {val}";
            }
        }

        private void TxtObsHigh_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && int.TryParse(txtObsHigh.Text, out int val))
            {
                _engine._obs_high_entry_price = val;
                AppendLog($"【觀察】做空手動設定為: {val}");
            }
        }

        private void TxtObsLow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && int.TryParse(txtObsLow.Text, out int val))
            {
                _engine._obs_low_entry_price = val;
                AppendLog($"【觀察】做多手動設定為: {val}");
            }
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            // 如果目前未連線，則不處理或直接呼叫啟動連線
            if (_yuantaQuote == null && _axHost == null)
            {
                AppendLog("\n--- 尚未連線即時行情，改為啟動連線 ---");
                StartRealtime();
                return;
            }

            AppendLog("\n--- 開始手動更新：清空當前快取並重新預載 ---");
            btnUpdate.IsEnabled = false;

            lock (_rtLock)
            {
                _isPreloading = true;

                // 清空所有實時快取與狀態
                _liveSymbolTrades["TXF"]["日盤"].Clear();
                _liveSymbolTrades["TXF"]["夜盤"].Clear();
                _liveSymbolTrades["MXF"]["日盤"].Clear();
                _liveSymbolTrades["MXF"]["夜盤"].Clear();

                _rtState["TXF"]["日盤"].Reset();
                _rtState["TXF"]["夜盤"].Reset();
                _rtState["MXF"]["日盤"].Reset();
                _rtState["MXF"]["夜盤"].Reset();
                
                _rtTriggers["TXF"]["日盤"].Clear();
                _rtTriggers["TXF"]["夜盤"].Clear();
                _rtTriggers["MXF"]["日盤"].Clear();
                _rtTriggers["MXF"]["夜盤"].Clear();

                _rtCompletedDetails["TXF"]["日盤"].Clear();
                _rtCompletedDetails["TXF"]["夜盤"].Clear();
                _rtCompletedDetails["MXF"]["日盤"].Clear();
                _rtCompletedDetails["MXF"]["夜盤"].Clear();

                _rtNotifiedKeys.Clear();
                _rtLastNetSpeedsTop["TXF"] = null;
                _rtLastNetSpeedsTop["MXF"] = null;
                _rtLastNetSpeedsBot["TXF"] = null;
                _rtLastNetSpeedsBot["MXF"] = null;
                _txfLastMatchQty = -1;
                _mxfLastMatchQty = -1;
                _isRecovering = true;

                // 徹底清空渲染快取，防止 UpdateUI 抓到舊時間與舊價格
                _lastMxfPrice = null;
                _lastMxfTime = "";
                _renderMxfPrice = 0;
                _renderMxfTime = "";
                _renderTxfPrice = 0;
                _renderTxfTime = "";
                _lastRenderedMxfPrice = 0;
                _lastRenderedMxfTime = "";
                _lastRenderedTxfPrice = 0;
                _lastRenderedTxfTime = "";

                _engine.ClearCache();
                _lastHeavyCalcTimeMs = 0;
                _lastKlineData.Clear();
                _lastSimulationResults.Clear();
                _lastSharedResultsMap = null;
                _lastRtStatusSnapshot.Clear();

                _uiGeneration++; // 阻斷過期背景任務的 UI 推送

                // 透過 Dispatcher 清空 UI
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _klineCollection.Clear();
                    _obsCollection.Clear();
                    _intervalStatsCollection.Clear();
                    klineChart?.Reset();

                    // 清空未破分 K 停損監控與趨勢方向表單
                    wndUnbrokenK?.Clear();

                    // 清空速差與極值資訊區
                    lblSpeedTxf.Text = "大臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → --";
                    lblSpeedMxf.Text = "小臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → --";
                    
                    runMaxInfo.Text = "最高價: -- (--)";
                    runMinInfo.Text = "最低價: -- (--)";
                    runAmpInfo.Text = "振幅: --";
                    
                    lblConsensusDir.Text = "| 共識: --";
                    lblTxfNetSpeed.Text = "| 大臺速差: --";
                    lblMxfNetSpeed.Text = "| 小臺速差: --";
                    lblLivePrice.Text = "| 成交價: --";



                    // 清空底部觀察狀態設定值
                    txtObsHigh.Text = "";
                    txtObsLow.Text = "";
                    cboObsHigh.Text = "";
                    cboObsHigh.Items.Clear();
                    cboObsLow.Text = "";
                    cboObsLow.Items.Clear();
                    
                    if (lblObsN != null) lblObsN.Content = "觀察N: --";
                    if (lblObsStatus != null) lblObsStatus.Text = "觀察: 待設定";
                }));
            }

            // 執行今日日誌預載 (該函式內包含背景非同步載入並於完成後開啟按鈕)
            PreloadTodayLog();
        }

        private void BtnRealtime_Click(object sender, RoutedEventArgs e)
        {
            ToggleRealtime();
        }

        private void BtnFill_Click(object sender, RoutedEventArgs e)
        {
            // 空實作
        }

        private string GetQuantReportStatus()
        {
            string reportPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reports", "advanced_quant_report_merged.md");
            if (!System.IO.File.Exists(reportPath))
            {
                return "未找到 advanced_quant_report_merged.md，極值使用預設值";
            }

            try
            {
                var qParams = _engine.LoadQuantParams("TXF", _currentTargetDays);
                string sourceStr = qParams.TryGetValue("source", out var val) && val != null ? val.ToString()! : "未知來源";

                if (sourceStr.Contains("動態載入"))
                {
                    return $"已載入極值: {sourceStr}";
                }
                else
                {
                    return "極值載入失敗(格式異常)，使用預設值";
                }
            }
            catch
            {
                return "極值載入發生錯誤，使用預設值";
            }
        }

        /// <summary>
        /// 處理未破停損總數點擊事件。
        /// </summary>
        public void FocusObserverOnStopLossPrice(string price)
        {
            _targetHighlightStopLossPrice = price;
            ApplyTargetHighlight();
        }

        private void ApplyTargetHighlight()
        {
            if (!string.IsNullOrEmpty(_targetHighlightStopLossPrice))
            {
                // 有新的搜尋目標時，清除所有的特殊反白標記
                foreach (var obs in _obsCollection)
                {
                    obs.IsTargetPriceHighlighted = false;
                }
            }
            
            if (string.IsNullOrEmpty(_targetHighlightStopLossPrice)) return;
            
            var matches = _obsCollection.Where(o => !o.IsBroken && o.StopLossPrice.ToString() == _targetHighlightStopLossPrice).ToList();
            if (matches.Count == 0) return;

            SimulationResult? targetObs = null;

            var kHighs = matches.Where(o => o.Type != null && o.Type.Contains("做多")).ToList();
            var kLows = matches.Where(o => o.Type != null && o.Type.Contains("做空")).ToList();

            if (kHighs.Count > 0)
            {
                // 如果是觀察 K 高，尋找最低的 A 點價
                targetObs = kHighs.OrderBy(o => o.BestAPrice).First();
            }
            else if (kLows.Count > 0)
            {
                // 如果是觀察 K 低，尋找最高的 A 點價
                targetObs = kLows.OrderByDescending(o => o.BestAPrice).First();
            }
            else
            {
                // 如果都不符合，沿用舊邏輯，尋找最早的觸發時間
                targetObs = matches.OrderBy(o => {
                    double t = TimeParser.ParseTime(o.TrigTime);
                    if (_currentSessionName == "夜盤" && t <= 18000.0) t += 86400.0;
                    return t;
                }).First();
            }

            if (targetObs != null)
            {
                targetObs.IsTargetPriceHighlighted = true; // 特別彰顯該欄位
                
                _isProgrammaticSelection = true;
                _isNavigatingToHighlight = true;
                dgObserver.SelectedItem = targetObs;
                _isNavigatingToHighlight = false;
                
                // 第一層防護：強制更新 UI 佈局
                dgObserver.UpdateLayout();
                dgObserver.ScrollIntoView(targetObs);
                
                // 第二層防護：延遲執行，確保 WPF VirtualizingStackPanel 真正生成項目後置中捲動
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    dgObserver.ScrollIntoView(targetObs);
                    dgObserver.UpdateLayout();

                    // 尋找 ScrollViewer 並計算置中
                    var scrollViewer = GetVisualChild<ScrollViewer>(dgObserver);
                    if (scrollViewer != null)
                    {
                        double targetIndex = dgObserver.Items.IndexOf(targetObs);
                        if (targetIndex != -1)
                        {
                            bool isItemBased = Math.Abs(scrollViewer.ExtentHeight - dgObserver.Items.Count) < 5;
                            if (isItemBased)
                            {
                                double centerOffset = targetIndex - (scrollViewer.ViewportHeight / 2.0);
                                scrollViewer.ScrollToVerticalOffset(Math.Max(0, centerOffset));
                            }
                            else
                            {
                                double avgRowHeight = scrollViewer.ExtentHeight / dgObserver.Items.Count;
                                double targetPixelOffset = (targetIndex * avgRowHeight) - (scrollViewer.ViewportHeight / 2.0);
                                scrollViewer.ScrollToVerticalOffset(Math.Max(0, targetPixelOffset));
                            }
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                
                _isProgrammaticSelection = false;
                
                // 只有成功找到並選取後，才清除目標，避免過早被舊的即時 tick 清掉
                _targetHighlightStopLossPrice = null;
            }
        }

        private static T? GetVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int numVisuals = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < numVisuals; i++)
            {
                DependencyObject v = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (v is T child)
                {
                    return child;
                }
                T? childOfChild = GetVisualChild<T>(v);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        private void TriggerReanalyze()
        {
            string qStatus = GetQuantReportStatus();
            lblStatus.Content = $"處理中，請稍候... | {qStatus}";
            btnUpdate.IsEnabled = false;
            klineChart.EnableAutoRange();

            string todayStr = DateTime.Now.ToString("yyyyMMdd");
            string todayLog = Path.Combine(GetLogsDirectory(), todayStr, "event.log");

            if (File.Exists(todayLog))
            {
                Task.Run(() =>
                {
                    var filePaths = new[] { (todayLog, (Func<double, bool>)(t => true)) };
                    var (success, result, status) = RunAnalysisSync(filePaths, "MXF", _currentTargetDays, ignoreTimeCheck: true);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        btnUpdate.IsEnabled = true;
                        if (success && result is Dictionary<string, string> reports)
                        {
                            OnAnalysisCompletedSuccess(reports);
                        }
                        else
                        {
                            lblStatus.Content = $"今天無相關日誌可分析 | {qStatus}";
                        }
                    }));
                });
            }
            else
            {
                lblStatus.Content = $"今天無相關日誌可分析 | {qStatus}";
                btnUpdate.IsEnabled = true;
            }
        }

        // ==================== 10. 輔助公用方法 ====================

        public int GetObsN() => _currentObsN;

        public List<string> GetKlineIntervals()
        {
            var intervals = new List<string>();
            foreach (ComboBoxItem item in cboKlineInterval.Items)
            {
                intervals.Add(item.Content.ToString() ?? "30");
            }
            return intervals;
        }

        public List<(string SessionName, IReadOnlyList<TradeTick> Trades, List<SimulationResult> TxfSigs, List<SimulationResult> MxfSigs)> GatherSessionDataSnapshot()
        {
            var list = new List<(string SessionName, IReadOnlyList<TradeTick> Trades, List<SimulationResult> TxfSigs, List<SimulationResult> MxfSigs)>();
            
            if (_isReplaying)
            {
                string session = _currentReplaySession;
                IReadOnlyList<TradeTick> trades;
                lock (_rtLock)
                {
                    trades = _replaySymbolTrades["MXF"][session];
                }
                var txfSigs = GetSimResultsFromSnapshot("TXF", session);
                var mxfSigs = GetSimResultsFromSnapshot("MXF", session);
                list.Add((session, trades, txfSigs, mxfSigs));
            }
            else if (_yuantaQuote != null)
            {
                string session = _currentRealtimePort == 442 ? "夜盤" : "日盤";
                IReadOnlyList<TradeTick> trades;
                lock (_rtLock)
                {
                    trades = _liveSymbolTrades["MXF"][session];
                }
                var txfSigs = GetSimResultsFromSnapshot("TXF", session);
                var mxfSigs = GetSimResultsFromSnapshot("MXF", session);
                list.Add((session, trades, txfSigs, mxfSigs));
            }
            else if (App.Current.Resources.Contains("_temp_offline_trades"))
            {
                var dict = (Dictionary<string, Dictionary<string, List<TradeTick>>>)App.Current.Resources["_temp_offline_trades"];
                foreach (var session in new[] { "日盤", "夜盤" })
                {
                    if (dict.TryGetValue("MXF", out var innerDict) && innerDict.TryGetValue(session, out var trades))
                    {
                        var txfSigs = GetSimResultsFromSnapshot("TXF", session);
                        var mxfSigs = GetSimResultsFromSnapshot("MXF", session);
                        list.Add((session, trades, txfSigs, mxfSigs));
                    }
                }
            }

            return list;
        }

        private void RefreshObserverComboboxes()
        {
            var sessionData = GatherSessionDataSnapshot();
            if (sessionData == null || sessionData.Count == 0) return;

            var highZonePrices = new HashSet<int>();
            var lowZonePrices = new HashSet<int>();

            foreach (var (sessionName, trades, txfSigs, mxfSigs) in sessionData)
            {
                var groups = new[] { ("TXF", txfSigs), ("MXF", mxfSigs) };
                foreach (var (sym, sigs) in groups)
                {
                    var qParams = _engine.LoadQuantParams(sym, _currentTargetDays);
                    foreach (var d in sigs)
                    {
                        string speedInfo = d.Tags.FirstOrDefault() ?? "";
                        var (isNormal, isContradiction) = TradingEngine.ClassifyTrigger(d.DisplayTitle, speedInfo);

                        if (!(isNormal || isContradiction)) continue;

                        string currentType = d.DisplayTitle.Contains("最高") ? "最高" : "最低";
                        string side = currentType == "最高" ? "top" : "bottom";

                        try
                        {
                            var tDict = (Dictionary<string, (int p50, int p75, int p90)>)qParams[side == "top" ? "time_top" : "time_bottom"];
                            int totalM = ParseTimeToMinutes(d.BestATime);
                            if (totalM < 0) throw new Exception("Invalid time");
                            if (sessionName == "夜盤" && totalM < 900) totalM += 1440;

                            int? p50 = null, p75 = null;
                            foreach (var kvp in tDict)
                            {
                                if (kvp.Key.Contains(sessionName))
                                {
                                    string timePart = kvp.Key.Split(' ')[1].Trim();
                                    if (timePart.Contains('-'))
                                    {
                                        var sStr = timePart.Split('-')[0];
                                        var eStr = timePart.Split('-')[1];
                                        int sMins = int.Parse(sStr.Split(':')[0]) * 60 + int.Parse(sStr.Split(':')[1]);
                                        int eMins = int.Parse(eStr.Split(':')[0]) * 60 + int.Parse(eStr.Split(':')[1]);
                                        if (sessionName == "夜盤")
                                        {
                                            if (sMins < 900) sMins += 1440;
                                            if (eMins < 900) eMins += 1440;
                                        }
                                        if (sMins <= totalM && totalM <= eMins)
                                        {
                                            p50 = kvp.Value.p50;
                                            p75 = kvp.Value.p75;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (p50.HasValue && p75.HasValue)
                            {
                                int priceVal = d.BestAPrice;
                                if (side == "top")
                                {
                                    highZonePrices.Add(priceVal + p50.Value);
                                    highZonePrices.Add(priceVal + p75.Value);
                                }
                                else
                                {
                                    lowZonePrices.Add(priceVal - p50.Value);
                                    lowZonePrices.Add(priceVal - p75.Value);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            var sortedHigh = highZonePrices.ToList();
            sortedHigh.Sort();

            var sortedLow = lowZonePrices.ToList();
            sortedLow.Sort();
            sortedLow.Reverse();

            // 更新 ComboBox
            string currHigh = cboObsHigh.Text;
            string currLow = cboObsLow.Text;

            cboObsHigh.Items.Clear();
            foreach (var p in sortedHigh) cboObsHigh.Items.Add(p.ToString());

            cboObsLow.Items.Clear();
            foreach (var p in sortedLow) cboObsLow.Items.Add(p.ToString());

            if (string.IsNullOrEmpty(currHigh) && sortedHigh.Count > 0)
            {
                cboObsHigh.Text = sortedHigh[0].ToString();
                _engine._obs_high_price = sortedHigh[0];
            }
            else cboObsHigh.Text = currHigh;

            if (string.IsNullOrEmpty(currLow) && sortedLow.Count > 0)
            {
                cboObsLow.Text = sortedLow[0].ToString();
                _engine._obs_low_price = sortedLow[0];
            }
            else cboObsLow.Text = currLow;
        }

        private static void SetWidgetStyleLazy(TextBlock widget, string colorHex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
                brush.Freeze();
                widget.Foreground = brush;
            }
            catch { }
        }

        /// <summary>
        /// 執行緒安全地向對應的 RichTextBox 添加著色日誌，並提供分流與記憶體快取。
        /// 對帳務日誌實施精簡過濾，防止過多 API 通訊細節造成 UI 雜亂。
        /// </summary>
        /// <param name="text">要寫入的日誌文字內容。</param>
        /// <param name="clear">是否在寫入前清空日誌快取與控制項。</param>
        /// <param name="forceScrollToEnd">是否強制將滾動條拉至最底部。</param>
        private void AppendLog(string text, bool clear = false, bool forceScrollToEnd = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(text, clear, forceScrollToEnd)));
                return;
            }
            // 初始化並獲取全域系統日誌與帳務日誌的資源快取清單
            if (!App.Current.Resources.Contains("_system_logs"))
            {
                App.Current.Resources["_system_logs"] = new List<string>();
            }
            var systemLogs = (List<string>)App.Current.Resources["_system_logs"];

            if (!App.Current.Resources.Contains("_query_logs"))
            {
                App.Current.Resources["_query_logs"] = new List<string>();
            }
            var queryLogs = (List<string>)App.Current.Resources["_query_logs"];

            // 若請求清除，則同步清空所有快取清單與對應的 UI 日誌看板
            if (clear)
            {
                systemLogs.Clear();
                queryLogs.Clear();
                LogHighlighter.AppendLog(txtOutput, "", clear: true);
                LogHighlighter.AppendLog(txtQueryOutput, "", clear: true);
            }

            if (!string.IsNullOrEmpty(text))
            {
                // 判斷日誌內容是否屬於交易、庫存或帳務相關，若是則分流至專屬的查詢日誌區
                bool isQueryLog = text.Contains("【帳務】") || 
                                  text.Contains("【帳務資料】") || 
                                  text.Contains("【帳務錯誤】") || 
                                  text.Contains("【帳務解析錯誤】") || 
                                  text.Contains("【庫存更新】") || 
                                  text.Contains("【下單】") || 
                                  text.Contains("【委託】") ||
                                  text.Contains("【成交回報】");

                string timeStr = DateTime.Now.ToString("HH:mm:ss");
                string logEntry = text.StartsWith("\n") ? $"\n[{timeStr}] {text.TrimStart()}" : $"[{timeStr}] {text}";

                if (isQueryLog)
                {
                    // 偵測並過濾掉雜亂的 API 傳輸細節與解析過程日誌
                    bool isMessyLog = text.Contains("UserDefinsFunc") || 
                                      text.Contains("已送出國內庫存查詢") || 
                                      text.Contains("自訂功能回報") || 
                                      text.Contains("開始解析 FA") || 
                                      text.Contains("單行鍵值對") || 
                                      text.Contains("匹配到") || 
                                      text.Contains("【帳務資料】") || 
                                      text.Contains("未偵測到明確標題");

                    if (isMessyLog)
                    {
                        // 雜亂日誌不在 UI 渲染，但於後台輸出以便排錯
                        System.Diagnostics.Debug.WriteLine($"[帳務細節] {logEntry}");
                        return;
                    }

                    // 寫入帳務查詢日誌快取，上限保留 50 筆
                    lock (_logLock)
                    {
                        queryLogs.Add(logEntry);
                        if (queryLogs.Count > 50)
                        {
                            queryLogs.RemoveAt(0);
                        }
                    }
                    // 渲染至右側的帳務查詢日誌，強制滾動至最底端
                    LogHighlighter.AppendLog(txtQueryOutput, logEntry, clear: false, forceScrollToEnd: true);
                }
                else
                {
                    // 寫入一般監控日誌快取，上限保留 50 筆
                    lock (_logLock)
                    {
                        systemLogs.Add(logEntry);
                        if (systemLogs.Count > 50)
                        {
                            systemLogs.RemoveAt(0);
                        }
                    }
                    // 渲染至左側的主監控日誌
                    LogHighlighter.AppendLog(txtOutput, text, clear: false, forceScrollToEnd);
                }
            }
        }

        private static int ParseTimeToMinutes(string timeStr)
        {
            if (string.IsNullOrEmpty(timeStr) || timeStr == "N/A") return -1;
            try
            {
                timeStr = timeStr.Trim();
                if (timeStr.Contains(':'))
                {
                    var parts = timeStr.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
                        return h * 60 + m;
                }
                else
                {
                    if (timeStr.Contains('.'))
                        timeStr = timeStr.Split('.')[0];

                    string digits = new string(timeStr.Where(char.IsDigit).ToArray());
                    if (digits.Length >= 3)
                    {
                        int h = 0, m = 0;
                        if (digits.Length >= 4)
                        {
                            h = int.Parse(digits.Substring(0, 2));
                            m = int.Parse(digits.Substring(2, 2));
                        }

                        if (digits.Length == 5 || digits.Length == 3 || h > 23 || m > 59)
                        {
                            h = int.Parse(digits.Substring(0, 1));
                            m = int.Parse(digits.Substring(1, 2));
                        }

                        if (h >= 0 && h <= 23 && m >= 0 && m <= 59)
                            return h * 60 + m;
                    }
                }
            }
            catch { }
            return -1;
        }

        private static CheckSessionPortResult CheckSessionPort()
        {
            DateTime now = DateTime.UtcNow.AddHours(8); // 台北時間
            int timeVal = now.Hour * 3600 + now.Minute * 60 + now.Second;
            int weekday = (int)now.DayOfWeek; // Sunday=0, Monday=1, ... Saturday=6

            // 週一至週五 08:30 ~ 14:49:59 為日盤 (1 <= weekday <= 5)
            bool isDay = (1 <= weekday && weekday <= 5) && (timeVal >= 8 * 3600 + 30 * 60 && timeVal < 14 * 3600 + 50 * 60);
            
            // 週一至週五 14:50 之後，或者週二至週六 08:30 之前為夜盤
            bool isNight1 = (1 <= weekday && weekday <= 5) && (timeVal >= 14 * 3600 + 50 * 60);
            bool isNight2 = (2 <= weekday && weekday <= 6) && (timeVal < 8 * 3600 + 30 * 60);

            if (isDay) return new CheckSessionPortResult(443, "日盤");
            if (isNight1 || isNight2) return new CheckSessionPortResult(442, "夜盤");

            return new CheckSessionPortResult(443, "非交易時間 (預設用 443 待命)");
        }

        private readonly struct CheckSessionPortResult(int port, string session)
        {
            public int Port { get; } = port;
            public string Session { get; } = session;
            public void Deconstruct(out int port, out string session) { port = Port; session = Session; }
        }


        // ==================== 11. 共用區間統計計算邏輯 ====================
        private Dictionary<int, List<SimulationResult>> ComputeAllIntervalResults(string sessionName, IReadOnlyList<TradeTick> trades, List<SimulationResult> txfSigs, List<SimulationResult> mxfSigs, int obsN, int maxCount = -1)
        {
            var resultsMap = new Dictionary<int, List<SimulationResult>>();
            if (trades == null || trades.Count == 0 || _engine == null) return resultsMap;

            foreach (int interval in _allIntervals)
            {
                var (klineData, _) = _engine.CalcKlineData(sessionName, trades, txfSigs, mxfSigs, interval, maxCount);
                var simResults = _engine.CalcSimulationResults(sessionName, trades, klineData, obsN, true, null, maxCount);
                resultsMap[interval] = simResults;
            }
            return resultsMap;
        }

        private void UpdateIntervalStatsUI(Dictionary<int, List<SimulationResult>> resultsMap)
        {
            int totalShort = 0;
            int totalLong = 0;

            var uniqueResults = resultsMap.Values.FirstOrDefault() ?? new List<SimulationResult>();

            foreach (var r in uniqueResults)
            {
                if (r.Tags.Contains("history") || r.Tags.Contains("annotation")) continue;
                if (r.Type == "做多") totalLong++;
                if (r.Type == "做空") totalShort++;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _intervalStatsCollection.Clear();

                // lblTotalStats 已經移至 UnbrokenKMonitor
            }));
        }

        // ==================== 10. 元大下單/帳務 API 整合與今日損益查詢 ====================

        /// <summary>
        /// 執行緒安全地比對傳入帳號與當前監控帳號。
        /// 相容元大 API 可能傳回的 7 碼、10 碼（含分公司）或 12 碼（含市場別與分公司）帳號格式。
        /// </summary>
        /// <param name="acNo">API 事件回傳的帳號字串。</param>
        /// <returns>若匹配則傳回 true，否則傳回 false。</returns>
        private bool IsMatchingAccount(string? acNo)
        {
            // 放寬：若 API 回報未帶帳號（如退單、異常失敗），預設為相符，防止被過濾器誤殺
            if (string.IsNullOrEmpty(acNo)) return true;
            string target = acNo.Trim();
            if (string.IsNullOrEmpty(_currentAccount)) return false;

            // 長度完全一致時直接比對
            if (target == _currentAccount) return true;

            // 若傳回之帳號較長，檢查是否以當前帳號結尾（解決 3碼分公司+7碼帳號，或 2+3碼分公司+7碼帳號之相容性）
            if (target.Length > _currentAccount.Length && target.EndsWith(_currentAccount)) return true;

            return false;
        }

        private void InitYuantaOrd()
        {
            if (_yuantaOrd != null) return;

            _yuantaOrd = new YuantaOrdLib.YuantaOrdClass();
            _yuantaOrd.OnLogonS += OnOrdLogonS;
            _yuantaOrd.OnUserDefinsFuncResult += OnOrdUserDefinsFuncResult;
            _yuantaOrd.OnOrdMatF += OnOrdMatF;
            _yuantaOrd.OnOrdResult += OnOrdResult;
            _yuantaOrd.OnOrdRptF += OnOrdRptF;
            _pnlCalculator.Reset();
        }

        private int LoginYuantaOrd(string user, string pwd)
        {
            if (_yuantaOrd == null) return -1;
            if (_isOrdLoggedIn || _isOrdLoggingIn)
            {
                AppendLog($"【帳務】已在登入狀態或正在登入，跳過連線呼叫。已登入: {_isOrdLoggedIn}, 登入中: {_isOrdLoggingIn}");
                return 2; 
            }

            _isOrdLoggingIn = true;
            int ret = _yuantaOrd.SetFutOrdConnection(user, pwd, "api.yuantafutures.com.tw", 80);
            if (ret != 2 && ret != 3) // 2 代表成功，3 代表連線中/送出中
            {
                _isOrdLoggingIn = false;
            }
            return ret;
        }

        private void OnOrdLogonS(int TLinkStatus, string AccList, string Casq, string Cast)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"【帳務狀態】OnLogonS: {TLinkStatus}, 帳號清單: {AccList.Trim()}");

                if (TLinkStatus == 2) // 登入連線成功
                {
                    _isOrdLoggedIn = true;
                    _isOrdLoggingIn = false;

                    string accListStr = AccList.Trim();
                    if (!string.IsNullOrEmpty(accListStr))
                    {
                        string[] accounts = accListStr.Split(';');
                        foreach (var acc in accounts)
                        {
                            // 過濾非期貨帳號 (通常期貨帳號開頭為 '2')
                            if (acc.Length > 2 && acc[0] == '2')
                            {
                                string accountInfo = acc.Substring(2); // 例如 "F009808900" 或 "F00-9808900"

                                // 元大下單格式通常是: 分公司-帳號 或 3位分公司+7位帳號
                                if (accountInfo.Contains("-"))
                                {
                                    string[] parts = accountInfo.Split('-');
                                    if (parts.Length >= 3)
                                    {
                                        _currentBranch = parts[0];
                                        _currentAccount = parts[1] + parts[2];
                                    }
                                    else if (parts.Length == 2)
                                    {
                                        _currentBranch = parts[0];
                                        _currentAccount = parts[1];
                                    }
                                }
                                else if (accountInfo.Length >= 10) // 例如 "F009808900" (前3碼分公司，後7碼帳號)
                                {
                                    _currentBranch = accountInfo.Substring(0, 3);
                                    _currentAccount = accountInfo.Substring(3);
                                }
                                else
                                {
                                    _currentBranch = "F00";
                                    _currentAccount = accountInfo;
                                }

                                lblPnLAccount.Text = $"帳號: {_currentBranch}-{_currentAccount}";
                                AppendLog($"【帳務】成功取得期貨監控帳號: {_currentBranch}-{_currentAccount}，自動發送今日平倉損益查詢 (FA003)...");

                                // 自動觸發第一次查詢今日平倉損益與庫存
                                QueryTodayPnL();
                                QueryCurrentPositions();
                                break;
                            }
                        }
                    }
                }
                else if (TLinkStatus < 0) // 登入失敗 (例如 -101)
                {
                    _isOrdLoggedIn = false;
                    _isOrdLoggingIn = false;
                    lblPnLAccount.Text = $"帳號: 登入失敗 ({TLinkStatus})";
                    lblTodayPnL.Text = "NT$ 0";
                    lblTodayPnL.Foreground = Brushes.LightGray;
                }
            }));
        }

        private void QueryTodayPnL()
        {
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount))
            {
                AppendLog("【帳務】查詢失敗: 帳務 API 未登入或未取得有效帳號。");
                return;
            }

            // 呼叫 UserDefinsFunc，代碼 FA003
            string param = $"Func=FA003|bhno={_currentBranch}|acno={_currentAccount}|suba=|type=1|currency=TWD";
            int ret = _yuantaOrd.UserDefinsFunc(param, "FA003");
            AppendLog($"【帳務】UserDefinsFunc(FA003) 送出，回傳代碼: {ret} (0 代表送出成功)");
        }

        private void QueryCurrentPositions()
        {
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount)) return;

            // 送出 FA002 (國內庫存總計)
            string param = $"Func=FA002|bhno={_currentBranch}|acno={_currentAccount}|suba=|kind=F|FC=N";
            int ret = _yuantaOrd.UserDefinsFunc(param, "FA002");
            AppendLog($"【帳務狀態】已送出國內庫存查詢 (FA002)，回傳代碼: {ret} (0 代表送出成功)");
        }


        private void OnOrdUserDefinsFuncResult(int RowCount, string Results, string WorkID)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"【帳務】自訂功能回報: WorkID={WorkID}, Rows={RowCount}, 內容={Results}");

                if (WorkID == "FA003" && RowCount > 0 && !string.IsNullOrEmpty(Results))
                {
                    try
                    {
                        string[] fields = Results.Split('|');
                        int colCount = fields.Length / RowCount;

                        AppendLog($"【帳務】開始解析 FA003 表格 ({RowCount} 行 * {colCount} 欄)...");

                        double totalCalculatedPnL = 0;
                        bool foundPnLField = false;
                        int pnlFieldIndex = -1;

                        if (RowCount == 1)
                        {
                            AppendLog($"【帳務】開始解析 FA003 單行鍵值對...");
                            foreach (string field in fields)
                            {
                                if (field.Trim().StartsWith("T_TOTAL_VALUE=", StringComparison.OrdinalIgnoreCase))
                                {
                                    string valStr = field.Substring(field.IndexOf('=') + 1);
                                    if (double.TryParse(valStr, out double val))
                                    {
                                        totalCalculatedPnL = val;
                                        foundPnLField = true;
                                        AppendLog($"【帳務】匹配到 T_TOTAL_VALUE (平倉損益): {val}");
                                    }
                                }
                                else if (field.Trim().StartsWith("CANUSE_MARGIN=", StringComparison.OrdinalIgnoreCase))
                                {
                                    string valStr = field.Substring(field.IndexOf('=') + 1);
                                    if (double.TryParse(valStr, out double val))
                                    {
                                        UpdateMarginUI(val);
                                    }
                                }
                            }
                        }
                        // 1. 如果第一行看起來是文字標題，我們掃描標題來尋找損益欄位索引
                        else if (RowCount > 1 && colCount > 0)
                        {
                            for (int j = 0; j < colCount; j++)
                            {
                                string colName = fields[j].Trim();
                                if (colName.Contains("損益") || colName.Contains("平倉損益") || colName.Contains("沖銷損益") ||
                                    colName.Equals("PNL", StringComparison.OrdinalIgnoreCase) || colName.Equals("PROFIT", StringComparison.OrdinalIgnoreCase) ||
                                    colName.Equals("GRANTAL", StringComparison.OrdinalIgnoreCase))
                                {
                                    pnlFieldIndex = j;
                                    foundPnLField = true;
                                    AppendLog($"【帳務】匹配到平倉損益欄位索引: [{j}] ({colName})");
                                    break;
                                }
                            }
                        }

                        // 2. 印出前幾列的內容供除錯
                        for (int i = 0; i < Math.Min(RowCount, 10); i++)
                        {
                            var rowFields = new List<string>();
                            for (int j = 0; j < colCount; j++)
                            {
                                int idx = i * colCount + j;
                                if (idx < fields.Length)
                                    rowFields.Add($"[{j}]:{fields[idx].Trim()}");
                            }
                            AppendLog($"【帳務資料】行 {i}: {string.Join(", ", rowFields)}");
                        }

                        // 3. 計算總損益：
                        if (foundPnLField && pnlFieldIndex != -1)
                        {
                            for (int i = 1; i < RowCount; i++)
                            {
                                int idx = i * colCount + pnlFieldIndex;
                                if (idx < fields.Length && double.TryParse(fields[idx], out double val))
                                {
                                    totalCalculatedPnL += val;
                                }
                            }
                        }

                        if (foundPnLField)
                        {
                            UpdatePnLUI(totalCalculatedPnL);
                            AppendLog($"【帳務】今日已平倉總損益: NT$ {totalCalculatedPnL}");
                        }
                        else
                        {
                            AppendLog("【帳務】未偵測到明確標題，將僅印出明細。以 FIFO 即時累加值顯示於 UI。");
                            UpdatePnLUI(_pnlCalculator.TotalPnL);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"【帳務解析錯誤】解析 FA003 查詢結果時出錯: {ex.Message}");
                    }
                }
                else if (WorkID == "FA002" && !string.IsNullOrEmpty(Results))
                {
                    try
                    {
                        // 輸出不受過濾的原始 Results 回顯，以便精確排查
                        AppendLog($"【庫存原始資料】RowCount={RowCount}, Results={Results}");

                        // 判斷是否為無部位之空資料
                        if (RowCount == 0 || Results.Contains("TOTAL_OFF_POSITION=0") || Results.Contains("查無資料"))
                        {
                            _currentPositionLots = 0;
                            _currentPositionCost = 0;
                            lblCurrentPosition.Text = "無部位";
                            AppendLog($"【庫存更新】目前無部位 (0 口)");
                        }
                        else
                        {
                            string[] fields = Results.Split('|');
                            int colCount = fields.Length / RowCount;

                            int cmdIdx = -1;
                            int posIdx = -1;
                            int priceIdx = -1;
                            int bsIdx = -1;
                            
                            // 1. 找尋標題列索引，並支援元大 API 定義的 SYMB (商品) 與 BS (買賣別)
                            for (int j = 0; j < colCount; j++)
                            {
                                string colName = fields[j].Trim().ToUpper();
                                if (colName == "COMMODITY" || colName == "COMMODITY_ID" || colName == "SYMB") cmdIdx = j;
                                if (colName == "OFF_POSITION" || colName == "QTY" || colName.Contains("POSITION")) posIdx = j;
                                if (colName == "PRICE" || colName == "AVG_PRICE" || colName.Contains("COST")) priceIdx = j;
                                if (colName == "BS" || colName == "BUY_SELL") bsIdx = j;
                            }

                            // 2. 解析後續資料列
                            if (cmdIdx != -1 && posIdx != -1 && priceIdx != -1)
                            {
                                int netLots = 0;       // 買賣相抵後的淨口數，多單為正值，空單為負值
                                double totalCost = 0;   // 所有部位的總加權成本 (價格 * 口數)
                                int totalLots = 0;     // 絕對值部位加總，用來作為計算加權均價的權重
                                
                                for (int i = 1; i < RowCount; i++)
                                {
                                    int rowBase = i * colCount;
                                    if (rowBase + cmdIdx < fields.Length)
                                    {
                                        string cmd = fields[rowBase + cmdIdx].Trim();
                                        
                                        // 簡單判斷：只要是大台 TXF 或小台 MXF 就納入部位計算 (可根據當前商品切換)
                                        if (cmd.StartsWith("TXF") || cmd.StartsWith("MXF"))
                                        {
                                            if (int.TryParse(fields[rowBase + posIdx], out int pos) && double.TryParse(fields[rowBase + priceIdx], out double price))
                                            {
                                                // 預設買賣別為買進 (多單)
                                                string bsVal = "B";
                                                if (bsIdx != -1 && rowBase + bsIdx < fields.Length)
                                                {
                                                    bsVal = fields[rowBase + bsIdx].Trim().ToUpper();
                                                }

                                                // 根據 B (買) 或 S (賣) 來加減淨部位口數與加權總成本
                                                if (bsVal == "B" || bsVal == "BUY")
                                                {
                                                    netLots += pos;
                                                    totalCost += price * pos;
                                                    totalLots += pos;
                                                }
                                                else if (bsVal == "S" || bsVal == "SELL")
                                                {
                                                    netLots -= pos;
                                                    totalCost += price * pos;
                                                    totalLots += pos;
                                                }
                                            }
                                        }
                                    }
                                }
                                
                                // 計算加權均價 (避免除以零)
                                double weightedAvgPrice = totalLots > 0 ? Math.Round(totalCost / totalLots, 2) : 0;

                                _currentPositionLots = netLots;
                                _currentPositionCost = weightedAvgPrice;

                                // 更新庫存 UI 面板文字
                                if (netLots == 0)
                                {
                                    lblCurrentPosition.Text = "無部位";
                                }
                                else
                                {
                                    string posDirection = netLots > 0 ? "多單" : "空單";
                                    lblCurrentPosition.Text = $"{posDirection} {Math.Abs(netLots)} 口 (成本: {weightedAvgPrice})";
                                }

                                AppendLog($"【庫存更新】取得最新部位: {lblCurrentPosition.Text}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"【帳務解析錯誤】解析 FA002 庫存結果時出錯: {ex.Message}");
                    }
                }
                
                // 為了除錯，如果是測試送出的 FA001 或 FA002 也把內容印出來
                if ((WorkID == "FA001" || WorkID == "FA002") && WorkID != "FA003")
                {
                    try
                    {
                        string[] fields = Results.Split('|');
                        int colCount = RowCount > 0 ? fields.Length / RowCount : 0;
                        for (int i = 0; i < Math.Min(RowCount, 5); i++)
                        {
                            var rowFields = new List<string>();
                            for (int j = 0; j < colCount; j++)
                            {
                                int idx = i * colCount + j;
                                if (idx < fields.Length)
                                    rowFields.Add($"[{j}]:{fields[idx].Trim()}");
                            }
                            AppendLog($"【{WorkID} 測試資料】行 {i}: {string.Join(", ", rowFields)}");
                        }
                    }
                    catch { }
                }
            }));
        }

        private void OnOrdMatF(string Omkt, string Buys, string Cmbf, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string S_Buys, string O_Prc, string A_Prc, string O_Qty, string Deal_Qty, string T_Date, string D_Time, string Order_No, string O_Src, string O_Lin, string Oseq_No)
        {
            // 加入成交回報調試日誌，以便追蹤元大傳回的原始資料與帳號比對結果
            AppendLog($"【成交回報調試】收到 OnOrdMatF：帳號='{AcNo.Trim()}', 商品='{Symb.Trim()}', 價格='{A_Prc.Trim()}', 口數='{Deal_Qty.Trim()}'");

            if (!IsMatchingAccount(AcNo))
            {
                AppendLog($"【成交回報調試】帳號不匹配。收到帳號: '{AcNo.Trim()}'，當前監控帳號: '{_currentAccount}'");
                return;
            }

            if (double.TryParse(A_Prc, out double price) && int.TryParse(Deal_Qty, out int qty))
            {
                _pnlCalculator.AddExecution(Symb, Buys, price, qty);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdatePnLUI(_pnlCalculator.TotalPnL);
                    AppendLog($"【成交回報】大/小臺平倉損益更新。商品: {Symb.Trim()}, 買賣: {Buys.Trim()}, 價格: {price}, 口數: {qty}。累計今日平倉損益: NT$ {_pnlCalculator.TotalPnL}");
                }));

                // 即時記憶體同步更新當前庫存部位 (僅限符合 TXF/MXF 格式的商品)
                string symbUpper = Symb.Trim().ToUpper();
                if (symbUpper.StartsWith("TXF") || symbUpper.StartsWith("MXF"))
                {
                    string buysUpper = Buys.Trim().ToUpper();
                    int oldLots = _currentPositionLots;
                    double oldCost = _currentPositionCost;

                    int change = (buysUpper == "B" || buysUpper == "BUY") ? qty : -qty;
                    int newLots = oldLots + change;

                    double newCost = 0;
                    if (newLots == 0)
                    {
                        newCost = 0;
                    }
                    else
                    {
                        // 判斷是否為同向開倉（包括原本無部位開倉，或多單再加碼，或空單再加碼）
                        bool isOpening = (oldLots == 0) ||
                                         (oldLots > 0 && change > 0) ||
                                         (oldLots < 0 && change < 0);

                        if (isOpening)
                        {
                            // 同向開倉：加權平均成本計算
                            double oldTotalCost = Math.Abs(oldLots) * oldCost;
                            double newTradeCost = qty * price;
                            newCost = Math.Round((oldTotalCost + newTradeCost) / Math.Abs(newLots), 2);
                        }
                        else
                        {
                            // 反向平倉：
                            int remainingOldLots = Math.Abs(oldLots) - qty;
                            if (remainingOldLots >= 0)
                            {
                                // 未超額平倉：成本維持舊成本
                                newCost = oldCost;
                            }
                            else
                            {
                                // 超額平倉且反向開倉：超出部分成本為新成交價
                                newCost = price;
                            }
                        }
                    }

                    _currentPositionLots = newLots;
                    _currentPositionCost = newCost;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (newLots == 0)
                        {
                            lblCurrentPosition.Text = "無部位";
                        }
                        else
                        {
                            string posDirection = newLots > 0 ? "多單" : "空單";
                            lblCurrentPosition.Text = $"{posDirection} {Math.Abs(newLots)} 口 (成本: {newCost})";
                        }
                        AppendLog($"【庫存更新】記憶體同步更新部位: {lblCurrentPosition.Text}");
                    }));
                }

                // 成交後延遲 600ms 自動查詢最新庫存部位，確保元大後台已同步寫入做最終一致性校驗
                DispatcherTimerExtensions.RunOnce(() => QueryCurrentPositions(), TimeSpan.FromMilliseconds(600));
            }
            else
            {
                AppendLog($"【成交回報調試】解析價格或口數失敗。原始價格: '{A_Prc}', 原始口數: '{Deal_Qty}'");
            }
        }

        /// <summary>
        /// 處理元大下單 API 委託即時結果回報事件。
        /// </summary>
        /// <param name="ID">委託 ID。</param>
        /// <param name="result">委託結果字串，格式為「流水號,錯誤代碼,錯誤訊息」或以水管符號區隔。</param>
        private void OnOrdResult(int ID, string result)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.IsNullOrEmpty(result)) return;
                
                // 元大 API OnOrdResult 可能以水管或逗號分隔，在此做通用拆分
                string[] parts = result.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    string seq = parts[0].Trim();
                    string errCode = parts[1].Trim();
                    string errMsg = parts[2].Trim();

                    if (errCode == "0000" || errCode == "00000" || string.IsNullOrEmpty(errCode) || errCode == "0")
                    {
                        AppendLog($"【委託】送出委託成功！(流水號: {seq})");
                    }
                    else
                    {
                        AppendLog($"【委託】送出委託失敗！錯誤代碼: {errCode}, 原因: {errMsg} (流水號: {seq})");
                    }
                }
                else
                {
                    AppendLog($"【委託】委託結果回報: {result}");
                }
            }));
        }

        /// <summary>
        /// 處理元大下單 API 自動委託回報事件。
        /// </summary>
        private void OnOrdRptF(string Omkt, string Mktt, string Cmbf, string Statusc, string Ts_Code, string Ts_Msg, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string O_Type, string Buys, string S_Buys, string O_Prc, string O_Qty, string Work_Qty, string Kill_Qty, string Deal_Qty, string Order_No, string T_Date, string O_Date, string O_Time, string O_Src, string O_Lin, string A_Prc, string Oseq_No, string Err_Code, string Err_Msg, string R_Time, string D_Flag)
        {
            if (!IsMatchingAccount(AcNo)) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                string bsText = S_Buys.Trim();
                if (string.IsNullOrEmpty(bsText))
                {
                    bsText = Buys.Trim() == "B" ? "買進" : "賣出";
                }

                string statusText = Ts_Msg.Trim();
                if (string.IsNullOrEmpty(statusText))
                {
                    statusText = Ts_Code == "04" ? "委託成功" : 
                                 Ts_Code == "05" ? "委託失敗" : 
                                 Ts_Code == "06" ? "全部成交" : 
                                 Ts_Code == "07" ? "全部取消" : $"狀態代碼: {Ts_Code}";
                }

                string priceText = O_Prc.Trim();
                string qtyText = O_Qty.Trim();
                string symbolText = Symb.Trim();
                string orderNo = Order_No.Trim();
                string oseqNo = Oseq_No.Trim();

                double parsedPriceVal = 0;
                int parsedPrice = 0;
                if (double.TryParse(priceText, out parsedPriceVal))
                {
                    parsedPrice = (int)Math.Round(parsedPriceVal);
                }

                string rawBuys = Buys.Trim().ToUpper();
                string rawSBuys = S_Buys.Trim().ToUpper();
                bool isBuy = rawBuys == "B" || rawSBuys.Contains("買") || rawSBuys.Contains("B");
                string itemType = isBuy ? "做多" : "做空";

                AppendLog($"【自動交易】收到委託回報：{bsText} {symbolText} {qtyText}口 @ {priceText} 書號: {orderNo}，狀態: {statusText}");

                // 嘗試從自動交易快取字典中找到 match 的項目 (優先使用流水號 OseqNo)
                AutoTradeState? matchState = null;
                var matchKeyVal = _autoTradeStates.FirstOrDefault(kv => 
                    (!string.IsNullOrEmpty(oseqNo) && (kv.Value.OseqNo == oseqNo || kv.Value.CloseOseqNo == oseqNo)) ||
                    (string.IsNullOrEmpty(oseqNo) && !string.IsNullOrEmpty(orderNo) && (kv.Value.OrderNo == orderNo || kv.Value.CloseOrderNo == orderNo))
                );
                
                // 如果流水號和書號都找不到，才使用模糊配對 (防備用)
                if (matchKeyVal.Key == default)
                {
                    matchKeyVal = _autoTradeStates.FirstOrDefault(kv => 
                        kv.Value.OrderedSymbol == symbolText && 
                        kv.Value.IsTriggered &&
                        (kv.Value.TradeStatus == "已送出" || kv.Value.TradeStatus == "委託中" || kv.Value.TradeStatus == "平倉中")
                    );
                }

                if (matchKeyVal.Key != default)
                {
                    matchState = matchKeyVal.Value;
                }

                // 放寬失敗判定，包含 Ts_Code == "09" (退單) 及狀態文字中含有退單額度保證金等訊息
                bool isFailedOrCancelled = Ts_Code == "05" || Ts_Code == "07" || Ts_Code == "09" || 
                                           !string.IsNullOrEmpty(Err_Msg.Trim()) ||
                                           statusText.Contains("失敗") || statusText.Contains("退單") || 
                                           statusText.Contains("額度") || statusText.Contains("保證金");
                if (isFailedOrCancelled)
                {
                    // 1. 處理失敗或取消
                    SimulationResult? targetItem = null;
                    if (!string.IsNullOrEmpty(oseqNo))
                    {
                        targetItem = _obsCollection.FirstOrDefault(o => o.OseqNo == oseqNo || o.CloseOseqNo == oseqNo);
                    }
                    if (targetItem == null && !string.IsNullOrEmpty(orderNo))
                    {
                        targetItem = _obsCollection.FirstOrDefault(o => o.OrderNo == orderNo || o.CloseOrderNo == orderNo);
                    }

                    if (targetItem == null)
                    {
                        targetItem = _obsCollection.FirstOrDefault(o => 
                            o.IsTriggered &&
                            (double.TryParse(o.TrigPrice, out var oTrig) && (int)Math.Round(oTrig) == parsedPrice) &&
                            o.Type == itemType &&
                            o.OrderedSymbol == symbolText); // 移除 TradeStatus 限制，改用 IsTriggered，防範競態條件
                    }

                    if (targetItem == null)
                    {
                        // Fallback: 僅比對商品、方向的第一筆下單項目（不限價格，防 API 價格回傳 0）
                        targetItem = _obsCollection.FirstOrDefault(o => 
                            o.IsTriggered &&
                            o.Type == itemType &&
                            o.OrderedSymbol == symbolText);
                    }

                    // 若精確定位到 targetItem，則以 A點價格與時間唯一鍵精確關聯快取狀態，忽略 ObsN 的可能微小差異，防止狀態覆蓋 Bug
                    if (targetItem != null)
                    {
                        var failMatchKeyVal = _autoTradeStates.FirstOrDefault(kv => 
                            kv.Key.Price == targetItem.BestAPrice && 
                            kv.Key.ATime == targetItem.BestATime);
                        if (failMatchKeyVal.Key != default)
                        {
                            matchState = failMatchKeyVal.Value;
                        }
                    }

                    string failReason = !string.IsNullOrEmpty(Err_Msg.Trim()) ? Err_Msg.Trim() : statusText;
                    if (failReason.Contains("保證金") || failReason.Contains("額度") || failReason.Contains("超過交易額度"))
                    {
                        failReason = "保證金不足";
                    }

                    if (targetItem != null)
                    {
                        if (targetItem.TradeStatus == "平倉中")
                        {
                            AppendLog($"【自動交易】平倉委託失敗！書號: {orderNo}，還原該列為「已成交」以重新監控。");
                            targetItem.TradeStatus = "已成交";
                            targetItem.CloseOrderNo = null;
                        }
                        else
                        {
                            AppendLog($"【自動交易】開倉委託失敗！書號: {orderNo}，原因: {failReason}");
                            targetItem.TradeStatus = $"失敗 ({failReason})";
                            targetItem.OrderNo = null;
                            // 失敗依然保留停損價數值，方便觀看
                        }
                    }

                    if (matchState != null)
                    {
                        if (matchState.TradeStatus == "平倉中")
                        {
                            matchState.TradeStatus = "Refocus";
                            matchState.TradeStatus = "已成交";
                            matchState.CloseOrderNo = null;
                        }
                        else
                        {
                            matchState.TradeStatus = $"失敗 ({failReason})";
                            matchState.OrderNo = null;
                            // 失敗依然保留停損價數值，方便觀看
                        }
                    }
                }
                else
                {
                    // 2. 處理成功或成交
                    if (Ts_Code == "04") // 委託成功
                    {
                        // 尋找開倉或平倉匹配項目
                        var targetItem = _obsCollection.FirstOrDefault(o => 
                            o.IsTriggered &&
                            string.IsNullOrEmpty(o.OrderNo) && 
                            // 修正：比對下單價格，應使用 TrigPrice 進場價而非 BestAPrice
                            (double.TryParse(o.TrigPrice, out var oTrig) && (int)Math.Round(oTrig) == parsedPrice) &&
                            o.Type == itemType && 
                            o.OrderedSymbol == symbolText);

                        if (targetItem == null)
                        {
                            // Fallback: 僅比對商品、方向的第一筆已觸發項目
                            targetItem = _obsCollection.FirstOrDefault(o => 
                                o.IsTriggered &&
                                string.IsNullOrEmpty(o.OrderNo) && 
                                o.Type == itemType && 
                                o.OrderedSymbol == symbolText);
                        }

                        if (targetItem != null)
                        {
                            targetItem.OrderNo = orderNo;
                            targetItem.TradeStatus = "Refocus";
                            targetItem.TradeStatus = "委託中";
                            AppendLog($"【自動交易】開倉成功！將委託書號 {orderNo} 綁定至：{targetItem.DisplayTitle} @ {parsedPrice}");
                            StartOrderTimeoutMonitor(targetItem, orderNo);

                            // 同步精確關聯快取狀態，以 A點價格與時間唯一鍵精確查找，忽略 ObsN 的可能微小差異，防止狀態覆蓋 Bug
                            var successMatchKeyVal = _autoTradeStates.FirstOrDefault(kv => 
                                kv.Key.Price == targetItem.BestAPrice && 
                                kv.Key.ATime == targetItem.BestATime);
                            if (successMatchKeyVal.Key != default)
                            {
                                matchState = successMatchKeyVal.Value;
                            }
                        }
                        else
                        {
                            // 檢查是否為平倉成功 (TradeStatus == "平倉中")
                            var closeItem = _obsCollection.FirstOrDefault(o => 
                                string.IsNullOrEmpty(o.CloseOrderNo) && 
                                o.OrderedSymbol == symbolText && 
                                o.TradeStatus == "平倉中");

                            if (closeItem != null)
                            {
                                closeItem.CloseOrderNo = orderNo;
                                AppendLog($"【自動交易】平倉委託已受理！將平倉書號 {orderNo} 綁定至：{closeItem.DisplayTitle}");
                            }
                            else
                            {
                                // 暫存起來，待 UI DiffMerge 載入時配對
                                lock (_unboundOrderReplies)
                                {
                                    _unboundOrderReplies[orderNo] = (parsedPrice, itemType, symbolText, "委託中");
                                }
                                AppendLog($"【自動交易】[快取回報] 暫時找不到相符極值列，已將書號 {orderNo} 加入待綁定快取。");
                            }
                        }

                        if (matchState != null)
                        {
                            if (matchState.TradeStatus == "已送出")
                            {
                                matchState.OrderNo = orderNo;
                                matchState.TradeStatus = "委託中";
                            }
                            else if (matchState.TradeStatus == "平倉中")
                            {
                                matchState.CloseOrderNo = orderNo;
                            }
                        }
                    }
                    else if (Ts_Code == "06" || Ts_Code == "08" || statusText.Contains("成交")) // 全部成交/成交
                    {
                        // 優先尋找開倉成交項目
                        var targetItem = _obsCollection.FirstOrDefault(o => o.OrderNo == orderNo);
                        if (targetItem != null)
                        {
                            targetItem.TradeStatus = "Refocus";
                            targetItem.TradeStatus = "已成交";
                            targetItem.StopLossDisplay = targetItem.StopLossPrice.ToString(); // 成交持倉中，顯示純數字停損價
                            AppendLog($"【自動交易】開倉單全部成交！書號: {orderNo} @ {parsedPrice} 啟動持倉停損停利監控。");
                        }
                        else
                        {
                            // 尋找平倉成交項目
                            var closeItem = _obsCollection.FirstOrDefault(o => o.CloseOrderNo == orderNo);
                            if (closeItem == null)
                            {
                                // 容錯：若平倉書號未及綁定，直接比對處於平倉中的項目
                                string oppType = itemType == "做多" ? "做空" : "做多"; // 平倉方向相反
                                closeItem = _obsCollection.FirstOrDefault(o => 
                                    o.OrderedSymbol == symbolText && 
                                    o.Type == oppType && 
                                    o.TradeStatus == "平倉中");
                            }

                            if (closeItem != null)
                            {
                                closeItem.TradeStatus = "已平倉";
                                AppendLog($"【自動交易】平倉單全部成交！書號: {orderNo}。交易完整結束。");
                            }
                            else
                            {
                                // 可能是開倉成交但 DiffMerge 還沒完成，先放入快取
                                lock (_unboundOrderReplies)
                                {
                                    _unboundOrderReplies[orderNo] = (parsedPrice, itemType, symbolText, "已成交");
                                }
                                AppendLog($"【自動交易】[快取成交] 暫時找不到書號 {orderNo}，已加入待成交快取。");
                            }
                        }

                        if (matchState != null)
                        {
                            if (matchState.TradeStatus == "委託中" || matchState.TradeStatus == "已送出")
                            {
                                matchState.TradeStatus = "Refocus";
                                matchState.TradeStatus = "Spacer";
                                matchState.TradeStatus = "已成交";
                                matchState.StopLossDisplay = matchState.StopLossPrice.ToString(); // 成交持倉中，顯示純數字停損價
                            }
                            else if (matchState.TradeStatus == "平倉中")
                            {
                                matchState.TradeStatus = "已平倉";
                            }
                        }

                        // 成交後延遲 500ms 刷新庫存部位
                        DispatcherTimerExtensions.RunOnce(() => QueryCurrentPositions(), TimeSpan.FromMilliseconds(500));
                    }
                }
            }));
        }

        private void UpdatePnLUI(double pnl)
        {
            lblTodayPnL.Text = $"NT$ {pnl:N0}";
            if (pnl > 0)
            {
                lblTodayPnL.Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75)); // 獲利為紅
            }
            else if (pnl < 0)
            {
                lblTodayPnL.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));  // 虧損為綠
            }
            else
            {
                lblTodayPnL.Foreground = Brushes.LightGray;
            }
        }

        private void UpdateMarginUI(double margin)
        {
            lblMargin.Text = $"NT$ {margin:N0}";
        }

        private void BtnQueryPnL_Click(object sender, RoutedEventArgs e)
        {
            if (!_isOrdLoggedIn)
            {
                AppendLog("【帳務】未成功登入帳務 API，嘗試手動觸發連線登入...");
                if (!string.IsNullOrEmpty(_mktUser) && !string.IsNullOrEmpty(_mktPwd))
                {
                    InitYuantaOrd();
                    // 強制重置登入中旗標，允許手動按鈕時強迫重試連線
                    _isOrdLoggingIn = false;
                    int ordRes = LoginYuantaOrd(_mktUser, _mktPwd);
                    AppendLog($"【帳務】手動 SetFutOrdConnection 呼叫完成，回傳結果代碼: {ordRes}");
                }
                else
                {
                    AppendLog("【帳務】登入失敗: 查無行情登入帳密。請先連接即時行情。");
                }
                return;
            }
            AppendLog("【帳務】手動觸發帳務與庫存查詢...");
            QueryTodayPnL();
            // 延遲 600ms 發送庫存查詢，避免平行併發衝突
            DispatcherTimerExtensions.RunOnce(() => QueryCurrentPositions(), TimeSpan.FromMilliseconds(600));
        }

        // === 自動交易事件處理與核心方法 ===

        private void ChkAutoTradeBuy_Changed(object sender, RoutedEventArgs e)
        {
            _isAutoTradeBuyEnabled = chkAutoTradeBuy.IsChecked == true;
            if (_isAutoTradeBuyEnabled)
            {
                _isBuyLocked = false; // 解除做多鎖定，允許下一筆新信號進場
                _lastBuyStopLossPrice = 0; // 重置前次停損價記錄
                AppendLog("【自動交易】已啟用自動「做多」下單監控（已重置鎖定狀態）。");
            }
            else
            {
                AppendLog("【自動交易】已關閉自動「做多」下單監控。");
            }
        }

        private void ChkAutoTradeSell_Changed(object sender, RoutedEventArgs e)
        {
            _isAutoTradeSellEnabled = chkAutoTradeSell.IsChecked == true;
            if (_isAutoTradeSellEnabled)
            {
                _isSellLocked = false; // 解除做空鎖定，允許下一筆新信號進場
                _lastSellStopLossPrice = 0; // 重置前次停損價記錄
                AppendLog("【自動交易】已啟用自動「做空」下單監控（已重置鎖定狀態）。");
            }
            else
            {
                AppendLog("【自動交易】已關閉自動「做空」下單監控。");
            }
        }

        private void RbTX_Changed(object sender, RoutedEventArgs e)
        {
            _isTxfSelected = rbTX.IsChecked == true;
        }

        private void ProcessBackgroundAutoTrade(SimulationResult item)
        {
            // 1. 檢查方向
            bool isAutoBuy = _isAutoTradeBuyEnabled;
            bool isAutoSell = _isAutoTradeSellEnabled;

            if (!isAutoBuy && !isAutoSell)
            {
                item.TradeStatus = "未啟用下單";
                return;
            }

            bool directionMatch = false;
            if (isAutoSell && item.Type == "做空") directionMatch = true;
            if (isAutoBuy && item.Type == "做多") directionMatch = true;

            if (!directionMatch)
            {
                item.TradeStatus = "方向不符 (不再下單)";
                return;
            }

            // 先解析 TrigPrice 與計算停損價，以便在鎖定檢查時比對一致性
            if (!double.TryParse(item.TrigPrice, out double trigPriceVal))
            {
                item.TradeStatus = "無效觸發價 (不再下單)";
                return;
            }
            int trigPrice = (int)Math.Round(trigPriceVal);
            int bestAPrice = item.BestAPrice;

            int stopLoss = 0;
            int takeProfit = 0;

            if (item.Type == "做空")
            {
                stopLoss = bestAPrice + 1; // A點價的上一個 (比A點高1點)
                int profitDiff = bestAPrice - trigPrice + 3; // A點價減去B觸發價，再加上3點成本
                takeProfit = trigPrice - profitDiff;
            }
            else // 做多
            {
                stopLoss = bestAPrice - 1; // A點價的下一個 (比A點低1點)
                int profitDiff = trigPrice - bestAPrice + 3; // B觸發價減去A點價，再加上3點成本
                takeProfit = trigPrice + profitDiff;
            }

            item.StopLossPrice = stopLoss;
            item.StopLossDisplay = stopLoss.ToString(); // 無論什麼狀態都顯示停損價數值
            item.TakeProfitPrice = takeProfit;

            // 1.5 檢查多空鎖定 (只做第一筆交易) - 加入停損價一致性檢查
            if (item.Type == "做多")
            {
                if (_isBuyLocked)
                {
                    // 若當前訊號之停損價與上一次下單時不同，則視為不同波段訊號，自動解除做多鎖定
                    if (stopLoss != _lastBuyStopLossPrice)
                    {
                        _isBuyLocked = false;
                        AppendLog($"【自動交易】偵測到做多停損價改變 ({_lastBuyStopLossPrice} -> {stopLoss})，解除做多鎖定並重新進場。");
                    }
                    else
                    {
                        item.TradeStatus = "已鎖定 (只做第一筆)";
                        // 同步更新快取狀態，以免 UI DiffMerge 時狀態丟失
                        var cKey = item.ConfirmedKey;
                        var cachedState = _autoTradeStates.GetOrAdd(cKey, k => new AutoTradeState());
                        cachedState.TradeStatus = "已鎖定 (只做第一筆)";
                        return;
                    }
                }
            }
            else if (item.Type == "做空")
            {
                if (_isSellLocked)
                {
                    // 若當前訊號之停損價與上一次下單時不同，則視為不同波段訊號，自動解除做空鎖定
                    if (stopLoss != _lastSellStopLossPrice)
                    {
                        _isSellLocked = false;
                        AppendLog($"【自動交易】偵測到做空停損價改變 ({_lastSellStopLossPrice} -> {stopLoss})，解除做空鎖定並重新進場。");
                    }
                    else
                    {
                        item.TradeStatus = "Refocus";
                        item.TradeStatus = "已鎖定 (只做第一筆)";
                        // 同步更新快取狀態
                        var cKey = item.ConfirmedKey;
                        var cachedState = _autoTradeStates.GetOrAdd(cKey, k => new AutoTradeState());
                        cachedState.TradeStatus = "Refocus";
                        cachedState.TradeStatus = "已鎖定 (只做第一筆)";
                        return;
                    }
                }
            }

            // 2. 檢查 API 連線與帳務登入
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount))
            {
                item.TradeStatus = "未登入 API (不再下單)";
                AppendLog($"【自動交易】自動下單失敗：元大交易 API 未登入。項目: {item.DisplayTitle}");
                return;
            }

            // 5. 下單商品與方向
            string buys = item.Type == "做多" ? "B" : "S";
            string priceStr = trigPrice.ToString();
            string qtyStr = "1";

            string baseSym = _isTxfSelected ? "TXF" : "MXF";
            string monthCode = _engine.GetMonthCode();
            string symbol = baseSym + monthCode;
            item.OrderedSymbol = symbol;
            item.IsTriggered = true;

            // 啟用互鎖與方向解鎖，並記錄當前停損價
            if (item.Type == "做多")
            {
                _isBuyLocked = true;
                _lastBuyStopLossPrice = stopLoss;
                _isSellLocked = false; // 解鎖做空
                AppendLog($"【自動交易】已觸發自動做多下單，鎖定做多監控 (停損價: {stopLoss})，並自動解鎖做空。");
            }
            else if (item.Type == "做空")
            {
                _isSellLocked = true;
                _lastSellStopLossPrice = stopLoss;
                _isBuyLocked = false; // 解鎖做多
                AppendLog($"【自動交易】已觸發自動做空下單，鎖定做空監控 (停損價: {stopLoss})，並自動解鎖做多。");
            }

            // 同步寫入快取
            var key = item.ConfirmedKey;
            var state = _autoTradeStates.GetOrAdd(key, k => new AutoTradeState());
            state.StopLossPrice = stopLoss;
            state.StopLossDisplay = stopLoss.ToString(); // 無論什麼狀態都顯示停損價數值
            state.TakeProfitPrice = takeProfit;
            state.OrderedSymbol = symbol;
            state.IsTriggered = true;
            state.TradeStatus = "Refocus";
            state.TradeStatus = "Spacer";
            state.TradeStatus = "已送出";

            // 同步寫入 UI 物件，消除非同步 DiffMerge 延遲，防止秒速退單回報找不到 "已送出" 項目
            item.TradeStatus = "已送出";
            item.StopLossPrice = stopLoss;
            item.StopLossDisplay = stopLoss.ToString();
            item.TakeProfitPrice = takeProfit;

            AppendLog($"【自動交易】記憶體瞬間產生極值！送出限價單！商品: {symbol} 方向: {(buys == "B" ? "買進" : "賣出")} 1口 @ {priceStr}，停損: {stopLoss}，停利: {takeProfit}");

            // 呼叫 API 送出新單委託 (FCode="01", CommodityType="0", ROD="R", Limit="L")
            string ret = _yuantaOrd.SendOrderF("01", "0", _currentBranch, _currentAccount, "", "", buys, symbol, priceStr, qtyStr, "0", "L", "R", "", "");
            string[] retParts = ret.Split('|');
            if (retParts.Length > 0)
            {
                string oseqNo = retParts[0].Trim();
                item.OseqNo = oseqNo;
                state.OseqNo = oseqNo;
            }
            AppendLog($"【自動交易】元大 API 下單回傳結果: {ret}");

            // 先設為已送出，等待 OnOrdRptF 收到委託成功 (04) 以綁定書號
            item.TradeStatus = "已送出";
        }

        private async void StartOrderTimeoutMonitor(SimulationResult item, string orderNo)
        {
            await Task.Delay(3000);
            Dispatcher.Invoke(() =>
            {
                var key = item.ConfirmedKey;
                if (_autoTradeStates.TryGetValue(key, out var state))
                {
                    if (state.OrderNo == orderNo && state.TradeStatus == "委託中")
                    {
                        AppendLog($"【自動交易】委託書號 {orderNo} 超過 3 秒未成交，執行自動撤單！");
                        if (_yuantaOrd != null && !string.IsNullOrEmpty(_currentAccount))
                        {
                            string buys = item.Type == "做多" ? "B" : "S";
                            string ret = _yuantaOrd.SendOrderF("03", "0", _currentBranch, _currentAccount, "", orderNo, buys, item.OrderedSymbol, "0", "", "", "L", "R", "", "");
                            AppendLog($"【自動交易】元大 API 撤單結果: {ret}");
                        }
                        state.TradeStatus = "逾時取消 (不再下單)";
                        item.TradeStatus = "逾時取消 (不再下單)";
                    }
                }
            });
        }
    }

    /// <summary>
    /// 自動交易背景狀態保存結構
    /// </summary>
    public class AutoTradeState
    {
        public string TradeStatus { get; set; } = "未啟用下單";
        public int TakeProfitPrice { get; set; } = 0;
        public string? OrderedSymbol { get; set; } = null;
        public bool IsTriggered { get; set; } = false;
        public string? OseqNo { get; set; } = null;
        public string? CloseOseqNo { get; set; } = null;
        public string? OrderNo { get; set; } = null;
        public string? CloseOrderNo { get; set; } = null;
        public int StopLossPrice { get; set; } = 0;
        public string? StopLossDisplay { get; set; } = null;
    }



    /// <summary>
    /// DispatcherTimer 輔助擴充方法，簡化 Timer 撰寫。
    /// </summary>
    public static class DispatcherTimerExtensions
    {
        public static void RunOnce(Action action, TimeSpan delay)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = delay
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }
    }
}