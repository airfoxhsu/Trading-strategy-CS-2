# ExtremeSignalAppCS

極值觀測與交易策略程式 - 基於 C# WPF 開發。

## 專案簡介

本專案主要用於市場交易行情的極值觀測與策略執行。核心邏輯在於監控 Tick 行情，當市場發生**「局部極端價格」**時，根據**「速度」與「成交量」**的變化（即「量增速增」條件）來捕捉潛在的市場反轉點，並記錄於極值觀測表 (`dgObserver`) 中，以利進一步的交易決策或自動化交易。

## 核心邏輯架構

系統主要包含以下核心模組：
- **Trading (交易引擎)**：包含 `TradingEngine.cs` 等核心邏輯，負責計算模擬結果、處理 Tick 行情、判定 A點（極點）與 B點（觸發點）並監控防守停損價。
- **Controls (介面控制項)**：自訂的 WPF 控制項，如極值觀測表 (`dgObserver`)。
- **Models (資料模型)**：定義 Tick、K線、策略狀態、觀測紀錄等資料結構。
- **Services (服務層)**：負責串接元大 API (Yuanta API) 及其他外部服務。
- **Backtesting (回測系統)**：用於對歷史 Tick 資料進行策略回測與參數優化。
- **ExtremeSignalAppCS.Tests (測試專案)**：針對核心邏輯的單元測試。

## 開發環境與依賴

- **開發語言**：C# (.NET Framework / .NET Core 專案)
- **UI 框架**：WPF (Windows Presentation Foundation)
- **外部 API**：元大交易 API
- **輔助腳本**：根目錄下包含若干 Python 腳本（例如資料修正、Log 解析或簡單的畫圖工具）

## 快速開始

1. 使用 Visual Studio 開啟專案資料夾。
2. 確認已安裝對應的 .NET 開發套件與元大 API 相關元件。
3. 建置並執行 `ExtremeSignalAppCS` 專案。
4. 相關日誌將輸出至根目錄下的 `YuantaApiLog.[日期].log` 或 `Logs` 資料夾中。

## 相關文件

- [極值觀測表觸發邏輯重點紀錄](file:///h:/Coding/CSharp/Trading-strategy-CS-2/重要筆記/極值觀測表觸發邏輯重點紀錄.md)：詳細記錄了極點（A點）、觸發點（B點）的定義與量增速增的計算公式。
- [ANTIGRAVITY.md](file:///h:/Coding/CSharp/Trading-strategy-CS-2/ANTIGRAVITY.md)：AI 助手協作原則與狀態記錄。
- [PROJECT_NOTES.md](file:///h:/Coding/CSharp/Trading-strategy-CS-2/PROJECT_NOTES.md)：開發筆記與待辦清單 (TODO)。
