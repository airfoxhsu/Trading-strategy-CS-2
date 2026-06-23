with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('List<TradeTick>? txfT = null;', 'ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>? txfT = null;')
content = content.replace('List<TradeTick>? mxfT = null;', 'ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>? mxfT = null;')

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Patch applied successfully")
