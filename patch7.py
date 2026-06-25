import sys
import re

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix OnOrdLogonS signature
content = content.replace('private void OnOrdLogonS(string status, string msg)', 'private void OnOrdLogonS(int status, string accList, string casq, string cast)')
content = content.replace('private void OnOrdLogonS(int status, string msg)', 'private void OnOrdLogonS(int status, string accList, string casq, string cast)')
content = content.replace('if (status == "0")', 'if (status == 2)')
content = content.replace('if (status == 0)', 'if (status == 2)')
content = content.replace('AppendLog($"【下單】登入狀態: {status} - {msg}");', 'AppendLog($"【下單】登入狀態: {status}");')

# Fix OnOrdMatF signature
content = content.replace('private void OnOrdMatF(string tType, string tAccount, string tSymbol, string tTime, string tPrice, string tQty)', 'private void OnOrdMatF(string Omkt, string Buys, string Cmbf, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string S_Buys, string O_Prc, string A_Prc, string O_Qty, string Deal_Qty, string T_Date, string D_Time, string Order_No, string O_Src, string O_Lin, string Oseq_No)')
content = content.replace('private void OnOrdMatF(string symbol, string mattime, string matpri, string tmatqty)', 'private void OnOrdMatF(string Omkt, string Buys, string Cmbf, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string S_Buys, string O_Prc, string A_Prc, string O_Qty, string Deal_Qty, string T_Date, string D_Time, string Order_No, string O_Src, string O_Lin, string Oseq_No)')

# Fix SetFutOrdConnection
content = content.replace('_yuantaOrd.SetFutOrdConnection("api.yuanta.com.tw", "443", user, pwd);', '_yuantaOrd.SetFutOrdConnection(user, pwd, "api.yuanta.com.tw", 443);')

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Fixed!')