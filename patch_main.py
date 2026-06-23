import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Wire up TradingEngine events in MainWindow constructor
init_code = r'''        public MainWindow()
        {
            InitializeComponent();
'''
new_init = r'''        public MainWindow()
        {
            InitializeComponent();
            
            _engine.OnTriggerCompleted += (res) => {
                Dispatcher.BeginInvoke(new System.Action(() => {
                    _obsCollection.Add(res);
                    if (chkTelegram.IsChecked == true && !_isReplaying && !_isPreloading) {
                        PushTelegramMessage($"🚨 極值達標 [{res.Type}] {res.StopLossDisplay}");
                    }
                }));
            };
            
            _engine.OnTriggerBroken += (res) => {
                Dispatcher.BeginInvoke(new System.Action(() => {
                    // Property is updated via INotifyPropertyChanged
                    if (chkTelegram.IsChecked == true && !_isReplaying && !_isPreloading) {
                        PushTelegramMessage($"💔 停損破位 [{res.Type}] {res.StopLossDisplay}");
                    }
                }));
            };
            
            _engine.OnKlineClosed += (bar) => {
                Dispatcher.BeginInvoke(new System.Action(() => {
                    _klineCollection.Add(bar);
                    if (dgKline.SelectedIndex == -1 && _klineCollection.Count > 0)
                    {
                        dgKline.ScrollIntoView(_klineCollection[_klineCollection.Count - 1]);
                    }
                }));
            };
'''
content = content.replace(init_code, new_init)

# 2. Inject ProcessTickStream into ProcessRawTick
# Find the end of ProcessRawTick
tick_injection = r'''                if (!_isReplaying && !_isPreloading && _isRealtimeUIEnabled)
                {
                    _analysisEvent.Set();
                }'''
                
new_tick_injection = r'''                if (!_isReplaying && !_isPreloading && _isRealtimeUIEnabled)
                {
                    // --- 呼叫串流引擎 ---
                    var tradesList = _liveSymbolTrades[baseSymbol][session];
                    _engine.ProcessTickStream(baseSymbol, session, tick, tradesList, tradesList.Count - 1, _currentObsN);
                }'''
content = content.replace(tick_injection, new_tick_injection)

# 3. Remove AnalysisWorkerLoopAsync and ApplyRealtimeAnalysisUI blocks entirely
# We will use regex to find and remove them.
pattern = re.compile(r'// ==================== 3\. 實時行情背景 Debounce 計算 Task 迴圈 ====================.*?(?=(// 4\. 刷新 K線與圖表 \(O\(1\) / O\(N\) 零 CPU 聚合開銷\)|public void PushTelegramMessage))', re.DOTALL)
content = re.sub(pattern, '', content)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print("Patch successful!")
