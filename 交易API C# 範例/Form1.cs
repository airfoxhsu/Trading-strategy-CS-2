using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

using YuantaOrdLib;

namespace YuantaAPIDemo
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private YuantaOrdLib.YuantaOrdClass m_yuanta_ord = null;

        // 紀錄各項 Log 
        private void LogMessage(string str)
        {
            lock (listBox_log_msg)
            {
                listBox_log_msg.Items.Add(DateTime.Now.ToString("HH:mm:ss.fff") + ": " + str);
                listBox_log_msg.SelectedIndex = listBox_log_msg.Items.Count - 1;
            }
        }
        
        private void Form1_Load(object sender, EventArgs e)
        {
            m_yuanta_ord = new YuantaOrdClass();

            m_yuanta_ord.OnLogonS += new _DYuantaOrdEvents_OnLogonSEventHandler(m_yuanta_ord_OnLogonS);
            m_yuanta_ord.OnOrdMatF += new _DYuantaOrdEvents_OnOrdMatFEventHandler(m_yuanta_ord_OnOrdMatF);
            m_yuanta_ord.OnOrdRptF += new _DYuantaOrdEvents_OnOrdRptFEventHandler(m_yuanta_ord_OnOrdRptF);
            m_yuanta_ord.OnDealQuery += new _DYuantaOrdEvents_OnDealQueryEventHandler(m_yuanta_ord_OnDealQuery);
            m_yuanta_ord.OnReportQuery += new _DYuantaOrdEvents_OnReportQueryEventHandler(m_yuanta_ord_OnReportQuery);
            m_yuanta_ord.OnOrdResult += new _DYuantaOrdEvents_OnOrdResultEventHandler(m_yuanta_ord_OnOrdResult);
            m_yuanta_ord.OnRfOrdRptRF += new _DYuantaOrdEvents_OnRfOrdRptRFEventHandler(m_yuanta_ord_OnRfOrdRptRF);
            m_yuanta_ord.OnRfReportQuery += new _DYuantaOrdEvents_OnRfReportQueryEventHandler(m_yuanta_ord_OnRfReportQuery);
            m_yuanta_ord.OnRfOrdMatRF += new _DYuantaOrdEvents_OnRfOrdMatRFEventHandler(m_yuanta_ord_OnRfOrdMatRF);
            m_yuanta_ord.OnRfDealQuery += new _DYuantaOrdEvents_OnRfDealQueryEventHandler(m_yuanta_ord_OnRfDealQuery);
            m_yuanta_ord.OnUserDefinsFuncResult += new _DYuantaOrdEvents_OnUserDefinsFuncResultEventHandler(m_yuanta_ord_OnUserDefinsFuncResult);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            m_yuanta_ord.DoLogout();
        }

        //自訂
        void m_yuanta_ord_OnUserDefinsFuncResult(int RowCount, string Results,string WorkID)
        {
            //listBox_UserDefins.Items.Clear();
            if (RowCount <= 0 || Results.Length <= 0)
                return;

            listBox_UserDefins.Items.Add(WorkID);
            listBox_UserDefins.Items.Add(Results);

        }


        // 查詢委託回報
        void m_yuanta_ord_OnReportQuery(int RowCount, string Results)
        {
            listBox_rpt.Items.Clear();

            if (RowCount <= 0 || Results.Length <= 0)
                return;

            string[] rows = Results.Split('|');

            for(int i=0; i < RowCount; i++)
            {
                StringBuilder sb = new StringBuilder();
                for(int j=0; j < rows.Length / RowCount; j++)
                {
                    sb.Append(rows[i * (rows.Length / RowCount) + j]);
                    if (j < rows.Length / RowCount - 1)
                        sb.Append(',');
                    else
                        listBox_rpt.Items.Add(sb.ToString().ToUpper());
                }
            }
        }

        // 查詢成交回報
        void m_yuanta_ord_OnDealQuery(int RowCount, string Results)
        {
            listBox_mat.Items.Clear();

            if (RowCount <= 0 || Results.Length <= 0)
                return;

            string[] rows = Results.Split('|');

            for (int i = 0; i < RowCount; i++)
            {
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < rows.Length / RowCount; j++)
                {
                    sb.Append(rows[i * (rows.Length / RowCount) + j]);
                    if (j < rows.Length / RowCount - 1)
                        sb.Append(',');
                    else
                        listBox_mat.Items.Add(sb.ToString().ToUpper());
                }
            }
        }

        // 即時委託單號回報
        void m_yuanta_ord_OnOrdResult(int ID, string result)
        {
            string ord_str = String.Format("ID={0},result={1}", ID, result.Trim());

            listBox_ord.Items.Insert(0, ord_str.ToUpper());

            LogMessage("OnOrdResult(" + ord_str + ")");

        }

        // 即時成交回報
        void m_yuanta_ord_OnOrdMatF(string Omkt, string Buys, string Cmbf, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string S_Buys, string O_Prc, string A_Prc, string O_Qty, string Deal_Qty, string T_Date, string D_Time, string Order_No, string O_Src, string O_Lin, string Oseq_No)
        {
            string mat_str = String.Format("Omkt={0},Buys={1},Cmbf={2},Bhno={3},Acno={4},Suba={5},Symb={6},Scnam={7},O_Kind={8},S_Buys={9},O_Prc={10},A_Prc={11},O_Qty={12},Deal_Qty={13},T_Date={14},D_Time={15},Order_No={16},O_Src={17},O_Lin={18},Oseq_No={19}"
                                    , Omkt.Trim(), Buys.Trim(), Cmbf.Trim(), Bhno.Trim(), AcNo.Trim(), Suba.Trim(), Symb.Trim(), Scnam.Trim(), O_Kind.Trim(), S_Buys.Trim(), O_Prc.Trim(), A_Prc.Trim(), O_Qty.Trim(), Deal_Qty.Trim(), T_Date.Trim(), D_Time.Trim(), Order_No.Trim(), O_Src.Trim(), O_Lin.Trim(), Oseq_No.Trim());

            listBox_mat.Items.Insert(0, mat_str.ToUpper());
            listBox_mat.SelectedIndex = listBox_mat.Items.Count - 1;

            LogMessage("OnOrdMatF(" + mat_str + ")");

        }

        // 即時委託回報
        void m_yuanta_ord_OnOrdRptF(string Omkt, string Mktt, string Cmbf, string Statusc, string Ts_Code, string Ts_Msg, string Bhno, string AcNo, string Suba, string Symb, string Scnam, string O_Kind, string O_Type, string Buys, string S_Buys, string O_Prc, string O_Qty, string Work_Qty, string Kill_Qty, string Deal_Qty, string Order_No, string T_Date, string O_Date, string O_Time, string O_Src, string O_Lin, string A_Prc, string Oseq_No, string Err_Code, string Err_Msg, string R_Time, string D_Flag)
        {
            string rpt_str = String.Format("Omkt={0},Mktt={1},Cmbf={2},Statusc={3},Ts_Code={4},Ts_Msg={5},Bhno={6},Acno={7},Suba={8},Symb={9},Scnam={10},O_Kind={11},O_Type={12},Buys={13},S_Buys={14},O_Prc={15},O_Qty={16},Work_Qty={17},Kill_Qty={18},Deal_Qty={19},Order_No={20},T_Date={21},O_Date={22},O_Time={23},O_Src={24},O_Lin={25},A_Prc={26},Oseq_No={27},Err_Code={28},Err_Msg={29},R_Time={30},D_Flag={31}"
                                    , Omkt.Trim(), Mktt.Trim(), Cmbf.Trim(), Statusc.Trim(), Ts_Code.Trim(), Ts_Msg.Trim(), Bhno.Trim(), AcNo.Trim(), Suba.Trim(), Symb.Trim(), Scnam.Trim(), O_Kind.Trim(), O_Type.Trim(), Buys.Trim(), S_Buys.Trim(), O_Prc.Trim(), O_Qty.Trim(), Work_Qty.Trim(), Kill_Qty.Trim(), Deal_Qty.Trim(), Order_No.Trim(), T_Date.Trim(), O_Date.Trim(), O_Time.Trim(), O_Src.Trim(), O_Lin.Trim(), A_Prc.Trim(), Oseq_No.Trim(), Err_Code.Trim(), Err_Msg.Trim(), R_Time.Trim(), D_Flag.Trim());

            listBox_rpt.Items.Insert(0, rpt_str.ToUpper());

            LogMessage("OnOrdRptF(" + rpt_str + ")");
        }

        //即時委託回報(外期)
        void m_yuanta_ord_OnRfOrdRptRF(string exc, string Omkt, string Statusc, string Ts_Code, string Ts_Msg, string Bhno,
            string Acno, string Suba, string Symb, string Scnam, string O_Kind, string Buys, string S_Buys, string PriceType,
            string O_Prc1, string O_Prc2, string O_Qty, string Work_Qty, string Kill_Qty, string Deal_Qty, string Order_No,
            string O_Date, string O_Time, string O_Src, string O_Lin, string A_Prc, string Oseq_No, string Err_Code, string Err_Msg,
            string R_Time, string D_Flag)
        {
            string rpt_str = String.Format("Exchange={0},Omkt={1},Statusc={2},Ts_Code={3},Ts_Msg={4},Bhno={5},Acno={6},Suba={7},Symb={8},Scnam={9},O_Kind={10},Buys={11},S_Buys={12},PriceType={13},O_Prc1={14},O_Prc2={15},O_Qty={16},Work_Qty={17},Kill_Qty={18},Deal_Qty={19},Order_No={20},O_Date={21},O_Time={22},O_Src={23},O_Lin={24},A_Prc={25},Oseq_No={26},Err_Code={27},Err_Msg={28},R_Time={29},D_Flag={30}", exc.Trim(), Omkt.Trim(), Statusc.Trim(), Ts_Code.Trim(), Ts_Msg.Trim(), Bhno.Trim(), Acno.Trim(), Suba.Trim(), Symb.Trim(), Scnam.Trim(), O_Kind.Trim(), Buys.Trim(), S_Buys.Trim(), PriceType.Trim(), O_Prc1.Trim(), O_Prc2.Trim(), O_Qty.Trim(), Work_Qty.Trim(), Kill_Qty.Trim(), Deal_Qty.Trim(), Order_No.Trim(), O_Date.Trim(), O_Time.Trim(), O_Src.Trim(), O_Lin.Trim(), A_Prc.Trim(), Oseq_No.Trim(), Err_Code.Trim(), Err_Msg.Trim(), R_Time.Trim(), D_Flag.Trim());
            listBox_rpt.Items.Insert(0, rpt_str.ToUpper().Trim());
            LogMessage("OnRfOrdRptRF(" + rpt_str + ")");
        }

        //即時成交回報(外期)
        void m_yuanta_ord_OnRfOrdMatRF(string exc, string Omkt, string Bhno,
            string Acno, string Suba, string Symb, string Scnam, string O_Kind, string Buys, string S_Buys, string PriceType,
            string O_Prc1, string O_Prc2, string A_Prc, string O_Qty, string Deal_Qty, string O_Date, string O_Time,
            string Order_No, string O_Src, string O_Lin, string Oseq_No)
        {
            string mat_str = String.Format("Exchange={0},Omkt={1},Bhno={2},Acno={3},Suba={4},Symb={5},Scnam={6},O_Kind={7},Buys={8},S_Buys={9},PriceType={10},O_Prc1={11},O_Prc2={12},A_Prc={13},O_Qty={14},Deal_Qty={15},Order_No={16},O_Date={17},D_Time={18},O_Src={19},O_Lin={20},Oseq_No={21}", exc.Trim(), Omkt.Trim(), Bhno.Trim(), Acno.Trim(), Suba.Trim(), Symb.Trim(), Scnam.Trim(), O_Kind.Trim(), Buys.Trim(), S_Buys.Trim(), PriceType.Trim(), O_Prc1.Trim(), O_Prc2.Trim(), A_Prc.Trim(), O_Qty.Trim(), Deal_Qty.Trim(), Order_No.Trim(), O_Date.Trim(), O_Time.Trim(), O_Src.Trim(), O_Lin.Trim(), Oseq_No.Trim());

            listBox_mat.Items.Insert(0, mat_str.ToUpper().Trim());
            listBox_mat.SelectedIndex = listBox_mat.Items.Count - 1;

            LogMessage("OnRfOrdRptRF(" + mat_str + ")");
        }

        //委託查詢回報(外期)
        void m_yuanta_ord_OnRfReportQuery(int RowCount, string Results)
        {
            listBox_rpt.Items.Clear();

            if (RowCount <= 0 || Results.Length <= 0)
                return;

            string[] rows = Results.Split('|');

            for (int i = 0; i < RowCount; i++)
            {
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < rows.Length / RowCount; j++)
                {
                    sb.Append(rows[i * (rows.Length / RowCount) + j]);
                    if (j < rows.Length / RowCount - 1)
                        sb.Append(',');
                    else
                        listBox_rpt.Items.Add(sb.ToString().ToUpper());
                }
            }
        }

        // 查詢成交回報(外期)
        void m_yuanta_ord_OnRfDealQuery(int RowCount, string Results)
        {
            listBox_mat.Items.Clear();

            if (RowCount <= 0 || Results.Length <= 0)
                return;

            string[] rows = Results.Split('|');

            for (int i = 0; i < RowCount; i++)
            {
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < rows.Length / RowCount; j++)
                {
                    sb.Append(rows[i * (rows.Length / RowCount) + j]);
                    if (j < rows.Length / RowCount - 1)
                        sb.Append(',');
                    else
                        listBox_mat.Items.Add(sb.ToString().ToUpper());
                }
            }
        }

        // 清除期貨可用帳號
        private void clear_acno()
        {
            comboBox_acno.Items.Clear();
            comboBox_acno.Text = "";
        }

        // 將接收到的帳號新增進帳號的 ComboBox 中
        private void insert_acno(string acc_list)
        {
            if (acc_list == null || acc_list.Length == 0)
                return;

            string[] acno = acc_list.Split(';');
            for (int i = 0; i < acno.Length; i++)
            {
                // 過濾非期貨帳號
                if (acno[i][0] != '2')
                    continue;

                comboBox_acno.Items.Add(acno[i].Substring(2, acno[i].Length - 2));
            }

            if (comboBox_acno.Items.Count > 0)
                comboBox_acno.SelectedIndex = 0;
        }

        // TLinkStatus: 回傳連線狀態, AccList: 回傳帳號, Casq: 憑證序號, Cast: 憑證狀態
        void m_yuanta_ord_OnLogonS(int TLinkStatus, string AccList, string Casq, string Cast)
        {
            LogMessage(String.Format("OnLogonS: {0},{1},{2},{3}", TLinkStatus, AccList, Casq, Cast));

            if (TLinkStatus == 2)
                insert_acno(AccList.Trim());

            textBox_status_code.Text = TLinkStatus.ToString();
        }

        // API 登入
        private void button_login_Click(object sender, EventArgs e)
        {
            button_login.Enabled = false;

            int ret_code = m_yuanta_ord.SetFutOrdConnection(textBox_id.Text, textBox_passwd.Text, textBox_ip.Text, int.Parse(textBox_port.Text));
            textBox_status_code.Text = ret_code.ToString();

            LogMessage(String.Format("SetFutOrdConnection() = {0}", ret_code));

            // 回傳 2 表示已經在 "已登入" 連線狀態  
            if (ret_code != 2)
                clear_acno();

            button_login.Enabled = true;
        }

        // API 登出
        private void button_logout_Click(object sender, EventArgs e)
        {
            button_logout.Enabled = false;
            clear_acno();

            m_yuanta_ord.DoLogout();
            LogMessage("DoLogout()");

            textBox_status_code.Text = "0";

            button_logout.Enabled = true;
        }

        // 下單委託
        private void button_order_Click(object sender, EventArgs e)
        {
            button_order.Enabled = false;

            if (comboBox_acno.Items.Count > 0)
            {
                // 判斷下單委託是否為同步 or 非同步
                if (checkBox_forwait.Checked == true)
                    m_yuanta_ord.SetWaitOrdResult(1);
                else
                    m_yuanta_ord.SetWaitOrdResult(0);

                string bs2 = "";
                if (comboBox_bscode2.Text.Length > 0)
                    bs2 = comboBox_bscode2.Text.Substring(0, 1);

                string[] rows = comboBox_acno.Text.Split('-');

                string ret_no = string.Empty;
                if (RFCheckBox.Checked)
                {
                    LogMessage(String.Format("RfSendOrder() {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}",
                    comboBox_fcode.Text.Substring(0, 2), comboBox_ctype.Text.Substring(0, 1), rows[0], rows[1], rows[2],
                        textBox_ordno.Text.Trim(), comboBox_bscode.Text.Substring(0, 1),
                        textBox_futno.Text.Trim(), comboBox_pritype.Text.Substring(0, 1), textBox_price.Text.Trim(),
                        RF_StopPrc.Text.Trim(), textBox_lots.Text.Trim(), comboBox_offset.Text.Trim()));
                    ret_no = m_yuanta_ord.RfSendOrder(comboBox_fcode.Text.Substring(0, 2), comboBox_ctype.Text.Substring(0, 1), rows[0], rows[1], rows[2],
                        textBox_ordno.Text.Trim(), comboBox_bscode.Text.Substring(0, 1),
                        textBox_futno.Text.Trim(), comboBox_pritype.Text.Substring(0, 1), textBox_price.Text.Trim(),
                        RF_StopPrc.Text.Trim(), textBox_lots.Text.Trim(), comboBox_offset.Text.Substring(0, 1));
                    LogMessage("RfSendOrder() = " + ret_no);
                }
                else {
                    LogMessage(String.Format("SendOrderF() {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}, {13}, {14}",
                    comboBox_fcode.Text.Substring(0, 2), comboBox_ctype.Text.Substring(0, 1), rows[0],
                    rows[1], rows[2], textBox_ordno.Text, comboBox_bscode.Text.Substring(0, 1),
                    textBox_futno.Text, textBox_price.Text, textBox_lots.Text, comboBox_offset.Text.Substring(0, 1),
                    comboBox_pritype.Text.Substring(0, 1), comboBox_ordcond.Text.Substring(0, 1),
                    bs2, textBox_futno2.Text));
                    ret_no = m_yuanta_ord.SendOrderF(comboBox_fcode.Text.Substring(0, 2), comboBox_ctype.Text.Substring(0, 1),
                                    rows[0], rows[1], rows[2], textBox_ordno.Text,
                                    comboBox_bscode.Text.Substring(0, 1), textBox_futno.Text, textBox_price.Text, textBox_lots.Text,
                                    comboBox_offset.Text.Substring(0, 1), comboBox_pritype.Text.Substring(0, 1),
                                    comboBox_ordcond.Text.Substring(0, 1), bs2, textBox_futno2.Text);
                    LogMessage("SendOrderF() = " + ret_no);
                }
                
            }
            else
            {
                LogMessage("請先完成登入");
            }


            button_order.Enabled = true;
        }

        // 查詢委託明細資料
        private void button_rpt_Click(object sender, EventArgs e)
        {
            button_rpt.Enabled = false;

            if (comboBox_acno.Items.Count > 0)
            {
                string[] rows = comboBox_acno.Text.Split('-');
                if (RFCheckBox.Checked)
                {
                    int ret_code = m_yuanta_ord.RfReportQuery(rows[0], rows[1], rows[2], comboBox_rpt_stus.Text.Substring(0, 1), comboBox_rpt_cflg.Text.Substring(0, 1), RF_Exc.Text.Trim());
                    LogMessage(String.Format("RfReportQuery() = {0}", ret_code));
                }
                else 
                {
                    int ret_code = m_yuanta_ord.ReportQuery("F", rows[0], rows[1], rows[2],
                            comboBox_rpt_stus.Text.Substring(0, 1), comboBox_rpt_kind.Text.Substring(0, 1), comboBox_rpt_cflg.Text.Substring(0, 1));

                    LogMessage(String.Format("ReportQuery() = {0}", ret_code));
                }

            }
            else
            {
                LogMessage("請先完成登入");
            }

            button_rpt.Enabled = true;
        }

        // 查詢成交明細資料
        private void button_mat_Click(object sender, EventArgs e)
        {
            button_mat.Enabled = false;

            if (comboBox_acno.Items.Count > 0)
            {
                string[] rows = comboBox_acno.Text.Split('-');
                if (RFCheckBox.Checked) {
                    int ret_code = m_yuanta_ord.RfDealQuery(rows[0], rows[1], rows[2], RF_Exc.Text.Trim());
                    LogMessage(String.Format("RfDealQuery() = {0}", ret_code));
                }
                else
                {
                    int ret_code = m_yuanta_ord.DealQuery("F", rows[0], rows[1], rows[2], comboBox_mat_kind.Text.Substring(0, 1));
                    LogMessage(String.Format("DealQuery() = {0}", ret_code));
                }
            }
            else
            {
                LogMessage("請先完成登入");
            }


            button_mat.Enabled = true;
        }

        private void RFCheckBox_CheckedChanged(object sender, EventArgs e)
        {
                    comboBox_pritype.Items.Clear();
                    comboBox_ctype.Items.Clear();
            if(RFCheckBox.Checked)
            {
                RF_Ord_Box.Visible = true;
                comboBox_fcode.Items.RemoveAt(1); //移除 02-減量
                comboBox_ctype.Items.Add("F-期貨");
                comboBox_ctype.Items.Add("O-選擇權");
                comboBox_pritype.Items.Add("1-限價");
                comboBox_pritype.Items.Add("2-市價");
                comboBox_pritype.Items.Add("4-停損");
                comboBox_pritype.Items.Add("8-停損限價");

                comboBox_ordcond.Enabled = false;
                comboBox_offset.Enabled = false;
                textBox_futno2.Enabled = false;
                comboBox_bscode2.Enabled = false;

            }else{
                comboBox_fcode.Items.Insert(1, "02-減量");
                comboBox_ctype.Items.Add("0-期貨");
                comboBox_ctype.Items.Add("1-選擇權");
                comboBox_ctype.Items.Add("4-期貨價差");
                comboBox_pritype.Items.Add("L-限價");
                comboBox_pritype.Items.Add("M-市價");
                RF_Ord_Box.Visible = false;

                comboBox_ordcond.Enabled = true;
                comboBox_offset.Enabled = true;
                textBox_futno2.Enabled = true;
                comboBox_bscode2.Enabled = true;
        }        
            comboBox_pritype.SelectedIndex = 0;
            comboBox_ctype.SelectedIndex = 0;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (comboBox_acno.Items.Count > 0)
            {
                int ret_code = m_yuanta_ord.UserDefinsFunc(textBox_Params.Text.Trim(), textBox_workID.Text.Trim());
            }
            else
            {
                LogMessage("請先完成登入");
            }
        }
    }
}