import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

target = r'''                    if (_klineCollection.Count > 0)
                    {
                        var lastBar = _klineCollection[^1];'''

replacement = r'''                    var state = _engine.GetState("TXF", _currentRealtimePort == 442 ? "夜盤" : "日盤");
                    if (state != null)
                    {
                        runMaxInfo.Text = $"最高價: {state.DayMax}";
                        runMinInfo.Text = $"最低價: {state.DayMin}";
                        runAmpInfo.Text = $"振幅: {state.DayMax - state.DayMin}";
                    }

                    if (_klineCollection.Count > 0)
                    {
                        var lastBar = _klineCollection[^1];'''

content = content.replace(target, replacement)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print("Added UI top labels update")
