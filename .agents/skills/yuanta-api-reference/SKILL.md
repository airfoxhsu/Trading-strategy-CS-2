---
name: yuanta-api-reference
description: 當涉及元大 API 有關行情或者是交易連線的問題時，提供相關規格書與文件的指引。
---

# 元大 API 行情與交易連線問題指引

當在專案中遇到元大 API 的行情接收或交易連線問題時，請參考專案目錄下 `元大API_PDF` 資料夾內的規格書文件：

- **行情連線與行情接收問題**：
  請參考 [元大行情API.pdf](file:///h:/Coding/CSharp/Trading-strategy-CS-2/元大API_PDF/元大行情API.pdf) 檔案。
- **交易連線與交易委託問題**：
  請參考 [元大BToCAPI格式.pdf](file:///h:/Coding/CSharp/Trading-strategy-CS-2/元大API_PDF/元大BToCAPI格式.pdf) 檔案。

## 常用操作指引
1. 在診斷行情遺漏、K線計算錯誤、Tick 接收不完整等問題時，應先查閱行情 API 的規格，確認封包格式與事件觸發機制。
2. 在診斷下單失敗、回報序號不符、帳務登入等交易問題時，應先查閱 BToC API 格式，確認欄位長度、回報代碼（如 "04"、"03" 等）之定義。
