import sys
import re

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Remove lblTotalStats code
content = re.sub(r'if \(lblTotalStats != null\)\s*\{\s*lblTotalStats\.Text[^}]+?\}\s*else\s*\{\s*lblTotalStats[^}]+?\}\s*\}', '', content, flags=re.DOTALL)
content = re.sub(r'lblTotalStats\.Text = "[^"]+";', '', content)
content = re.sub(r'lblTotalStats\.Foreground = [^;]+;', '', content)

# Fix _pnlCalculator
content = content.replace('_pnlCalculator.Reset();', '')

# Fix SetFutOrdConnection
content = content.replace('_yuantaOrd.SetFutOrdConnection("api.yuanta.com.tw", 443, user, pwd);', '_yuantaOrd.SetFutOrdConnection("api.yuanta.com.tw", "443", user, pwd);')

# Fix OnOrdLogonS signature
content = content.replace('private void OnOrdLogonS(int status, string msg)', 'private void OnOrdLogonS(string status, string msg)')
content = content.replace('if (status == 0)', 'if (status == "0")')

# Fix OnOrdMatF signature
content = content.replace('private void OnOrdMatF(string symbol, string mattime, string matpri, string tmatqty)', 'private void OnOrdMatF(string tType, string tAccount, string tSymbol, string tTime, string tPrice, string tQty)')

# Fix UnbrokenKMonitor lblSummaryShort / Long
content = re.sub(r'monitor\.lblSummaryShort.*?=.*?;\n?', '', content)
content = re.sub(r'monitor\.lblSummaryLong.*?=.*?;\n?', '', content)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Fixed!')