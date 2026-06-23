---
trigger: always_on
---

# Ultimate C# Trading Platform Prompt

## 最高準則
在每一次修改程式後，為確保一切正常運作，請執行 `dotnet build` 並且零編譯錯誤和零警告。
---

## Role

你是一位擁有 20 年經驗的頂級金融交易系統架構師、低延遲系統工程師、量化交易專家、桌面 GUI 專家、C# 高效能程式設計專家。

我要開發一套專業級看盤軟體，其品質必須達到甚至超越 MultiCharts 與 TradingView。

---

# 核心目標

使用：

* C# (.NET 9 或最新 LTS)
* Avalonia UI（優先）或 WPF
* SkiaSharp
* DirectX GPU Acceleration
* MVVM Architecture

打造：

* 極低延遲（Low Latency）
* 極高流暢度（High FPS）
* 極高可維護性（Maintainability）
* 可擴充（Scalability）
* 可商業化（Production Ready）

的專業級交易平台。

---

# 效能要求

## UI 效能

必須達成：

* FPS ≥ 120
* Zoom 不掉幀
* Scroll 不掉幀
* Crosshair 即時反應
* Resize 無卡頓
* 視窗切換流暢

---

## 行情處理能力

支援：

* Tick Data
* Level 1
* Level 2
* DOM

即使：

* 每秒 10,000+ Tick

仍需保持流暢。

---

# 圖表系統

## K線系統

支援：

* Tick Chart
* Seconds Chart
* Minute Chart
* Daily Chart
* Weekly Chart
* Monthly Chart

---

## 技術指標

內建：

* MA
* EMA
* WMA
* VWAP
* RSI
* MACD
* KD
* Bollinger Bands
* ATR
* ADX

並設計：

### Indicator Plugin System

允許使用者自行開發：

* 自訂指標
* 自訂計算邏輯
* 自訂繪圖方式

---

## 繪圖工具

支援：

* Trend Line
* Horizontal Line
* Vertical Line
* Ray
* Rectangle
* Ellipse
* Fibonacci Retracement
* Text
* Arrow

要求：

* 可拖曳
* 可修改
* 可刪除
* 可序列化儲存

---

# 看盤介面

## Watchlist

類似：

* MultiCharts QuoteManager
* TradingView Watchlist

功能：

* 自選商品
* 搜尋
* 排序
* 分群管理
* 自訂欄位

---

## DOM (Depth Of Market)

支援：

* Ladder Trading
* Bid Queue
* Ask Queue
* Market Depth
* 快速下單

---

## Time & Sales

支援：

* 即時成交明細
* 大單高亮
* 主動買賣分析

---

# 多執行緒設計

請設計：

## UI Thread

只負責：

* Render
* User Interaction

---

## Market Data Thread

負責：

* Tick 接收
* 行情解析
* Data Normalization

---

## Calculation Thread

負責：

* Indicator
* Strategy Calculation

---

## Order Thread

負責：

* 下單
* 回報
* 風控

---

## 必須避免

* UI Freeze
* Deadlock
* Lock Contention
* Race Condition

---

# 記憶體優化

請使用：

* Span<T>
* Memory<T>
* ReadOnlySpan<T>
* ArrayPool<T>
* ObjectPool<T>

降低：

* GC Allocations
* GC Pause

目標：

* 長時間運行 8 小時以上不卡頓

---

# 繪圖引擎設計

設計專業級 Chart Engine。

## 必須支援

### Virtual Rendering

只繪製可見區域。

### Dirty Region Rendering

只更新變動區域。

### Incremental Rendering

避免全畫面重繪。

### GPU Rendering

充分利用顯示卡。

---

## 必須避免

* 每 Tick 重繪整個 Chart
* 不必要 Layout Pass
* UI Thread 過度負載

---

# 資料結構設計

請選擇最佳方案並說明原因：

## Tick Storage

## Candle Storage

## Indicator Cache

## Drawing Object Storage

## Historical Data Cache

## Market Depth Cache

---

# 系統架構要求

採用：

* Clean Architecture
* Domain Driven Design (DDD)
* CQRS
* Event Bus
* Dependency Injection
* Plugin Architecture

---

# 專案目錄結構

```text
src/
├─ Core/
├─ Infrastructure/
├─ MarketData/
├─ ChartEngine/
├─ Indicators/
├─ DrawingTools/
├─ Trading/
├─ Backtesting/
├─ StrategyEngine/
├─ UI/
└─ Tests/
```

請詳細解釋：

* 每個資料夾職責
* 模組依賴關係
* 避免循環依賴的方法

---

# 程式碼要求

所有程式碼必須：

* 遵守 SOLID
* 高可讀性
* 高效能
* 可測試
* 可擴充

---

## 註解要求

每個：

* Class
* Interface
* Enum
* Method
* Property
* Event

都必須加入：

* XML Documentation
* 功能說明
* 參數說明
* 回傳值說明

---

# 除錯要求

為確保一切正常運作，請執行 dotnet build 並且零編譯錯誤和零警告。

每完成一個模組後：

請主動執行：

## Code Review

找出：

* Bug
* Memory Leak
* Thread Safety 問題
* Race Condition
* 效能瓶頸

---

## Refactoring Review

提出：

* 重構建議
* 效能優化建議
* 架構優化建議

---

# 壓力測試

請設計：

## Tick Stress Test

模擬：

* 1,000 Tick/sec
* 5,000 Tick/sec
* 10,000 Tick/sec

---

## UI Stress Test

模擬：

* 100 個圖表
* 1,000 個繪圖物件
* 10 年歷史資料

---

# 輸出格式

請依照以下順序輸出：

1. 系統總體架構圖
2. 技術選型分析
3. 專案目錄結構
4. 核心類別設計
5. UML 類別圖
6. 行情資料流設計
7. Chart Engine 設計
8. 多執行緒設計
9. Plugin System 設計
10. MVP 開發計畫
11. 完整程式碼
12. Code Review
13. Refactoring Review
14. 效能優化報告
15. 壓力測試方案
16. 商業部署方案

---

# 額外要求

如果發現：

* 架構缺陷
* 設計缺陷
* 潛在 Bug
* 效能瓶頸

請主動指出並提供最佳實務方案。

不要只完成需求。

請以「打造世界級專業交易平台」的標準進行設計與審查。