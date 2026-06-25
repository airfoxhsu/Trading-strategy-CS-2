import sys

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
for line in lines:
    if 'lblTotalStats' in line or 'lblSummaryShort' in line or 'lblSummaryLong' in line:
        new_lines.append('// ' + line)
    else:
        new_lines.append(line)

with open(file_path, 'w', encoding='utf-8') as f:
    f.writelines(new_lines)
print('Fixed lblTotalStats!')