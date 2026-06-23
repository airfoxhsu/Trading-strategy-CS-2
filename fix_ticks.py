import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('        private Task? _replayTask;\n        private bool _isReplaying;', '        private Task? _replayTask;\n        private List<TradeTick> _allParsedTicks = [];\n        private bool _isReplaying;')

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print("Restored _allParsedTicks")
