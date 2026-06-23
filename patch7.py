with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Replace line 2336 area
old_block = '''                            var mergedList = new List<TradeTick>(historyList.Count + liveList.Count);
                            mergedList.AddRange(historyList);
                            
                            if (liveList.Count > 0)
                            {
                                if (historyList.Count > 0)
                                {
                                    var lastHistTime = historyList[^1].TimeVal;
                                    var filteredLive = liveList.Where(x => x.TimeVal > lastHistTime).ToList();
                                    mergedList.AddRange(filteredLive);
                                }
                                else
                                {
                                    mergedList.AddRange(liveList);
                                }
                            }
                            
                            _liveSymbolTrades[sym][sess] = mergedList;'''

new_block = '''                            var mergedList = new ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>(historyList.Count + liveList.Count);
                            foreach (var item in historyList) mergedList.Add(item);
                            
                            if (liveList.Count > 0)
                            {
                                if (historyList.Count > 0)
                                {
                                    var lastHistTime = historyList[^1].TimeVal;
                                    var filteredLive = liveList.Where(x => x.TimeVal > lastHistTime).ToList();
                                    foreach (var item in filteredLive) mergedList.Add(item);
                                }
                                else
                                {
                                    foreach (var item in liveList) mergedList.Add(item);
                                }
                            }
                            
                            _liveSymbolTrades[sym][sess] = mergedList;'''

content = content.replace(old_block, new_block)

# Fix line 2387 area
old_block2 = '''                    var oldTrades = _liveSymbolTrades[sym][session];
                    var newTrades = new List<TradeTick>(oldTrades.Count);
                    newTrades.AddRange(oldTrades);

                    // ?????? TimeSpan ???????Tick
                    string lastTime = newTrades[^1].Time;'''

new_block2 = '''                    var oldTrades = _liveSymbolTrades[sym][session];
                    var newTrades = new ExtremeSignalAppCS.Models.ConcurrentAppendOnlyList<TradeTick>(oldTrades.Count);
                    foreach (var item in oldTrades) newTrades.Add(item);

                    // ?????? TimeSpan ???????Tick
                    string lastTime = newTrades[^1].Time;'''

content = content.replace(old_block2, new_block2)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Patch applied successfully")
