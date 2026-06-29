# 專案開發筆記 (Project Notes)

這個檔案用來記錄開發過程中的技術細節、踩坑記錄、目前進度以及待辦清單 (TODO)。

## 專案進度

### 2026-06-29 專案初始化與結構整理
- [x] 完成專案基礎文件初始化：
  - [x] `ANTIGRAVITY.md` (AI 協作規則)
  - [x] `README.md` (專案概覽與架構)
  - [x] `PROJECT_NOTES.md` (開發筆記與待辦清單)
- [ ] 盤點專案根目錄下暫存的 Python 輔助檔案與日誌檔案，評估是否需要歸檔或清理。

### 2026-06-29 狀態追蹤修復與重構
- [x] 實作委託流水號 (`OseqNo`) 絕對匹配機制，徹底解決退單配對失誤、帳號過濾誤殺與 Race Condition 造成的狀態卡死或覆蓋問題。
- [x] 停損破位機制修復，不再被舊狀態覆蓋「已破」標記。
- [x] 優化 UI `DataGrid` 自適應欄寬，避免長字串撐爆版面。

---

## 待辦清單 (TODO)

### 1. 程式碼整理與維護
- [ ] **Python 暫存腳本評估**：
  - 根目錄下有許多 `fix*.py` 與 `patch*.py` 檔案（例如 `fix2.py`, `patch8.py` 等）。
  - 需要確認這些檔案的用途，若已不再需要，應予以清理或移至 `scratch/` 資料夾，以保持根目錄整潔。
- [ ] **巨大檔案評估**：
  - 根目錄存在較大的記憶體傾印檔案 `dump.dmp` (約 610MB) 與 `dump_20260611_122802.dmp` (約 615MB)。
  - 確認是否需要保留，否則建議刪除或移出專案目錄以釋放空間，並確保不會被 Git 誤追蹤（`.gitignore` 已設定排除 `*.dmp`）。

### 2. 功能測試與驗證
- [ ] **極值觀測邏輯驗證**：
  - 詳細閱讀並驗證 `TradingEngine.CalcSimulationResultsInternal` 中關於「量增速增」的判定是否符合預期。
  - 撰寫/執行 `ExtremeSignalAppCS.Tests` 單元測試，確保反轉點（A點）與觸發點（B點）的抓取無誤。
- [ ] **元大 API 串接測試**：
  - 檢視 `YuantaApiLog` 日誌，確認 API 連線狀態、Tick 接收頻率，以及是否有異常的斷線重連。

---

## 關鍵技術重點摘要

### 極值觀測觸發公式
1. **收集條件**：
   - 往前尋找 `N` 筆外盤/內盤 Tick（不得有更極端價格）。
   - 往後尋找 `N` 筆對立盤 Tick（不得破極端價格）。
2. **速增條件**：
   $$\text{AvgPostInterval} < \text{AvgPreInterval}$$
3. **量增條件**：
   $$\text{PostVolume} > \text{PreVolume}$$

詳細邏輯請參閱：[極值觀測表觸發邏輯重點紀錄.md](file:///h:/Coding/CSharp/Trading-strategy-CS-2/重要筆記/極值觀測表觸發邏輯重點紀錄.md)
