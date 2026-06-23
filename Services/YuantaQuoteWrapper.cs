using System;
using System.Runtime.InteropServices;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// 行情全部資訊接收委派
    /// </summary>
    public delegate void GetMktAllReceivedDelegate(
        string symbol, string refPri, string openPri, string highPri, string lowPri,
        string upPri, string dnPri, string matchTime, string matchPri, string matchQty,
        string tolMatchQty, string bestBuyQty, string bestBuyPri, string bestSellQty, string bestSellPri,
        string fdbPri, string fdbQty, string fdsPri, string fdsQty, int reqType);

    /// <summary>
    /// 元大行情 COM ActiveX 互操作強型別封裝層。
    /// 徹底拋棄脆弱的 IReflect 動態反射，改用強型別 Interop 事件，保證 Tick 100% 穩定流入。
    /// </summary>
    public class YuantaQuoteWrapper
    {
        private readonly AxYuantaQuoteLib.AxYuantaQuote _axHost;

        // 當 COM 事件觸發時，通知主介面的委派事件
        public event Action<int, string, int>? MktStatusChanged;
        public event GetMktAllReceivedDelegate? GetMktAllReceived;

        /// <summary>
        /// 建構元大行情 Wrapper，繫結強型別控制項與事件
        /// </summary>
        /// <param name="axHost">AxYuantaQuote 強型別控制項實體</param>
        public YuantaQuoteWrapper(AxYuantaQuoteLib.AxYuantaQuote axHost)
        {
            _axHost = axHost;
            
            // 繫結強型別事件回呼
            _axHost.OnMktStatusChange += AxHost_OnMktStatusChange;
            _axHost.OnGetMktAll += AxHost_OnGetMktAll;
        }

        /// <summary>
        /// 強型別連線狀態變更事件轉發
        /// </summary>
        private void AxHost_OnMktStatusChange(object sender, AxYuantaQuoteLib._DYuantaQuoteEvents_OnMktStatusChangeEvent e)
        {
            try
            {
                MktStatusChanged?.Invoke(e.status, e.msg ?? "", e.reqType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM事件] 轉發 OnMktStatusChange 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 強型別即時 Tick 行情事件轉發
        /// </summary>
        private void AxHost_OnGetMktAll(object sender, AxYuantaQuoteLib._DYuantaQuoteEvents_OnGetMktAllEvent e)
        {
            try
            {
                // 關鍵修正：強型別 Event 參數中不包含 fdbPri/fdbQty/fdsPri/fdsQty，這些在主邏輯中也未被使用，在此轉發時直接帶入空字串即可。
                GetMktAllReceived?.Invoke(
                    e.symbol ?? "", e.refPri ?? "", e.openPri ?? "", e.highPri ?? "", e.lowPri ?? "",
                    e.upPri ?? "", e.dnPri ?? "", e.matchTime ?? "", e.matchPri ?? "", e.matchQty ?? "",
                    e.tolMatchQty ?? "", e.bestBuyQty ?? "", e.bestBuyPri ?? "", e.bestSellQty ?? "", e.bestSellPri ?? "",
                    "", "", "", "", e.reqType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM事件] 轉發 OnGetMktAll 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 強型別登入行情伺服器方法。
        /// </summary>
        /// <param name="user">登入帳號</param>
        /// <param name="pwd">登入密碼</param>
        /// <param name="ip">伺服器 IP</param>
        /// <param name="port">伺服器 Port</param>
        /// <param name="mode">盤別 (1 = T盤, 2 = T+1盤)</param>
        /// <param name="localLp">本機端點</param>
        public int SetMktLogon(string user, string pwd, string ip, string port, int mode, int localLp)
        {
            try
            {
                // 關鍵修正：強型別 SetMktLogon 的回傳型別為 void，我們直接呼叫並回傳 0 代表成功。
                _axHost.SetMktLogon(user, pwd, ip, port, mode, localLp);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 呼叫 SetMktLogon 失敗: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 強型別註冊訂閱商品行情方法。
        /// </summary>
        /// <param name="symbol">商品合約代碼 (例如 TXFJ4)</param>
        /// <param name="mode">訂閱模式 (1-Snapshot, 2-Update, 4-SnapshotUpd)</param>
        /// <param name="reqType">盤別</param>
        /// <param name="param">保留參數</param>
        public int AddMktReg(string symbol, int mode, int reqType, int param)
        {
            try
            {
                // 關鍵修正：元大 API 的 mode 參數在 Interop 中是 string 型別，需以 mode.ToString() 傳入
                return _axHost.AddMktReg(symbol, mode.ToString(), reqType, param);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 呼叫 AddMktReg 失敗: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 斷開 COM 事件連接點，徹底防止內存洩漏與殘留幽靈 Tick 觸發。
        /// </summary>
        public void DisconnectEvents()
        {
            try
            {
                _axHost.OnMktStatusChange -= AxHost_OnMktStatusChange;
                _axHost.OnGetMktAll -= AxHost_OnGetMktAll;
                Console.WriteLine("[COM] 已成功斷開強型別元大行情事件訂閱。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 斷開強型別事件異常: {ex.Message}");
            }
        }
    }
}
