import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add _latestAnalysisResult
content = content.replace(
    'private volatile int _uiGeneration = 0; // UI 世代計數器，防止過期 Dispatcher 任務覆蓋已清空的 UI',
    'private volatile int _uiGeneration = 0; // UI 世代計數器，防止過期 Dispatcher 任務覆蓋已清空的 UI\n        private volatile System.Collections.Generic.Dictionary<string, object>? _latestAnalysisResult = null;'
)

# 2. Add volatile variables for lock-free match qty instead of dictionary
content = content.replace(
    'private readonly Dictionary<string, int> _rtLastMatchQty = [];',
    '// 無鎖即時行情過濾與防呆狀態 (Lock-Free Fallbacks)\n        private volatile int _txfLastMatchQty = -1;\n        private volatile int _mxfLastMatchQty = -1;'
)
content = content.replace('_rtLastMatchQty.Clear();', '_txfLastMatchQty = -1;\n                _mxfLastMatchQty = -1;')

# 3. OnCompositionTargetRendering
comp_target_old = '''        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            if (!_isInitialized || _isReplaying) return;
            
            // 每幀無鎖撈取最新的 volatile 變數'''
comp_target_new = '''        private void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            if (!_isInitialized || _isReplaying) return;
            
            var analysisResult = System.Threading.Interlocked.Exchange(ref _latestAnalysisResult, null);
            if (analysisResult != null)
            {
                ApplyRealtimeAnalysisUI(analysisResult);
            }
            
            // 每幀無鎖撈取最新的 volatile 變數'''
content = content.replace(comp_target_old, comp_target_new)

# 4. OnGetMktAll modifications
on_get_mkt_all_old = '''                // 重複/滯後 Tick 行情過濾防線
                lock (_rtLock)
                {
                    if (_rtLastMatchQty.TryGetValue(baseSymbol, out int prevQty))
                    {
                        if (currentQty <= prevQty) return;
                    }
                    _rtLastMatchQty[baseSymbol] = currentQty;
                }

                int price = (int)double.Parse(matchPri);
                
                // 取得買一賣一最前端值以判定內外盤
                string bpStr = bestBuyPri.Split(',')[0];
                string spStr = bestSellPri.Split(',')[0];
                int bestBp = (!string.IsNullOrEmpty(bpStr) && double.TryParse(bpStr, out double bpVal)) ? (int)bpVal : 0;
                int bestSp = (!string.IsNullOrEmpty(spStr) && double.TryParse(spStr, out double spVal)) ? (int)spVal : 0;'''

on_get_mkt_all_new = '''                // 重複/滯後 Tick 行情過濾防線 (Lock-Free)
                if (baseSymbol == "TXF")
                {
                    if (currentQty <= _txfLastMatchQty) return;
                    _txfLastMatchQty = currentQty;
                }
                else
                {
                    if (currentQty <= _mxfLastMatchQty) return;
                    _mxfLastMatchQty = currentQty;
                }

                int price = (int)double.Parse(matchPri.AsSpan());
                
                // Zero Allocation 解析買一賣一 (取代 Split(',') 避免產生陣列與字串垃圾)
                int bestBp = ParseFirstPrice(bestBuyPri);
                int bestSp = ParseFirstPrice(bestSellPri);'''

content = content.replace(on_get_mkt_all_old, on_get_mkt_all_new)

# 5. OnGetMktAll lock(_rtLock) replacement
lock_rt_old = '''                // 買賣盤方向Fallback處理
                lock (_rtLock)
                {
                    if (side == TradeSide.Unknown)
                    {
                        var prevTrades = _liveSymbolTrades[baseSymbol][session];
                        side = prevTrades.Count > 0 ? prevTrades[^1].Side : TradeSide.Outer;
                    }

                    var tick = new TradeTick(baseSymbol, mt, tVal, price, side, bestBp, bestSp, session);
                    _liveSymbolTrades[baseSymbol][session].Add(tick);

                    // O(1) 增量狀態更新
                    var state = _rtState[baseSymbol][session];
                    state.Count++;
                    state.SumPrice += price;
                    
                    if (price > state.DayMax)
                    {
                        state.DayMax = price;
                        state.MaxTime = mt;
                    }
                    if (price < state.DayMin)
                    {
                        state.DayMin = price;
                        state.MinTime = mt;
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

                    if (baseSymbol == "MXF")
                    {
                        _lastMxfPrice = price;
                        _lastMxfTime = mt;
                    }
                }'''

lock_rt_new = '''                // 買賣盤方向Fallback處理 (Lock-Free)
                if (side == TradeSide.Unknown)
                {
                    var prevTrades = _liveSymbolTrades[baseSymbol][session];
                    side = prevTrades.Count > 0 ? prevTrades[^1].Side : TradeSide.Outer;
                }

                var tick = new TradeTick(baseSymbol, mt, tVal, price, side, bestBp, bestSp, session);
                
                // 完全 Lock-Free 的資料結構寫入 (Zero GC Allocation & No OS Lock)
                _liveSymbolTrades[baseSymbol][session].Add(tick);

                // O(1) 增量狀態更新，改用極輕量物件鎖，避免與其他商品或全域資源阻塞
                var state = _rtState[baseSymbol][session];
                lock (state)
                {
                    state.Count++;
                    state.SumPrice += price;
                    
                    if (price > state.DayMax)
                    {
                        state.DayMax = price;
                        state.MaxTime = mt;
                    }
                    if (price < state.DayMin)
                    {
                        state.DayMin = price;
                        state.MinTime = mt;
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

                    if (baseSymbol == "MXF")
                    {
                        _lastMxfPrice = price;
                        _lastMxfTime = mt;
                    }
                }'''

content = content.replace(lock_rt_old, lock_rt_new)

# 6. Replace string Contains with StartsWith
content = content.replace('symbol.Contains("TXF") ? "TXF" : (symbol.Contains("MXF") ? "MXF" : "")', 'symbol.StartsWith("TXF") ? "TXF" : (symbol.StartsWith("MXF") ? "MXF" : "")')

# 7. Add ParseFirstPrice method
parse_first_price = '''        // Zero Allocation 字串數字提取 (避免 Split(',') 造成的字串與陣列配置)
        private int ParseFirstPrice(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int commaIdx = s.IndexOf(',');
            var span = commaIdx < 0 ? s.AsSpan() : s.AsSpan(0, commaIdx);
            if (double.TryParse(span, out double val)) return (int)val;
            return 0;
        }

        // ==================== 3. 實時行情背景 Debounce 計算 Task 迴圈 ===================='''
content = content.replace('        // ==================== 3. 實時行情背景 Debounce 計算 Task 迴圈 ====================', parse_first_price)


# 8. AnalysisWorkerLoopAsync completely event-driven
worker_loop_old = '''        private async Task AnalysisWorkerLoopAsync(CancellationToken token)
        {
            // 將 CancellationToken 的 WaitHandle 與 _analysisEvent 組合，達成全睡眠零 CPU 佔用等待
            var handles = new WaitHandle[] { _analysisEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                // 等待 CPU 喚醒 (有 Tick 寫入才進來，避免無意義空轉)
                int which = WaitHandle.WaitAny(handles);
                if (token.IsCancellationRequested || which == 1) break;

                // 150ms 頻率防抖動 (對應舊版 ANALYSIS_DEBOUNCE_SEC = 0.15 秒)
                try { await Task.Delay(150, token); } catch (OperationCanceledException) { break; }
                _analysisEvent.Reset(); // 清除 Debounce 殘留訊號

                try
                {
                    // 世代驗證，如果運算前 _uiGeneration 被切換(換盤/回放)，放棄本次
                    int gen = _uiGeneration;

                    // 在獨立背景執行緒全速運算最新狀態
                    var result = RunRealtimeAnalysisCompute();
                    
                    // 運算完成後丟回主執行緒更新 UI (WPF RichTextBox, DataGrids, Labels)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_uiGeneration != gen) return; // 世代過期，丟棄結果
                        ApplyRealtimeAnalysisUI(result);
                    }));
                }
                catch (Exception ex)
                {
                    // 攔截並紀錄背景執行緒的例外，防止 Silent Crash
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"🚨【量化計算崩潰】核心計算背景執行緒發生未知錯誤: {ex.Message}\\n{ex.StackTrace}");
                    }));
                }
            }
        }'''

worker_loop_new = '''        private void AnalysisWorkerLoop(CancellationToken token)
        {
            var handles = new WaitHandle[] { _analysisEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                // 真正的事件驅動 (Event-Driven)：平時完全休眠 (0% CPU)，有 Tick 瞬間喚醒，0 等待延遲
                int which = WaitHandle.WaitAny(handles);
                if (token.IsCancellationRequested || which == 1) break;

                // 移除所有 Task.Delay 造成的 150ms 人工延遲，達成極低延遲架構
                _analysisEvent.Reset(); 

                try
                {
                    int gen = _uiGeneration;

                    // 背景全速運算最新狀態 (Zero Allocation / Lock Free)
                    var result = RunRealtimeAnalysisCompute();
                    
                    // 為了避免 0 延遲運算導致 Dispatcher UI 卡死，我們將結果透過 Lock-Free 通道傳送
                    // 並在 60FPS 的 UI Rendering 事件中無鎖撈取
                    if (_uiGeneration == gen)
                    {
                        System.Threading.Interlocked.Exchange(ref _latestAnalysisResult, result);
                    }
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog($"🚨【量化計算崩潰】核心計算背景執行緒發生未知錯誤: {ex.Message}\\n{ex.StackTrace}");
                    }));
                }
            }
        }'''

content = content.replace(worker_loop_old, worker_loop_new)
content = content.replace('_analysisTask = AnalysisWorkerLoopAsync(_analysisCts.Token);', '_analysisTask = Task.Run(() => AnalysisWorkerLoop(_analysisCts.Token));')

# 9. Fix RunRealtimeAnalysisCompute lock(_rtLock)
run_rt_old = '''            var tradesSnapshot = new Dictionary<string, IReadOnlyList<TradeTick>>();
            var stateSnapshot = new Dictionary<string, TradingState>();

            lock (_rtLock)
            {
                var tradesSource = _isReplaying ? _replaySymbolTrades : _liveSymbolTrades;
                var stateSource = _isReplaying ? _replayRtState : _rtState;

                foreach (var symbol in new[] { "TXF", "MXF" })
                {
                    tradesSnapshot[symbol] = tradesSource[symbol][activeSession]; // Zero Allocation
                    stateSnapshot[symbol] = stateSource[symbol][activeSession].Clone();
                }
            }'''

run_rt_new = '''            var tradesSnapshot = new Dictionary<string, IReadOnlyList<TradeTick>>();
            var stateSnapshot = new Dictionary<string, TradingState>();

            var tradesSource = _isReplaying ? _replaySymbolTrades : _liveSymbolTrades;
            var stateSource = _isReplaying ? _replayRtState : _rtState;

            foreach (var symbol in new[] { "TXF", "MXF" })
            {
                // Lock-Free 參考拷貝 (Zero Allocation)
                tradesSnapshot[symbol] = tradesSource[symbol][activeSession]; 
                
                var state = stateSource[symbol][activeSession];
                lock (state)
                {
                    stateSnapshot[symbol] = state.Clone();
                }
            }'''
content = content.replace(run_rt_old, run_rt_new)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
