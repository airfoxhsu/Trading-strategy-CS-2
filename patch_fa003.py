import sys
import re

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

fa003_replace = '''
                if (WorkID == "FA003" && RowCount > 0)
                {
                    string[] fields = Results.Split('|');
                    int colCount = fields.Length / RowCount;
                    int equityIdx = -1, grantalIdx = -1, floatIdx = -1;
                    for (int j = 0; j < colCount; j++) {
                        string col = fields[j].Trim().ToUpper();
                        if (col == "EQUITY") equityIdx = j;
                        if (col == "GRANTAL") grantalIdx = j;
                        if (col == "FLOAT_MARGIN") floatIdx = j;
                    }
                    if (RowCount > 1) {
                        if (equityIdx != -1 && equityIdx < fields.Length) {
                            string val = fields[colCount + equityIdx].Trim();
                            if (double.TryParse(val, out double eq)) UpdateMarginUI(eq);
                        }
                        if (grantalIdx != -1 && grantalIdx < fields.Length) {
                            string val = fields[colCount + grantalIdx].Trim();
                            if (double.TryParse(val, out double pnl)) UpdatePnLUI(pnl);
                        }
                    }
                }
'''

content = re.sub(r'if \(WorkID == "FA003" && RowCount > 0\)\s*\{.*?else if \(WorkID == "FA002"\)', fa003_replace.strip() + '\n                else if (WorkID == "FA002")', content, flags=re.DOTALL)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Fixed FA003 parsing!')