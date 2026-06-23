import sys

with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'rb') as f:
    content_bytes = f.read()

content_str = content_bytes.decode('utf-8', errors='ignore')

# 找到目標位置
search_str = 'if (baseSymbol == "TXF")\r\n                {\r\n                    _renderTxfPrice = price;'
idx = content_str.find(search_str)

if idx != -1:
    start_idx = content_str.rfind('//', 0, idx)
    end_idx = content_str.find('}', idx)
    end_idx = content_str.find('}', end_idx + 1)
    end_idx = content_str.find('}', end_idx + 1)
    
    target = content_str[start_idx:end_idx+1]
    
    replacement = '''// 實時 Render Loop 快取 (單向寫入)
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
    
    new_content_str = content_str.replace(target, replacement)
    
    with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'wb') as f:
        f.write(new_content_str.encode('utf-8'))
    print('SUCCESS')
else:
    print('FAILED TO FIND TARGET')
