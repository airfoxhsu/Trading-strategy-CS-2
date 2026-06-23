import sys

with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Fix 1: OnCompositionTargetRendering
content = content.replace('if (!_isInitialized || _isReplaying) return;', 'if (!_isInitialized) return;')

# Fix 2: ProcessRawTick
t2 = '''                // 實時 Render Loop 快取 (單向寫入)
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
                }'''

r2 = '''                // 實時 Render Loop 快取 (單向寫入)
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
                }'''

if t2 in content:
    content = content.replace(t2, r2)
else:
    print('Failed to replace t2')

# Fix 3: ReplayLoopAsync
t3 = '''                    if (baseSym == "MXF")
                    {
                        _lastMxfPrice = price;
                        _lastMxfTime = mt;
                    }'''

r3 = '''                    if (baseSym == "MXF")
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
                    }'''

if t3 in content:
    content = content.replace(t3, r3)
else:
    print('Failed to replace t3')

with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print('SUCCESS')
