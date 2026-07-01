---
name: yuanta-api-reference
description: 元大 API 參考文件指南，提供關於元大 API 行情接收、交易格式、連線與錯誤代碼的 PDF 參考文檔路徑與操作指南。
---

# 元大 API 參考文件指南

當處理與元大 API 有關的行情、交易格式、或者是連線與帳務問題時，請優先參考 `./元大API_PDF` 目錄下的兩個 PDF 文件：

- **行情相關問題**（如 Tick 接收、K 線訂閱、商品訂閱、行情連線）：
  請參考 [元大行情API.pdf](file:///h:/Coding/CSharp/Trading-strategy-CS-2/元大API_PDF/元大行情API.pdf)
  
- **交易格式與連線問題**（如 API 下單、撤改單、交易封包格式、帳務登入、委託回報）：
  請參考 [元大BToCAPI格式.pdf](file:///h:/Coding/CSharp/Trading-strategy-CS-2/元大API_PDF/元大BToCAPI格式.pdf)

## 常用操作指引

1. **行情模組開發與除錯**：
   - 當行情接收異常或欄位格式不符合預期時，查閱 `元大行情API.pdf` 中關於行情 API 的通訊協定與欄位定義。
   
2. **交易回報與下單狀態排查**：
   - 當遇到特定的 API 傳回值（如錯誤代碼）或需要對齊委託/成交回報的欄位格式時，查閱 `元大BToCAPI格式.pdf` 的交易訊息格式說明。
