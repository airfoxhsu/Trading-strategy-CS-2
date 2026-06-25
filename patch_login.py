import sys
import re

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Update OnOrdLogonS to parse _currentBranch and _currentAccount
onLogon = '''
        private void OnOrdLogonS(int status, string accList, string casq, string cast)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                AppendLog($"【下單】登入狀態: {status}");
                if (status == 2) {
                    if (!string.IsNullOrEmpty(accList)) {
                        var acnos = accList.Split(';');
                        foreach (var acno in acnos) {
                            if (acno.StartsWith("2,")) {
                                var parts = acno.Split(',');
                                if (parts.Length >= 3) {
                                    _currentBranch = parts[1];
                                    _currentAccount = parts[2];
                                    break;
                                }
                            }
                        }
                    }
                    QueryTodayPnL();
                    QueryCurrentPositions();
                    QueryPendingOrders();
                }
            }));
        }
'''
content = re.sub(r'private void OnOrdLogonS\(int status, string accList, string casq, string cast\)\s*\{.*?\}\s*\}\)\);\s*\}', onLogon.strip(), content, flags=re.DOTALL)

# 2. Add InitYuantaOrd and LoginYuantaOrd to StartRealtime after SetMktLogon
loginCall = '''
                int res = _yuantaQuote.SetMktLogon(user, pwd, "203.66.93.84", port.ToString(), 1, 0);
                AppendLog($"【行情】SetMktLogon 呼叫完成，回傳代碼: {res} (0 代表送出登入要求)");
                
                InitYuantaOrd();
                int ordRes = LoginYuantaOrd(user, pwd);
                AppendLog($"【下單】SetFutOrdConnection 呼叫完成，回傳代碼: {ordRes}");
'''
content = content.replace('int res = _yuantaQuote.SetMktLogon(user, pwd, "203.66.93.84", port.ToString(), 1, 0);\n                AppendLog($"【行情】SetMktLogon 呼叫完成，回傳代碼: {res} (0 代表送出登入要求)");', loginCall.strip())

# 3. Add Logout to StopRealtime
logoutCall = '''
                _yuantaQuote.DoLogout();
                if (_yuantaOrd != null) {
                    _yuantaOrd.DoLogout();
                    _yuantaOrd.OnLogonS -= OnOrdLogonS;
                    _yuantaOrd.OnUserDefinsFuncResult -= OnOrdUserDefinsFuncResult;
                    _yuantaOrd.OnReportQuery -= OnOrdReportQuery;
                    _yuantaOrd.OnOrdMatF -= OnOrdMatF;
                    _yuantaOrd = null;
                }
'''
content = content.replace('_yuantaQuote.DoLogout();', logoutCall.strip())

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Fixed Login calls!')