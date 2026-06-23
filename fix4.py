import sys

with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'rb') as f:
    content_bytes = f.read()

content_str = content_bytes.decode('utf-8', errors='ignore')

# Fix 1
content_str = content_str.replace('if (!_isInitialized || _isReplaying) return;', 'if (!_isInitialized) return;')

# Fix 3 (Replay loop)
t3 = '''                    if (baseSym == "MXF")\r\n                    {\r\n                        _lastMxfPrice = price;\r\n                        _lastMxfTime = mt;\r\n                    }'''

r3 = '''                    if (baseSym == "MXF")\r\n                    {\r\n                        _renderMxfPrice = price;\r\n                        Volatile.Write(ref _renderMxfTime, mt);\r\n                        _lastMxfPrice = price;\r\n                        _lastMxfTime = mt;\r\n                    }\r\n                    else if (baseSym == "TXF")\r\n                    {\r\n                        _renderTxfPrice = price;\r\n                        Volatile.Write(ref _renderTxfTime, mt);\r\n                    }'''

if t3 in content_str:
    content_str = content_str.replace(t3, r3)
else:
    print('Failed t3')

with open('h:/Coding/CSharp/Trading-strategy-CS-1/MainWindow.xaml.cs', 'wb') as f:
    f.write(content_str.encode('utf-8'))

print('SUCCESS')
