import re

with open('MainWindow.xaml.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Replace OnCompositionTargetRendering manually
pattern = r'(private void OnCompositionTargetRendering\(object\? sender, EventArgs e\)\s*\{\s*if \(!_isInitialized \|\| _isReplaying\) return;\s*)'

replacement = r'\1\n            var analysisResult = System.Threading.Interlocked.Exchange(ref _latestAnalysisResult, null);\n            if (analysisResult != null)\n            {\n                ApplyRealtimeAnalysisUI(analysisResult);\n            }\n\n'

content = re.sub(pattern, replacement, content)

with open('MainWindow.xaml.cs', 'w', encoding='utf-8') as f:
    f.write(content)
