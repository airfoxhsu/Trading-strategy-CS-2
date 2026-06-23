import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove AnalysisWorkerLoopAsync call
content = re.sub(r'_ = Task\.Run\(\(\) => AnalysisWorkerLoopAsync\(_marketDataCts\.Token\)\);', '', content)

# 2. Add _latestTriggerLog back above PushTelegramMessage
content = content.replace('public void PushTelegramMessage', 'private string _latestTriggerLog = "";\n\n        public void PushTelegramMessage')

# 3. Add FormatExtremeTime back
format_time_code = r'''        private static string FormatExtremeTime(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 6) return t;
            return $"{t[..2]}:{t[2..4]}:{t[4..6]}";
        }
        
'''
# Insert it before private (bool Success... RunAnalysisSync
content = content.replace('private (bool Success, object Result, string? Status) RunAnalysisSync', format_time_code + 'private (bool Success, object Result, string? Status) RunAnalysisSync')

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print("Fix successful!")
