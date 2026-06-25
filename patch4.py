import sys
import re

file_path = r'h:\Coding\CSharp\Trading-strategy-CS-2\MainWindow.xaml.cs'

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

fields = '''
        // 原生元大 COM 行情連線相關欄位
        private AxYuantaQuoteLib.AxYuantaQuote? _axHost;
        private YuantaQuoteWrapper? _yuantaQuote;
        private string[] _symbolsToRegister = [];

        // 委託與帳務 COM (YuantaOrdLib)
        private YuantaOrdLib.YuantaOrdClass? _yuantaOrd;
        private string _currentBranch = string.Empty;
        private string _currentAccount = string.Empty;
        public System.Collections.ObjectModel.ObservableCollection<PendingOrder> PendingOrders { get; } = new();
'''
content = re.sub(r'// 原生元大 COM 行情連線相關欄位.*?private string\[\] _symbolsToRegister = \[\];', fields, content, flags=re.DOTALL)

newMethods = '''
        // ==================== 10. YuantaOrd API ====================

        private void BtnQueryPnL_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AppendLog("【帳務】手動觸發帳務與庫存及委託查詢...");
            QueryTodayPnL();
            QueryCurrentPositions();
            QueryPendingOrders();
        }

        private void UpdateMarginUI(double margin)
        {
            lblMargin.Text = $"NT$ {margin:N0}";
            if (margin > 0)
                lblMargin.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220));
        }

        private void UpdatePnLUI(double pnl)
        {
            lblTodayPnL.Text = $"NT$ {pnl:N0}";
            if (pnl > 0)
                lblTodayPnL.Foreground = System.Windows.Media.Brushes.Red;
            else if (pnl < 0)
                lblTodayPnL.Foreground = System.Windows.Media.Brushes.LimeGreen;
            else
                lblTodayPnL.Foreground = System.Windows.Media.Brushes.LightGray;
        }

        private void InitYuantaOrd()
        {
            if (_yuantaOrd != null) return;
            _yuantaOrd = new YuantaOrdLib.YuantaOrdClass();
            _yuantaOrd.OnLogonS += OnOrdLogonS;
            _yuantaOrd.OnUserDefinsFuncResult += OnOrdUserDefinsFuncResult;
            _yuantaOrd.OnReportQuery += OnOrdReportQuery;
            _yuantaOrd.OnOrdMatF += OnOrdMatF;
            _pnlCalculator.Reset();
        }

        private int LoginYuantaOrd(string user, string pwd)
        {
            if (_yuantaOrd == null) return -1;
            return _yuantaOrd.SetFutOrdConnection("api.yuanta.com.tw", 443, user, pwd);
        }

        private void OnOrdLogonS(int status, string msg)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                AppendLog($"【下單】登入狀態: {status} - {msg}");
                if (status == 0) {
                    QueryTodayPnL();
                    QueryCurrentPositions();
                    QueryPendingOrders();
                }
            }));
        }

        private void QueryTodayPnL()
        {
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount)) return;
            string param = $"Func=FA003|bhno={_currentBranch}|acno={_currentAccount}|suba=|type=1|currency=TWD";
            _yuantaOrd.UserDefinsFunc(param, "FA003");
        }

        private void QueryCurrentPositions()
        {
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount)) return;
            string param = $"Func=FA002|bhno={_currentBranch}|acno={_currentAccount}|suba=|kind=F|FC=N";
            _yuantaOrd.UserDefinsFunc(param, "FA002");
        }

        private void QueryPendingOrders()
        {
            if (_yuantaOrd == null || string.IsNullOrEmpty(_currentAccount)) return;
            _yuantaOrd.ReportQuery("F", _currentBranch, _currentAccount, "", "1", "3", "0");
        }

        private void OnOrdUserDefinsFuncResult(int RowCount, string Results, string WorkID)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                if (WorkID == "FA003" && RowCount > 0)
                {
                    string[] fields = Results.Split('|');
                    int colCount = fields.Length / RowCount;
                    int equityIdx = -1;
                    for (int j = 0; j < colCount; j++) {
                        if (fields[j].Trim().ToUpper() == "EQUITY") equityIdx = j;
                    }
                    if (equityIdx != -1 && RowCount > 1 && equityIdx < fields.Length) {
                        string val = fields[colCount + equityIdx].Trim();
                        if (double.TryParse(val, out double eq)) UpdateMarginUI(eq);
                    }
                }
                else if (WorkID == "FA002")
                {
                    if (RowCount == 0 || Results.Contains("TOTAL_OFF_POSITION=0") || Results.Contains("查無資料"))
                    {
                        lblCurrentPosition.Text = "無部位";
                        return;
                    }
                    string[] fields = Results.Split('|');
                    int colCount = fields.Length / RowCount;
                    int bIdx = -1, sIdx = -1, pIdx = -1;
                    for (int j = 0; j < colCount; j++) {
                        string col = fields[j].Trim().ToUpper();
                        if (col == "BS") bIdx = j;
                        if (col == "OFF_POSITION") sIdx = j;
                        if (col == "PRICE") pIdx = j;
                    }
                    if (bIdx != -1 && sIdx != -1 && pIdx != -1 && RowCount > 1) {
                        string bs = fields[colCount + bIdx].Trim();
                        string lots = fields[colCount + sIdx].Trim();
                        string price = fields[colCount + pIdx].Trim();
                        lblCurrentPosition.Text = $"{lots} 口 (成本: {price})";
                    }
                }
            }));
        }

        private void OnOrdReportQuery(int RowCount, string Results)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                PendingOrders.Clear();
                if (RowCount == 0 || string.IsNullOrEmpty(Results) || Results.Contains("查無資料")) return;
                string[] fields = Results.Split('|');
                int colCount = fields.Length / RowCount;
                int ordIdx = -1, cmdIdx = -1, bsIdx = -1, priceIdx = -1, qtyIdx = -1;
                for (int j = 0; j < colCount; j++) {
                    string col = fields[j].Trim().ToUpper();
                    if (col == "ORDER_NO") ordIdx = j;
                    if (col == "COMMODITY") cmdIdx = j;
                    if (col == "BS") bsIdx = j;
                    if (col == "PRICE" || col == "ORDER_PRICE") priceIdx = j;
                    if (col == "ORDER_QTY" || col == "QTY" || col.Contains("QTY")) qtyIdx = j;
                }
                if (ordIdx != -1 && cmdIdx != -1 && bsIdx != -1 && priceIdx != -1 && qtyIdx != -1) {
                    for (int i = 1; i < RowCount; i++) {
                        int r = i * colCount;
                        if (r + qtyIdx < fields.Length) {
                            string bsStr = fields[r + bsIdx].Trim() == "B" ? "買進" : "賣出";
                            double price = 0;
                            double.TryParse(fields[r + priceIdx].Trim(), out price);
                            int qty = 0;
                            int.TryParse(fields[r + qtyIdx].Trim(), out qty);
                            PendingOrders.Add(new PendingOrder {
                                OrderNo = fields[r + ordIdx].Trim(),
                                Symbol = fields[r + cmdIdx].Trim(),
                                Name = fields[r + cmdIdx].Trim(),
                                BS = bsStr,
                                Price = price,
                                Qty = qty
                            });
                        }
                    }
                }
            }));
        }

        private void CancelOrder_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is PendingOrder order && _yuantaOrd != null) {
                AppendLog($"【委託】送出取消指令: {order.OrderNo}");
                _yuantaOrd.SendOrderF("01", "F", _currentBranch, _currentAccount, "", order.OrderNo, order.BS == "買進" ? "B" : "S", order.Symbol, "0", "0", "0", "L", "0", "", "");
                System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => QueryPendingOrders())));
            }
        }

        private void OnOrdMatF(string symbol, string mattime, string matpri, string tmatqty)
        {
            QueryCurrentPositions();
            QueryPendingOrders();
        }
'''

content = re.sub(r'(\s*\}\s*\n\s*\}\s*\n\s*/// <summary>\s*\n\s*/// DispatcherTimer)', newMethods + r'\1', content)

content += '''
    public class PendingOrder
    {
        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BS { get; set; } = string.Empty;
        public int Qty { get; set; }
        public double Price { get; set; }
        public string OrderNo { get; set; } = string.Empty;
        public string DisplayText => $"{Name} {BS} {Qty}口 @ {Price}";
    }
'''

content = content.replace('InitializeComponent();', 'InitializeComponent();\n            icPendingOrders.ItemsSource = PendingOrders;')

content = content.replace('int ordRes = LoginYuantaOrd(_mktUser, _mktPwd);', 'InitYuantaOrd();\n                                      int ordRes = LoginYuantaOrd(_mktUser, _mktPwd);')
content = content.replace('_yuantaOrd.DoLogout();', '_yuantaOrd.DoLogout();\n                    _yuantaOrd.OnLogonS -= OnOrdLogonS;\n                    _yuantaOrd.OnUserDefinsFuncResult -= OnOrdUserDefinsFuncResult;\n                    _yuantaOrd.OnReportQuery -= OnOrdReportQuery;\n                    _yuantaOrd.OnOrdMatF -= OnOrdMatF;')


with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Done!')