with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('new Dictionary<string, Dictionary<string, List<TradeTick>>>', 'new Dictionary<string, Dictionary<string, ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>>>')

content = content.replace('out List<TradeTick>? txfT', 'out ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>? txfT')
content = content.replace('out List<TradeTick>? mxfT', 'out ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>? mxfT')
content = content.replace('var mergedList = new List<TradeTick>(historyList.Count + liveList.Count);\\n                            mergedList.AddRange(historyList);\\n                            mergedList.AddRange(liveList);\\n                            _liveSymbolTrades[sym][sess] = mergedList;', 'var mergedList = new ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>(historyList.Count + liveList.Count);\\n                            foreach(var item in historyList) mergedList.Add(item);\\n                            foreach(var item in liveList) mergedList.Add(item);\\n                            _liveSymbolTrades[sym][sess] = mergedList;')

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Patch applied successfully")
