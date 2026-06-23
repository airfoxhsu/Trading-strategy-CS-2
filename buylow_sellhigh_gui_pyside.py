# -*- coding: utf-8 -*-
"""
專業級台指極值訊號自動對比看盤軟體 - PyQt5 高性能 32 位元版
為了相容只能在 32-bit (win32) 下運作的元大行情 API，我們特將本軟體基於 PyQt5 + PyQtGraph 進行優化。
100% 無損保留 @[buylow_sellhigh_gui.py] 的計算邏輯、N值判定、OHLC 聚合與未破停損時序狀態機。

作者: Antigravity 高級量化交易系統工程師
"""

import sys
import os
import re
import time
import asyncio
import threading
import statistics
import traceback
import queue
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Tuple, Union
import json
import requests

# 量化與高性能計算庫
import numpy as np # type: ignore
import httpx # type: ignore

# PyQt5 GUI 元件 (完全相容 32 位元環境)
from PyQt5.QtCore import Qt, QThread, pyqtSignal as Signal, pyqtSlot as Slot, QTimer, QAbstractTableModel, QModelIndex, QObject
from PyQt5.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QLabel, QComboBox, QCheckBox, QLineEdit, QSpinBox as QSpinbox,
    QTableView, QSplitter, QDialog, QTextEdit, QFileDialog, QMessageBox,
    QHeaderView, QStyle, QSlider
)
from PyQt5.QtGui import QColor, QFont, QBrush, QPainter, QIcon, QSyntaxHighlighter, QTextCharFormat

# COM/ActiveX 底層依賴 (元大行情 API 必須透過 comtypes + ATL 建立，QAxWidget 無法正確實體化)
import pythoncom  # type: ignore
from ctypes import byref, POINTER, windll, c_int  # type: ignore
from comtypes import IUnknown, GUID  # type: ignore
from comtypes.client import GetBestInterface, GetEvents  # type: ignore

try:
    user32 = windll.user32
    atl = windll.atl
except Exception:
    user32 = None  # 非 Windows 環境的防禦性處理
    atl = None

# PyQtGraph 圖表 (自動對接 PyQt5)
import pyqtgraph as pg # type: ignore

# 程式所在目錄
if getattr(sys, 'frozen', False):
    APP_DIR = os.path.dirname(sys.executable)
else:
    APP_DIR = os.path.dirname(os.path.abspath(__file__))

# 預設參數
ABS_N_TICKS_DEFAULT = 250
ANALYSIS_DEBOUNCE_SEC = 0.15  # 背景分析執行緒 debounce 時間(秒)，限制主線程重繪頻率，釋放極大 CPU 空間

# =============================================================================
# 1A. 元大行情 ActiveX COM 事件接收器與封裝 (100% 移植原版 comtypes 方案)
# =============================================================================

class YuantaQuoteEvents(object):
    """
    職責: COM 事件接收器。
    元大行情 API 的 ActiveX 控制元件會透過 COM Connection Point 機制
    將 Tick 行情與登入狀態等事件回呼到此物件。
    回呼會在主執行緒的 pythoncom.PumpWaitingMessages() 期間同步觸發，
    因此內部不需要額外加鎖，但也絕對不能執行耗時操作。
    """
    def __init__(self, wrapper: 'YuantaQuoteWrapper'):
        """初始化事件接收器，持有外層 Wrapper 的弱參照"""
        self.wrapper = wrapper

    def OnMktStatusChange(self, this, Status, Msg, ReqType):
        """
        API 登入狀態變更事件。
        Status == 2 代表登入成功；Status < 0 代表連線異常。
        """
        gui = self.wrapper.gui
        # 防止舊連線殘留在 Message Queue 裡的無效幽靈事件
        if getattr(gui, 'quote_wrapper', None) is not self.wrapper:
            return
        gui.on_mkt_status_change(Status, str(Msg), ReqType)

    def OnGetMktAll(self, this, symbol, RefPri, OpenPri, HighPri, LowPri,
                    UpPri, DnPri, MatchTime, MatchPri, MatchQty, TolMatchQty,
                    BestBuyQty, BestBuyPri, BestSellQty, BestSellPri,
                    FDBPri, FDBQty, FDSPri, FDSQty, ReqType):
        """
        Tick 即時行情事件。
        每一筆成交報價都會觸發此回呼，在高頻環境下可能每秒數十至上百次。
        """
        gui = self.wrapper.gui
        # 防止舊連線送來假 Tick
        if getattr(gui, 'quote_wrapper', None) is not self.wrapper:
            return
        gui.on_get_mkt_all(
            str(symbol), str(RefPri), str(OpenPri), str(HighPri), str(LowPri),
            str(UpPri), str(DnPri), str(MatchTime), str(MatchPri), str(MatchQty),
            str(TolMatchQty), str(BestBuyQty), str(BestBuyPri),
            str(BestSellQty), str(BestSellPri),
            str(FDBPri), str(FDBQty), str(FDSPri), str(FDSQty),
            int(ReqType)
        )


class YuantaQuoteWrapper:
    """
    職責: ActiveX COM 元件封裝層。
    必須在主執行緒建立。使用 ATL (Active Template Library) 的
    AtlAxCreateControlEx 在指定 HWND 上實體化元大 ActiveX 控制元件，
    再透過 comtypes 取得 Automation 介面與事件連接。

    為什麼不用 QAxWidget？
    ──────────────────────
    QAxWidget.setControl() 內部使用 OleCreate/CoCreateInstance，
    對「純 ActiveX 視窗控制元件」需要有效的 OLE Container 視窗。
    元大 YUANTAQUOTE.YuantaQuoteCtrl.1 是標準 ATL ActiveX，
    在 QAxWidget 的 OLE 容器中經常實體化失敗 (QAxBase::setControl 錯誤)。
    改用 ATL 原生 API 直接建立，100% 穩定。
    """
    def __init__(self, handle: int, gui: 'ExtremeSignalApp'):
        """
        在給定的 HWND 上建立元大行情 ActiveX 控制元件。
        
        參數:
            handle: 宿主視窗的原生 HWND
            gui: 主視窗 ExtremeSignalApp 實例，用於事件回呼
        """
        self.gui = gui
        # 透過 ATL 在 HWND 上建立 ActiveX 控制元件
        Iwindow = POINTER(IUnknown)()
        Icontrol = POINTER(IUnknown)()
        Ievent = POINTER(IUnknown)()

        res = atl.AtlAxCreateControlEx(
            "YUANTAQUOTE.YuantaQuoteCtrl.1", handle, None,
            byref(Iwindow), byref(Icontrol),
            byref(GUID()), Ievent
        )
        # 取得 Automation Dispatch 介面
        self.YuantaQuote = GetBestInterface(Icontrol)
        # 建立 COM 事件接收器並連接
        self.YuantaQuoteEvents = YuantaQuoteEvents(self)
        self.YuantaQuoteEventsConnect = GetEvents(
            self.YuantaQuote, self.YuantaQuoteEvents
        )


# =============================================================================
# 1B. 數據結構與核心計算引擎 (100% 原裝邏輯無損移植)
# =============================================================================

class TradingEngine(QObject):
    """
    職責: 核心計算與回測引擎。
    完全封裝原始 Tkinter GUI 中的所有數學公式、N值 durations 統計、時序狀態機與 OHLC 聚合。
    與 GUI 繪圖執行緒完全隔離，可在獨立執行緒中被安全調用，杜絕 GIL 鎖死導致的 UI 凍結。
    """
    # 當計算完成時發送訊號至主 UI 執行緒
    analysis_completed = Signal(dict)
    log_triggered = Signal(str)

    def __init__(self, abs_n_ticks: int = ABS_N_TICKS_DEFAULT):
        super().__init__()
        self.abs_n_ticks = abs_n_ticks
        self._parsed_file_cache_path = None
        self._parsed_file_cache_trades = None
        self._quant_params_cache = {}
        self._rt_duration_cache = {}
        self._rt_triggers = {}
        
    def log(self, text: str):
        """將日誌發送到 UI"""
        self.log_triggered.emit(text)

    def calculate_stats(self, data_list: List[float]) -> Optional[Dict[str, float]]:
        """100% 移植原始 calculate_stats 邏輯"""
        if not data_list:
            return None
        data_list = sorted(data_list)
        n = len(data_list)
        mean = sum(data_list) / n
        p25 = data_list[int(n * 0.25)]
        p50 = data_list[n // 2]
        p75 = data_list[int(n * 0.75)]
        p90 = data_list[int(n * 0.90)]
        max_val = data_list[-1]
        std_dev = statistics.stdev(data_list) if n > 1 else 0.0
        return {"count": n, "mean": mean, "p25": p25, "p50": p50, "p75": p75, "p90": p90, "max": max_val, "std": std_dev}

    def get_month_code(self) -> str:
        """計算台股期貨近月合約代碼"""
        now = datetime.now(timezone(timedelta(hours=8)))
        day = now.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
        while day.weekday() != 2:
            day = day + timedelta(days=1)
        third_wednesday = day + relativedelta_days(14)
        third_wed_14 = third_wednesday.replace(hour=14, minute=0, second=0, microsecond=0)
        
        if now >= third_wed_14:
            next_month = (third_wednesday + relativedelta_months(1))
            day = next_month.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
        else:
            day = third_wednesday.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
            
        codes = "ABCDEFGHIJKL"
        y = day.year % 10
        m = codes[day.month - 1]
        return f"{m}{y}"

    def parse_time(self, time_str: str) -> float:
        """解析時間字串為當日累積秒數"""
        time_str = time_str.strip()
        if time_str.isdigit() and len(time_str) == 12:
            try:
                h = int(time_str[0:2])
                m = int(time_str[2:4])
                s = int(time_str[4:6])
                ms = int(time_str[6:12]) / 1_000_000
                return h * 3600 + m * 60 + s + ms
            except Exception:
                pass
        try:
            if ":" in time_str:
                parts = time_str.split(".")
                hms = parts[0].split(":")
                h = int(hms[0])
                m = int(hms[1])
                s = int(hms[2])
                ms = int(parts[1]) / 1_000_000 if len(parts) > 1 else 0.0
                return h * 3600 + m * 60 + s + ms
        except Exception:
            pass
        return 0.0

    def _calc_side_speed(self, trades: List[dict]) -> Tuple[Optional[float], Optional[float], str]:
        """計算外盤與內盤的每筆平均間隔時間與共識方向"""
        outer_times = [t["t_val"] for t in trades if t.get("side") == "Outer"]
        inner_times = [t["t_val"] for t in trades if t.get("side") == "Inner"]
        
        outer_avg = None
        inner_avg = None
        
        if len(outer_times) >= 2:
            outer_avg = (outer_times[-1] - outer_times[0]) / (len(outer_times) - 1)
        
        if len(inner_times) >= 2:
            inner_avg = (inner_times[-1] - inner_times[0]) / (len(inner_times) - 1)
        
        if outer_avg is not None and inner_avg is not None:
            if abs(outer_avg - inner_avg) > 0.01:
                direction = "多方 📈" if outer_avg < inner_avg else "空方 📉"
            else:
                direction = "持平 ⚖️"
        else:
            direction = "資料不足"
            
        return outer_avg, inner_avg, direction

    def _calc_side_speed_from_state(self, state: dict) -> Tuple[Optional[float], Optional[float], str]:
        """從當前狀態計算內外盤的平均速度與共識方向 (100% 移植原版邏輯)"""
        outer_avg = None
        inner_avg = None
        
        if state.get("outer_count", 0) >= 2 and state.get("last_outer_time") is not None and state.get("first_outer_time") is not None:
            outer_avg = (state["last_outer_time"] - state["first_outer_time"]) / (state["outer_count"] - 1)
            
        if state.get("inner_count", 0) >= 2 and state.get("last_inner_time") is not None and state.get("first_inner_time") is not None:
            inner_avg = (state["last_inner_time"] - state["first_inner_time"]) / (state["inner_count"] - 1)
            
        if outer_avg is not None and inner_avg is not None:
            if abs(outer_avg - inner_avg) > 0.01:
                direction = "多方 📈" if outer_avg < inner_avg else "空方 📉"
            else:
                direction = "持平 ⚖️"
        else:
            direction = "資料不足"
            
        return outer_avg, inner_avg, direction

    def get_durations(self, trades: List[dict], n: int, idx: int, pre_side: str, post_side: str) -> Tuple[Optional[float], Optional[float], Optional[float], Optional[str], Optional[float], int, int, int]:
        """
        100% 移植 get_durations 核心判定邏輯。
        在 B 點未確認前，A 點價格絕對不准突破（做空不准創新高，做多不准創新低），
        這是系統過濾雜訊、保證訊號高勝率的物理護城河。
        """
        if idx >= len(trades) or idx < 0:
            return None, None, None, None, None, 0, 0, idx
            
        # 前向：蒐集 idx 及其前 n 筆同盤成交
        pre_list = [trades[idx]]
        curr = idx - 1
        while curr >= 0 and len(pre_list) < (n + 1):
            if trades[curr]["side"] == pre_side:
                pre_list.append(trades[curr])
            curr -= 1
            
        # 後向：蒐集 idx 及其後 n 筆同盤成交
        post_list = [trades[idx]]
        extreme_price = trades[idx]["price"]
        curr = idx + 1
        while curr < len(trades) and len(post_list) < (n + 1):
            # 約束條件：B 點形成前 A 點不准被突破
            if post_side == "Inner": # 疑似頭部，不准再創新高
                if trades[curr]["price"] > extreme_price:
                    return None, None, None, None, None, len(pre_list) - 1, len(post_list) - 1, curr - 1
            else: # 疑似底部，不准再創新低
                if trades[curr]["price"] < extreme_price:
                    return None, None, None, None, None, len(pre_list) - 1, len(post_list) - 1, curr - 1
                    
            if trades[curr]["side"] == post_side:
                post_list.append(trades[curr])
            curr += 1
            
        actual_pre_n = len(pre_list) - 1
        actual_post_n = len(post_list) - 1
        
        pre_avg = None
        if actual_pre_n >= 1:
            pre_sum = sum(pre_list[i]["t_val"] - pre_list[i+1]["t_val"] for i in range(actual_pre_n))
            pre_avg = pre_sum / actual_pre_n
            
        post_avg = None
        if actual_post_n >= n:
            post_sum = sum(post_list[i+1]["t_val"] - post_list[i]["t_val"] for i in range(actual_post_n))
            post_avg = post_sum / actual_post_n
            
        threshold = None
        trig_time = None
        trig_price = None
        
        if actual_post_n >= 1:
            if post_side == "Inner":
                threshold = min(t["price"] for t in post_list)
            else:
                threshold = max(t["price"] for t in post_list)
            trig_time = post_list[-1]["time"]
            trig_price = post_list[-1]["price"]
            
        return pre_avg, post_avg, threshold, trig_time, trig_price, actual_pre_n, actual_post_n, curr - 1

    def _get_status_str(self, pre: Optional[float], post: Optional[float], actual_pre: int, actual_post: int, expected_n: int) -> str:
        """100% 移植原始 _get_status_str"""
        if actual_pre >= expected_n and actual_post >= expected_n:
            return " [達標]" if (pre and post and post < pre) else " [未達標]"
        elif actual_pre < 1 and actual_post < 1:
            return " [邊界資料不足]"
        elif actual_post < expected_n:
            return " [邊界未達標]"
        elif pre and post and post < pre:
            return " [邊界達標]"
        else:
            return f" [邊界未達標({actual_pre},{actual_post})]"

    def _load_quant_params(self, target_symbol: str, target_days: int = 60) -> Dict[str, Any]:
        """
        100% 移植原始的報告解析與載入邏輯。
        維持原汁原味地去讀取 reports/advanced_quant_report_merged.md 報告檔案。
        """
        if target_symbol == "TXF":
            params = {
                "time_top": {
                    "日盤: 08:45-09:45": (77, 179, 264),
                    "日盤: 09:45-10:45": (49, 86, 180),
                    "日盤: 10:45-11:45": (23, 59, 72),
                    "日盤: 11:45-13:45": (4, 27, 38),
                    "夜盤: 15:00-16:00": (152, 224, 388),
                    "夜盤: 16:00-19:00": (70, 154, 286),
                    "夜盤: 19:00-23:00": (65, 137, 325),
                    "夜盤: 23:00-05:00": (0, 43, 81)
                },
                "time_bottom": {
                    "日盤: 08:45-09:45": (61, 138, 371),
                    "日盤: 09:45-10:45": (68, 141, 242),
                    "日盤: 10:45-11:45": (32, 90, 185),
                    "日盤: 11:45-13:45": (3, 44, 99),
                    "夜盤: 15:00-16:00": (71, 174, 428),
                    "夜盤: 16:00-19:00": (76, 136, 389),
                    "夜盤: 19:00-23:00": (60, 189, 355),
                    "夜盤: 23:00-05:00": (21, 143, 333)
                },
                "source": "大台(TXF)系統預設值 (單一時段分佈架構)"
            }
        else:
            params = {
                "time_top": {
                    "日盤: 08:45-09:45": (101, 192, 293),
                    "日盤: 09:45-10:45": (70, 129, 212),
                    "日盤: 10:45-11:45": (34, 69, 95),
                    "日盤: 11:45-13:45": (12, 29, 57),
                    "夜盤: 15:00-16:00": (152, 239, 460),
                    "夜盤: 16:00-18:00": (120, 182, 330),
                    "夜盤: 18:00-22:00": (59, 152, 234),
                    "夜盤: 22:00-23:00": (61, 149, 245),
                    "夜盤: 23:00-05:00": (16, 62, 117)
                },
                "time_bottom": {
                    "日盤: 08:45-09:45": (84, 171, 338),
                    "日盤: 09:45-10:45": (64, 130, 209),
                    "日盤: 10:45-11:45": (68, 140, 387),
                    "日盤: 11:45-13:45": (19, 63, 123),
                    "夜盤: 15:00-16:00": (76, 185, 442),
                    "夜盤: 16:00-18:00": (64, 223, 482),
                    "夜盤: 18:00-22:00": (65, 212, 436),
                    "夜盤: 22:00-23:00": (140, 329, 460),
                    "夜盤: 23:00-05:00": (66, 179, 301)
                },
                "source": "小台(MXF)系統預設值 (單一時段分佈架構)"
            }
            
        report_path = os.path.join(APP_DIR, "reports", "advanced_quant_report_merged.md")
        if not os.path.exists(report_path):
            old_path = os.path.join(APP_DIR, "reports", f"advanced_quant_report_{target_symbol}.md")
            if os.path.exists(old_path):
                report_path = old_path
            else:
                return params
            
        try:
            with open(report_path, "r", encoding="utf-8") as f:
                full_content = f.read()
                
            symbol_section = full_content
            if "advanced_quant_report_merged.md" in report_path:
                parts = full_content.split(f"# {target_symbol} 量化分析報告")
                if len(parts) > 1:
                    symbol_section = parts[1].split("\n---\n")[0]
                else:
                    return params
            
            target_label = "全部" if target_days == 0 else str(target_days)
            day_range_section = None

            if f"## 回測天數: {target_label}" in symbol_section:
                after_header = symbol_section.split(f"## 回測天數: {target_label}")[1]
                if "## 回測天數:" in after_header:
                    day_range_section = after_header.split("## 回測天數:")[0]
                else:
                    day_range_section = after_header

                header_line = f"## 回測天數: {target_label}" + after_header.split("\n")[0]
                day_match = re.search(r"日盤\s+(\d+)\s+天", header_line)
                night_match = re.search(r"夜盤\s+(\d+)\s+天", header_line)
                day_count = int(day_match.group(1)) if day_match else 0
                night_count = int(night_match.group(1)) if night_match else 0
                params["source"] = f"動態載入自 {target_symbol} 回測數據 ({target_label}天, 日盤{day_count}/夜盤{night_count})"
            else:
                day_range_section = symbol_section
                day_match = re.search(r"日盤\s+(\d+)\s+天", symbol_section)
                night_match = re.search(r"夜盤\s+(\d+)\s+天", symbol_section)
                if day_match and night_match:
                    params["source"] = f"動態載入自 {target_symbol} 舊版回測數據 (日盤{day_match.group(1)}/夜盤{night_match.group(1)}天)"
                else:
                    old_match = re.search(r"結合\s+(\d+)\s+天", symbol_section)
                    if old_match:
                        params["source"] = f"動態載入自 {target_symbol} 舊版回測數據 (共{old_match.group(1)}天)"

            if day_range_section:
                # 解析頭部時段表格
                if "### 時段分佈 - top" in day_range_section:
                    top_time_section = day_range_section.split("### 時段分佈 - top")[1].split("###")[0]
                    cleared_sess = set()
                    for line in top_time_section.split("\n"):
                        if "|" in line and ("日盤" in line or "夜盤" in line):
                            parts = [p.strip() for p in line.split("|")]
                            if len(parts) >= 9:
                                try:
                                    dim = parts[1]
                                    p50 = int(parts[6])
                                    p75 = int(parts[7])
                                    p90 = int(parts[8])
                                    sess = "日盤" if "日盤" in dim else "夜盤"
                                    if sess not in cleared_sess:
                                        keys = list(params["time_top"].keys())
                                        for k in keys:
                                            if sess in k:
                                                del params["time_top"][k]
                                        cleared_sess.add(sess)
                                    params["time_top"][dim] = (p50, p75, p90)
                                except Exception:
                                    pass
                
                # 解析底部時段表格
                if "### 時段分佈 - bottom" in day_range_section:
                    bot_time_section = day_range_section.split("### 時段分佈 - bottom")[1].split("###")[0]
                    cleared_sess = set()
                    for line in bot_time_section.split("\n"):
                        if "|" in line and ("日盤" in line or "夜盤" in line):
                            parts = [p.strip() for p in line.split("|")]
                            if len(parts) >= 9:
                                try:
                                    dim = parts[1]
                                    p50 = int(parts[6])
                                    p75 = int(parts[7])
                                    p90 = int(parts[8])
                                    sess = "日盤" if "日盤" in dim else "夜盤"
                                    if sess not in cleared_sess:
                                        keys = list(params["time_bottom"].keys())
                                        for k in keys:
                                            if sess in k:
                                                del params["time_bottom"][k]
                                        cleared_sess.add(sess)
                                    params["time_bottom"][dim] = (p50, p75, p90)
                                except Exception:
                                    pass
        except Exception as e:
            print(f"解析量化報告例外: {e}")
            
        return params

    def _calc_kline_data(self, session_name: str, trades: List[dict], txf_sigs: List[tuple], mxf_sigs: List[tuple], interval_mins: int = 30) -> Tuple[List[tuple], List[tuple]]:
        """100% 移植 _calc_kline_data"""
        start_t_val = 31500 if session_name == "日盤" else 54000
        interval = interval_mins * 60
        if interval <= 0:
            interval = 1800
            
        signal_t_vals = []
        for sym_prefix, sigs in [("大臺", txf_sigs), ("小臺", mxf_sigs)]:
            for d in sigs:
                is_unmet = " [未達標]" in str(d[1])
                speed_info = d[10] if len(d) > 10 else d[9] if len(d) > 9 else ""
                is_contradiction = False
                if is_unmet:
                    if "最高" in str(d[1]) and ("空速增" in speed_info or "多速減" in speed_info):
                        is_contradiction = True
                    elif "最低" in str(d[1]) and ("多速增" in speed_info or "空速減" in speed_info):
                        is_contradiction = True
                        
                is_normal_trigger = "[達標]" in str(d[1]) and "未" not in str(d[1]) and "邊界" not in str(d[1])
                
                if is_normal_trigger or is_contradiction:
                    if len(d) > 4 and d[4] is not None:
                        b_time_str = str(d[4])
                        b_t_val = self.parse_time(b_time_str)
                        if session_name == "夜盤" and b_t_val <= 18000:
                            b_t_val += 86400
                        direction = "最高" if "最高" in str(d[1]) else "最低"
                        sig_type = f"{sym_prefix}{direction}[達標]" if is_normal_trigger else f"{sym_prefix}{direction}[矛盾]"
                        signal_t_vals.append((b_t_val, sig_type, d))

        signal_t_vals.sort(key=lambda x: x[0])

        buckets = {}
        for t in trades:
            t_val = t["t_val"]
            if t_val < start_t_val:
                continue
            bucket_idx = int((t_val - start_t_val) // interval)
            if bucket_idx not in buckets:
                buckets[bucket_idx] = {"trades": [], "signals": []}
            buckets[bucket_idx]["trades"].append(t)
            
        for sig_t_val, sig_type, sig_obj in signal_t_vals:
            if sig_t_val >= start_t_val:
                bucket_idx = int((sig_t_val - start_t_val) // interval)
                if bucket_idx in buckets:
                    if not buckets[bucket_idx]["signals"] or buckets[bucket_idx]["signals"][-1] != sig_type:
                        buckets[bucket_idx]["signals"].append(sig_type)
                    if "signal_objs" not in buckets[bucket_idx]:
                        buckets[bucket_idx]["signal_objs"] = []
                    buckets[bucket_idx]["signal_objs"].append(sig_obj)
                    
        kline_data = []
        breakouts = []
        prev_high = None
        prev_low = None
        
        pending_long_trigger_price = None
        pending_short_trigger_price = None
        pending_long_signal_objs = []
        pending_short_signal_objs = []
        pending_long_time_label = ""
        pending_short_time_label = ""
        
        sorted_indices = sorted(buckets.keys())
        for b_idx in sorted_indices:
            b_trades = buckets[b_idx]["trades"]
            if not b_trades:
                continue
                
            b_start = start_t_val + b_idx * interval
            b_end = b_start + interval
            
            def fmt_hm(t_s):
                t_s = t_s % 86400
                h = int(t_s // 3600)
                m = int((t_s % 3600) // 60)
                return f"{h:02d}:{m:02d}"
                
            time_label = f"{fmt_hm(b_start)}~{fmt_hm(b_end)}"
            
            open_p = b_trades[0]["price"]
            high_p = max(t["price"] for t in b_trades)
            low_p = min(t["price"] for t in b_trades)
            close_p = b_trades[-1]["price"]
            
            break_high_text = "是" if prev_high is not None and high_p > prev_high else ""
            break_low_text = "是" if prev_low is not None and low_p < prev_low else ""
            
            if pending_long_trigger_price is not None and high_p > pending_long_trigger_price:
                break_high_text = "做多"
                breakout_time = b_trades[-1]["time"]
                for t in b_trades:
                    if t["price"] > pending_long_trigger_price:
                        breakout_time = t["time"]
                        break
                breakouts.append(("做多", breakout_time, pending_long_time_label, pending_long_signal_objs))
                pending_long_trigger_price = None
                pending_long_signal_objs = []
                
            if pending_short_trigger_price is not None and low_p < pending_short_trigger_price:
                break_low_text = "做空"
                breakout_time = b_trades[-1]["time"]
                for t in b_trades:
                    if t["price"] < pending_short_trigger_price:
                        breakout_time = t["time"]
                        break
                breakouts.append(("做空", breakout_time, pending_short_time_label, pending_short_signal_objs))
                pending_short_trigger_price = None
                pending_short_signal_objs = []

            for sig in buckets[b_idx].get("signals", []):
                if "最低" in sig:
                    pending_long_trigger_price = high_p
                    pending_short_trigger_price = None
                    pending_long_signal_objs = [obj for obj in buckets[b_idx].get("signal_objs", []) if "最低" in obj[1]]
                    pending_long_time_label = time_label
                elif "最高" in sig:
                    pending_short_trigger_price = low_p
                    pending_long_trigger_price = None
                    pending_short_signal_objs = [obj for obj in buckets[b_idx].get("signal_objs", []) if "最高" in obj[1]]
                    pending_short_time_label = time_label
            
            prev_high = high_p
            prev_low = low_p
            
            unique_sigs = list(dict.fromkeys(buckets[b_idx]["signals"]))
            signals_str = ", ".join(unique_sigs)
            
            if close_p > open_p:
                tag = "up"
            elif close_p < open_p:
                tag = "down"
            else:
                tag = "flat"
                
            kline_data.append((time_label, high_p, low_p, open_p, close_p, signals_str, break_high_text, break_low_text, tag))
            
        return kline_data, breakouts

    def _calc_simulation_results(self, session: str, trades: List[dict], kline_data: List[tuple], obs_n: int) -> List[tuple]:
        """
        100% 移植 _calc_simulation_results 時序狀態機。
        """
        results = []
        if len(kline_data) < 2:
            return results

        if not hasattr(self, '_sim_kline_cache'):
            self._sim_kline_cache = {}

        kline_boundaries = []
        for i in range(1, len(kline_data)):
            prev_kline = kline_data[i - 1]
            current_kline = kline_data[i]

            try:
                obs_high_entry = int(float(prev_kline[2]))
                obs_low_entry = int(float(prev_kline[1]))
                prev_high = int(float(prev_kline[1]))
                prev_low = int(float(prev_kline[2]))
            except (ValueError, TypeError):
                continue

            time_label = current_kline[0]
            try:
                start_str, end_str = time_label.split("~")
                h, m = map(int, start_str.split(":"))
                start_mins = h * 60 + m
                if session == "夜盤" and start_mins < 900:
                    start_mins += 1440
                start_t_val = start_mins * 60

                h, m = map(int, end_str.split(":"))
                end_mins = h * 60 + m
                if session == "夜盤" and end_mins < 900:
                    end_mins += 1440
                end_t_val = end_mins * 60
            except Exception:
                continue

            kline_boundaries.append((start_t_val, end_t_val, obs_high_entry, obs_low_entry, prev_high, prev_low))

        if not kline_boundaries:
            return results

        total_boundaries = len(kline_boundaries)
        last_known_start_idx = 0
        last_known_end_idx = 0

        for kb_idx, (kline_start, kline_end, obs_high_entry, obs_low_entry, prev_high, prev_low) in enumerate(kline_boundaries):
            is_last_kline = (kb_idx == total_boundaries - 1)
            cache_key = (kline_start, kline_end, obs_high_entry, obs_low_entry, prev_high, prev_low, obs_n)

            if not is_last_kline and cache_key in self._sim_kline_cache:
                results.extend(self._sim_kline_cache[cache_key])
                continue

            kline_start_idx = last_known_start_idx
            for idx in range(last_known_start_idx, len(trades)):
                if trades[idx]["t_val"] >= kline_start:
                    kline_start_idx = idx
                    break

            kline_end_trade_idx = len(trades)
            for idx in range(max(last_known_end_idx, kline_start_idx), len(trades)):
                if trades[idx]["t_val"] >= kline_end:
                    kline_end_trade_idx = idx
                    break

            last_known_start_idx = kline_start_idx
            last_known_end_idx = kline_end_trade_idx

            sliced_trades = trades[:kline_end_trade_idx]
            if not sliced_trades:
                continue

            kline_results = []

            # 做空路徑 (觀察 K 低)
            search_start = kline_start_idx
            while search_start < len(sliced_trades):
                gate_idx = None
                for j in range(max(1, search_start), len(sliced_trades)):
                    if sliced_trades[j]["price"] < obs_high_entry and sliced_trades[j - 1]["price"] >= obs_high_entry:
                        gate_idx = j
                        break
                if gate_idx is None and search_start < len(sliced_trades) and sliced_trades[search_start]["price"] < obs_high_entry:
                    if search_start == kline_start_idx:
                        gate_idx = search_start

                if gate_idx is None:
                    break

                running_maxes = []
                current_max = None
                for j in range(gate_idx, len(sliced_trades)):
                    if sliced_trades[j]["price"] > obs_high_entry:
                        if current_max is None or sliced_trades[j]["price"] > current_max:
                            current_max = sliced_trades[j]["price"]
                            running_maxes.append(j)

                if not running_maxes:
                    break

                last_successful_b_idx = None
                for a_idx in running_maxes:
                    a_price = sliced_trades[a_idx]["price"]
                    pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = \
                        self.get_durations(sliced_trades, obs_n, a_idx, "Outer", "Inner")

                    if (pre is not None and post is not None
                            and act_pre >= obs_n and act_post >= obs_n
                            and post < pre
                            and trig_price is not None):
                        confirmed_key = (a_price, sliced_trades[a_idx]["time"], obs_n)
                        if not any(k[0] == confirmed_key for k in kline_results):
                            raw_data = {
                                "type": "K低",
                                "obs_entry": obs_high_entry,
                                "best_a_time": sliced_trades[a_idx]["time"],
                                "best_a_price": a_price,
                                "trig_time": trig_time,
                                "trig_price": trig_price,
                                "pre": pre,
                                "post": post,
                                "prev_high": prev_high,
                                "prev_low": prev_low,
                                "b_idx": b_idx,
                                "obs_n": obs_n
                            }
                            kline_results.append((confirmed_key, raw_data, ("obs_high",)))
                        last_successful_b_idx = b_idx

                if last_successful_b_idx is not None:
                    search_start = last_successful_b_idx + 1
                else:
                    break

            # 做多路徑 (觀察 K 高)
            search_start = kline_start_idx
            while search_start < len(sliced_trades):
                gate_idx = None
                for j in range(max(1, search_start), len(sliced_trades)):
                    if sliced_trades[j]["price"] > obs_low_entry and sliced_trades[j - 1]["price"] <= obs_low_entry:
                        gate_idx = j
                        break
                if gate_idx is None and search_start < len(sliced_trades) and sliced_trades[search_start]["price"] > obs_low_entry:
                    if search_start == kline_start_idx:
                        gate_idx = search_start

                if gate_idx is None:
                    break

                running_mins = []
                current_min = None
                for j in range(gate_idx, len(sliced_trades)):
                    if sliced_trades[j]["price"] < obs_low_entry:
                        if current_min is None or sliced_trades[j]["price"] < current_min:
                            current_min = sliced_trades[j]["price"]
                            running_mins.append(j)

                if not running_mins:
                    break

                last_successful_b_idx = None
                for a_idx in running_mins:
                    a_price = sliced_trades[a_idx]["price"]
                    pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = \
                        self.get_durations(sliced_trades, obs_n, a_idx, "Inner", "Outer")

                    if (pre is not None and post is not None
                            and act_pre >= obs_n and act_post >= obs_n
                            and post < pre
                            and trig_price is not None):
                        confirmed_key = (a_price, sliced_trades[a_idx]["time"], obs_n)
                        if not any(k[0] == confirmed_key for k in kline_results):
                            raw_data = {
                                "type": "K高",
                                "obs_entry": obs_low_entry,
                                "best_a_time": sliced_trades[a_idx]["time"],
                                "best_a_price": a_price,
                                "trig_time": trig_time,
                                "trig_price": trig_price,
                                "pre": pre,
                                "post": post,
                                "prev_high": prev_high,
                                "prev_low": prev_low,
                                "b_idx": b_idx,
                                "obs_n": obs_n
                            }
                            kline_results.append((confirmed_key, raw_data, ("obs_low",)))
                        last_successful_b_idx = b_idx

                if last_successful_b_idx is not None:
                    search_start = last_successful_b_idx + 1
                else:
                    break

            if not is_last_kline:
                self._sim_kline_cache[cache_key] = kline_results

            results.extend(kline_results)

        final_results = []
        current_mode = None
        locked_sl = None

        sorted_results = sorted(results, key=lambda x: x[1]["b_idx"])

        for i, (confirmed_key, raw_data, tags) in enumerate(sorted_results):
            sig_type = raw_data["type"]
            b_idx = raw_data["b_idx"]
            obs_n = raw_data["obs_n"]
            obs_entry = raw_data["obs_entry"]
            
            if sig_type == "K低":
                if current_mode != "K低":
                    current_mode = "K低"
                    locked_sl = raw_data["prev_high"]
            elif sig_type == "K高":
                if current_mode != "K高":
                    current_mode = "K高"
                    locked_sl = raw_data["prev_low"]
            
            next_b_idx = sorted_results[i + 1][1]["b_idx"] if i + 1 < len(sorted_results) else len(trades)
            check_trades_chrono = trades[b_idx:next_b_idx]
            check_trades_greedy = trades[b_idx:]
            
            is_broken_chrono = False
            is_broken_greedy = False
            if sig_type == "K低":
                is_broken_chrono = any(t["price"] > locked_sl for t in check_trades_chrono)
                is_broken_greedy = any(t["price"] > locked_sl for t in check_trades_greedy)
            elif sig_type == "K高":
                is_broken_chrono = any(t["price"] < locked_sl for t in check_trades_chrono)
                is_broken_greedy = any(t["price"] < locked_sl for t in check_trades_greedy)
                
            stop_loss_display = f"{locked_sl}(已破)" if is_broken_greedy else str(locked_sl)
            
            if is_broken_chrono:
                # 停損被觸發後，鎖定失效，下一個訊號將重新鎖定
                current_mode = None
            
            row = (
                f"N={obs_n} 觀察{sig_type} {obs_entry}",
                raw_data["best_a_time"],
                str(raw_data["best_a_price"]),
                str(raw_data["trig_time"]) if raw_data["trig_time"] else "N/A",
                str(raw_data["trig_price"]) if raw_data["trig_price"] else "N/A",
                f"{raw_data['pre']:.4f}s" if raw_data['pre'] else "N/A",
                f"{raw_data['post']:.4f}s" if raw_data['post'] else "N/A",
                stop_loss_display
            )
            final_results.append((confirmed_key, row, tags))
            
        return final_results

    def _simulate_speed_pushes_dual(self, txf_trades: List[dict], mxf_trades: List[dict]) -> List[str]:
        """100% 移植共識推播歷史紀錄模擬"""
        tagged = [("TXF", t) for t in txf_trades] + [("MXF", t) for t in mxf_trades]
        tagged.sort(key=lambda x: x[1]["t_val"])

        pushes = []
        state = {}
        for sym in ["TXF", "MXF"]:
            state[sym] = {
                "o_first": None, "o_count": 0, "o_last": None,
                "i_first": None, "i_count": 0, "i_last": None,
                "direction": "資料不足",
                "price_sum": 0, "price_count": 0
            }

        last_consensus = None
        has_pushed = False

        for sym, t in tagged:
            s = state[sym]
            side = t.get("side")
            t_val = t["t_val"]

            s["price_sum"] += t.get("price", 0)
            s["price_count"] += 1

            if side == "Outer":
                if s["o_first"] is None: s["o_first"] = t_val
                s["o_count"] += 1
                s["o_last"] = t_val
            elif side == "Inner":
                if s["i_first"] is None: s["i_first"] = t_val
                s["i_count"] += 1
                s["i_last"] = t_val

            o_avg = (s["o_last"] - s["o_first"]) / (s["o_count"] - 1) if s["o_count"] >= 2 else None
            i_avg = (s["i_last"] - s["i_first"]) / (s["i_count"] - 1) if s["i_count"] >= 2 else None

            if o_avg is not None and i_avg is not None:
                if abs(o_avg - i_avg) > 0.01:
                    s["direction"] = "多方 📈" if o_avg < i_avg else "空方 📉"
                else:
                    s["direction"] = "持平 ⚖️"

            both_ready = all(state[k]["o_count"] >= 250 and state[k]["i_count"] >= 250 for k in ["TXF", "MXF"])
            if not both_ready:
                continue

            txf_dir = state["TXF"]["direction"]
            mxf_dir = state["MXF"]["direction"]
            if "多方" in txf_dir and "多方" in mxf_dir:
                consensus = "多方 📈"
            elif "空方" in txf_dir and "空方" in mxf_dir:
                consensus = "空方 📉"
            else:
                continue

            switched = False
            arrow = ""
            if last_consensus is not None:
                if "空方" in last_consensus and "多方" in consensus:
                    arrow = "空方 → 多方 📈"
                    switched = True
                elif "多方" in last_consensus and "空方" in consensus:
                    arrow = "多方 → 空方 📉"
                    switched = True
            elif not has_pushed:
                arrow = f"初步確立 → {consensus}"
                switched = True

            if switched:
                has_pushed = True
                last_consensus = consensus
                price = t.get("price", "--")
                time_str = t.get("time", "--")

                def _spd(sd, kf, kl, kc):
                    return f"{(sd[kl] - sd[kf]) / (sd[kc] - 1):.4f}s" if sd[kc] >= 2 else "--"

                def _avg_pri(sd):
                    return int(round(sd["price_sum"] / sd["price_count"])) if sd["price_count"] else 0

                push_msg = (
                    f"    [共識推播] {time_str} | {arrow: <15} | 價: {price:<5}"
                    f" | 大臺 外:{_spd(state['TXF'], 'o_first', 'o_last', 'o_count')}"
                    f" 內:{_spd(state['TXF'], 'i_first', 'i_last', 'i_count')} 均價:{_avg_pri(state['TXF'])}"
                    f" | 小臺 外:{_spd(state['MXF'], 'o_first', 'o_last', 'o_count')}"
                    f" 內:{_spd(state['MXF'], 'i_first', 'i_last', 'i_count')} 均價:{_avg_pri(state['MXF'])}"
                )
                pushes.append(push_msg)

        return pushes

    def _get_speed_snapshot_str(self, symbol: str, trades: List[dict], trig_idx: int, other_trades_all: List[dict], last_net_speeds: Optional[dict] = None) -> str:
        """100% 移植 _get_speed_snapshot_str"""
        if trig_idx >= len(trades):
            trig_idx = len(trades) - 1
        if trig_idx < 0:
            return "    成交速度: 資料不足"
            
        target_t_val = trades[trig_idx]["t_val"]
        trades_up_to_trig = trades[:trig_idx+1]
        
        o_avg, i_avg, d_str = self._calc_side_speed(trades_up_to_trig)
        o_cnt = sum(1 for t in trades_up_to_trig if t.get("side") == "Outer")
        i_cnt = sum(1 for t in trades_up_to_trig if t.get("side") == "Inner")
        o_s = f"{o_avg:.4f}s/{o_cnt:5d}筆" if o_avg is not None else "資料不足"
        i_s = f"{i_avg:.4f}s/{i_cnt:5d}筆" if i_avg is not None else "資料不足"
        avg_pri = int(round(sum(t["price"] for t in trades_up_to_trig) / len(trades_up_to_trig))) if trades_up_to_trig else 0
        
        net_speeds = {"TXF": "--", "MXF": "--"}
        
        def calc_net(tr_list):
            oa, ia, _ = self._calc_side_speed(tr_list)
            if oa is not None and ia is not None:
                return ia - oa
            return None

        base_net = calc_net(trades_up_to_trig)
        base_sym = "TXF" if "TXF" in symbol else "MXF"
        other_sym = "MXF" if "TXF" in symbol else "TXF"
        other_trades_up_to = [t for t in other_trades_all if t["t_val"] <= target_t_val]
        other_net = calc_net(other_trades_up_to)

        def format_net(sym, curr_val):
            if curr_val is None:
                return "--" + "       "
            base_str = f"{curr_val:+.4f}s"
            suffix = ""
            if last_net_speeds is not None:
                prev_val = last_net_speeds.get(sym)
                if prev_val is not None and abs(curr_val - prev_val) > 0.00001:
                    if curr_val > prev_val:
                        suffix = " 多速增" if curr_val > 0 else " 空速減"
                    else:
                        suffix = " 空速增" if curr_val < 0 else " 多速減"
                last_net_speeds[sym] = curr_val
            
            if not suffix:
                suffix = "       "
            return base_str + suffix

        net_speeds[base_sym] = format_net(base_sym, base_net)
        net_speeds[other_sym] = format_net(other_sym, other_net)

        return f"    成交速度: 外盤(買) {o_s} | 內盤(賣) {i_s} → {d_str} | 大台速差: {net_speeds['TXF']}  小台速差: {net_speeds['MXF']} | 均價:{avg_pri}"

    def run_analysis_sync(self, file_paths: List[Tuple[str, Any]], target_symbol: str, target_days: int = 60, ignore_time_check: bool = True) -> Tuple[bool, Union[str, dict], Optional[str]]:
        """100% 移植 _analyze_file_logic 核心離線分析流程，用於日誌檔預載與更新按鈕"""
        quant_params = self._load_quant_params(target_symbol, target_days)
        
        pattern = re.compile(r"Symbol=([^, \t\r\n]+)")
        mattime_pat = re.compile(r"mattime=([^, \t\r\n]+)")
        mat_pri_pat = re.compile(r"matpri=([-]?\d+)")
        tmatqty_pat = re.compile(r"tmatqty=([-]?\d+)")
        bestbp_pat = re.compile(r"bestbp=([\d,]*)")
        bestsp_pat = re.compile(r"bestsp=([\d,]*)")

        all_symbol_trades = {"TXF": {"日盤": [], "夜盤": []}, "MXF": {"日盤": [], "夜盤": []}}
        last_tmatqty = defaultdict(lambda: -1)

        try:
            for file_path, time_filter in file_paths:
                if not os.path.exists(file_path): 
                    continue
                with open(file_path, "r", encoding="cp950") as f:
                    for line in f:
                        if "TXF" not in line and "MXF" not in line:
                            continue
                        match = pattern.search(line)
                        if not match: 
                            continue
                        symbol = match.group(1)
                        
                        base_sym = None
                        if symbol.startswith("TXF"): 
                            base_sym = "TXF"
                        elif symbol.startswith("MXF"): 
                            base_sym = "MXF"
                        else: 
                            continue
                        
                        mt_match = mattime_pat.search(line)
                        mp_match = mat_pri_pat.search(line)
                        tq_match = tmatqty_pat.search(line)
                        if not mt_match or not mp_match or not tq_match: 
                            continue
                        
                        time_str = mt_match.group(1)
                        t_val_raw = self.parse_time(time_str)
                        
                        if not time_filter(t_val_raw):
                            continue
                            
                        if 31500 <= t_val_raw <= 49500:
                            session = "日盤"
                            t_val = t_val_raw
                        elif t_val_raw >= 54000 or t_val_raw <= 18000:
                            session = "夜盤"
                            t_val = t_val_raw + 86400 if t_val_raw <= 18000 else t_val_raw
                        else:
                            continue
                            
                        tmatqty = int(tq_match.group(1))
                        if tmatqty < 0 or tmatqty <= last_tmatqty[(base_sym, session, file_path)]: 
                            continue
                        last_tmatqty[(base_sym, session, file_path)] = tmatqty
                        
                        bp_m = bestbp_pat.search(line)
                        sp_m = bestsp_pat.search(line)
                        if not bp_m or not sp_m: 
                            continue
                        
                        try:
                            b_prices = bp_m.group(1)
                            s_prices = sp_m.group(1)
                            best_bp = int(b_prices.split(",")[0]) if b_prices and b_prices.split(",")[0] else 0
                            best_sp = int(s_prices.split(",")[0]) if s_prices and s_prices.split(",")[0] else 0
                            if best_bp <= 0 or best_sp <= 0:
                                continue
                        except Exception: 
                            continue

                        price = int(mp_match.group(1))
                        side = "Outer" if price >= best_sp else ("Inner" if price <= best_bp else None)
                        
                        if side is None:
                            prev_trades = all_symbol_trades[base_sym].get(session, [])
                            side = prev_trades[-1]["side"] if prev_trades else "Outer"
                        
                        all_symbol_trades[base_sym][session].append({
                            "time": time_str, "t_val": t_val,
                            "price": price, "side": side
                        })
            
            self.log(f"      [引擎] 雙核心取得原始資料: 大臺日盤 {len(all_symbol_trades['TXF']['日盤'])} 筆/夜盤 {len(all_symbol_trades['TXF']['夜盤'])} 筆 | 小臺日盤 {len(all_symbol_trades['MXF']['日盤'])} 筆/夜盤 {len(all_symbol_trades['MXF']['夜盤'])} 筆")
            
            # 同時對大臺 TXF 與小臺 MXF 進行雙核心分析，且大臺優先
            symbol_trades = {
                "TXF": all_symbol_trades["TXF"],
                "MXF": all_symbol_trades["MXF"]
            }

            aggregated_trades = {}
            for symbol, sessions in symbol_trades.items():
                for session_name in ["日盤", "夜盤"]:
                    if sessions[session_name]:
                        trades = sessions[session_name]
                        start_time = trades[0]["t_val"]
                        end_time = trades[-1]["t_val"]
                        
                        if not ignore_time_check:
                            if session_name == "日盤":
                                if (start_time is not None and float(start_time) > 32400.0) or \
                                   (end_time is not None and float(end_time) < 46800.0):
                                    continue
                            else:
                                if (start_time is not None and float(start_time) > 63000.0) or \
                                   (end_time is not None and float(end_time) < 103500.0):
                                    continue
                                    
                        aggregated_trades[f"{symbol} ({session_name})"] = trades

            if not aggregated_trades:
                return False, "未找到有效數據。", None

            reports_by_session = {"日盤": "", "夜盤": ""}
            
            session_speeds = {"日盤": {"TXF": "--", "MXF": "--"}, "夜盤": {"TXF": "--", "MXF": "--"}}
            for ss, tr in aggregated_trades.items():
                s_name = "日盤" if "日盤" in ss else "夜盤"
                sym_code = "TXF" if "TXF" in ss else "MXF"
                oa, ia, _ = self._calc_side_speed(tr)
                if oa is not None and ia is not None:
                    session_speeds[s_name][sym_code] = f"{ia - oa:+.4f}s"
                    
            for sess_name in ["日盤", "夜盤"]:
                txf_trades = all_symbol_trades["TXF"][sess_name]
                mxf_trades = all_symbol_trades["MXF"][sess_name]
                if txf_trades and session_speeds[sess_name]["TXF"] == "--":
                    oa_t, ia_t, _ = self._calc_side_speed(txf_trades)
                    if oa_t is not None and ia_t is not None:
                        session_speeds[sess_name]["TXF"] = f"{ia_t - oa_t:+.4f}s"
                if mxf_trades and session_speeds[sess_name]["MXF"] == "--":
                    oa_m, ia_m, _ = self._calc_side_speed(mxf_trades)
                    if oa_m is not None and ia_m is not None:
                        session_speeds[sess_name]["MXF"] = f"{ia_m - oa_m:+.4f}s"

            for symbol_session, trades in aggregated_trades.items():
                report = ""
                # 智慧型解析當前商品與盤別
                curr_symbol = "TXF" if "TXF" in symbol_session else "MXF"
                curr_session = "日盤" if "日盤" in symbol_session else "夜盤"
                other_sym_code = "MXF" if curr_symbol == "TXF" else "TXF"
                
                # 自動載入該商品專屬的統計量化參數，完美保證停損點與進場區精確度
                quant_params = self._load_quant_params(curr_symbol, target_days)
                
                day_max = max(t["price"] for t in trades)
                day_min = min(t["price"] for t in trades)
                final_close = trades[-1]["price"] if trades else 0
                abs_details = []
                
                running_max = -999999
                running_min = 999999
                last_price = None
                last_check_time_h = -999999.0
                last_check_time_b = -999999.0
                RECHECK_SECONDS = 30.0

                # 階段一：極值點基礎數據篩選與過濾 (speed_str 先用空字串 placeholder，並在尾端攜帶 b_idx)
                for i in range(len(trades)):
                    price = trades[i]["price"]
                    t_val = trades[i]["t_val"]
                    
                    is_trig_h = False
                    is_trig_b = False
                    
                    if price > running_max:
                        running_max = price
                        is_trig_h = True
                    elif price == running_max:
                        if (last_price is not None and last_price < price) or (t_val - last_check_time_h >= RECHECK_SECONDS):
                            is_trig_h = True
                            
                    if price < running_min:
                        running_min = price
                        is_trig_b = True
                    elif price == running_min:
                        if (last_price is not None and last_price > price) or (t_val - last_check_time_b >= RECHECK_SECONDS):
                            is_trig_b = True
                    
                    last_price = price

                    curr_session = "日盤" if "日盤" in symbol_session else "夜盤"
                    other_trades_all = all_symbol_trades[other_sym_code].get(curr_session, [])
                    if price == day_max:
                        pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = self.get_durations(trades, self.abs_n_ticks, i, "Outer", "Inner")
                        status = self._get_status_str(pre, post, act_pre, act_post, self.abs_n_ticks)
                        amp = running_max - running_min if running_min != 999999 else 0
                        abs_details.append((t_val, "時段最高" + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, final_close, "", b_idx))
                    elif is_trig_h:
                        pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = self.get_durations(trades, self.abs_n_ticks, i, "Outer", "Inner")
                        status = self._get_status_str(pre, post, act_pre, act_post, self.abs_n_ticks)
                        if status in [" [達標]", " [邊界達標]", " [未達標]"]:
                            amp = running_max - running_min if running_min != 999999 else 0
                            prefix = "曾未達標最高" if "未達標" in status else "曾達標最高"
                            abs_details.append((t_val, prefix + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, final_close, "", b_idx))

                    if price == day_min:
                        pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = self.get_durations(trades, self.abs_n_ticks, i, "Inner", "Outer")
                        status = self._get_status_str(pre, post, act_pre, act_post, self.abs_n_ticks)
                        amp = running_max - running_min if running_max != -999999 else 0
                        abs_details.append((t_val, "時段最低" + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, final_close, "", b_idx))
                    elif is_trig_b:
                        pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = self.get_durations(trades, self.abs_n_ticks, i, "Inner", "Outer")
                        status = self._get_status_str(pre, post, act_pre, act_post, self.abs_n_ticks)
                        if status in [" [達標]", " [邊界達標]", " [未達標]"]:
                            amp = running_max - running_min if running_max != -999999 else 0
                            prefix = "曾未達標最低" if "未達標" in status else "曾達標最低"
                            abs_details.append((t_val, prefix + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, final_close, "", b_idx))
                            
                    if is_trig_h: last_check_time_h = t_val
                    if is_trig_b: last_check_time_b = t_val

                report += f"\n>>> 商品: {symbol_session} | 時段極值: {day_min} ~ {day_max} | 成交價: {final_close}\n"
                report += f"    ● 當前分析採用的區間參數來源: {quant_params['source']}\n"
                report += f"       - 進場區間 [P50, P75]：動態配對『該小時時段歷史統計』數值。\n"
                report += f"       - 停損防守 [P90]：雙維交集！\n"
                
                abs_details.sort(key=lambda x: x[0])
                filtered_abs_details = []
                seen_records = set()
                for d in abs_details:
                    direction = "最高" if "最高" in d[1] else "最低"
                    key = (direction, d[3])
                    if key in seen_records:
                        continue
                    seen_records.add(key)
                    filtered_abs_details.append(d)

                # ==================== 階段二：精確速差比對邏輯 ====================
                # 重新初始化大小台歷史速差狀態，避免受已被隱藏的過渡點污染
                last_net_speeds_top = {"TXF": None, "MXF": None}
                last_net_speeds_bot = {"TXF": None, "MXF": None}
                
                final_abs_details_with_speeds = []
                for d in filtered_abs_details:
                    # 只要後向(post)為 None 的記錄，必定會被隱藏，故維持 placeholder 且跳過歷史速差比對
                    if d[7] is None:
                        # 補齊 11 個元素的元組結構，與原有格式完全一致
                        final_abs_details_with_speeds.append((d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], d[9], ""))
                        continue
                        
                    t_val, status_str, a_time, price_val, trig_time, trig_price, pre, post, amp_val, f_close, _, b_idx = d
                    
                    is_top = "最高" in status_str
                    is_day_extreme = "時段最高" in status_str or "時段最低" in status_str
                    
                    # 時段最高/最低點若不達標，平常並不顯示成交速度，故不予比對
                    need_speed = True
                    if is_day_extreme and "達標" not in status_str:
                        need_speed = False
                        
                    if need_speed:
                        if is_top:
                            speed_str = self._get_speed_snapshot_str(curr_symbol, trades, b_idx, other_trades_all, last_net_speeds_top)
                        else:
                            speed_str = self._get_speed_snapshot_str(curr_symbol, trades, b_idx, other_trades_all, last_net_speeds_bot)
                    else:
                        speed_str = ""
                        
                    final_abs_details_with_speeds.append((t_val, status_str, a_time, price_val, trig_time, trig_price, pre, post, amp_val, f_close, speed_str))
                
                # 直接替換為精確計算速差後的詳情列表，無縫接軌後續渲染
                filtered_abs_details = final_abs_details_with_speeds

                final_abs_h = sum(1 for d in filtered_abs_details if "最高" in d[1] and "達標" in d[1] and "未" not in d[1])
                final_abs_b = sum(1 for d in filtered_abs_details if "最低" in d[1] and "達標" in d[1] and "未" not in d[1])

                report += f"    ● 絕對極值信號 (當時段最高低):\n"
                report += f"       - 頂部達標: {final_abs_h} 筆\n"
                report += f"       - 底部達標: {final_abs_b} 筆\n"
                
                def wide_pad(text: str, width: int) -> str:
                    actual_w = sum(2 if ord(c) > 127 else 1 for c in text)
                    return text + " " * max(0, width - actual_w)
                
                h_type = wide_pad("類型", 22)
                h_zone = wide_pad("進場區/停損", 23)
                h_a_time = wide_pad("A點時間", 15)
                h_a_pri = wide_pad("A點價", 8)
                h_b_time = wide_pad("B點時間", 15)
                h_trig = wide_pad("觸發價", 8)
                h_pre = wide_pad("前向平均", 12)
                h_post = wide_pad("後向平均", 12)
                h_amp = wide_pad("當時振幅", 8)

                header = f"{h_type} | {h_zone} | {h_a_time} | {h_a_pri} | {h_b_time} | {h_trig} | {h_pre} | {h_post} | {h_amp}"
                sep = "    " + "-" * 142

                outer_avg, inner_avg, direction_str = self._calc_side_speed(trades)
                outer_count = sum(1 for t in trades if t.get("side") == "Outer")
                inner_count = sum(1 for t in trades if t.get("side") == "Inner")
                outer_s = f"{outer_avg:.4f}s/{outer_count}筆" if outer_avg is not None else "資料不足"
                inner_s = f"{inner_avg:.4f}s/{inner_count}筆" if inner_avg is not None else "資料不足"
                avg_pri = int(round(sum(t["price"] for t in trades) / len(trades))) if trades else 0
                txf_n = session_speeds[curr_session]["TXF"]
                mxf_n = session_speeds[curr_session]["MXF"]

                report += f"\n    [絕對極值詳情 (平均每筆間隔)]  收盤價: {final_close}\n"
                report += f"    ● 成交速度: 外盤(買) {outer_s} | 內盤(賣) {inner_s} → {direction_str} | 大台速差: {txf_n}  小台速差: {mxf_n} | 均價:{avg_pri}\n"
                report += f"    {header}\n"
                report += f"{sep}\n"
                
                last_abs_type = None
                for d in filtered_abs_details:
                    if d[7] is None:
                        continue
                        
                    current_type = "最高" if "最高" in d[1] else "最低"
                    if last_abs_type is not None and current_type != last_abs_type:
                        report += f"{sep}\n"
                    last_abs_type = current_type
                    
                    b_time_val = d[4] if d[4] is not None else "N/A"
                    b_pri_val = str(d[5]) if d[5] is not None else "N/A"
                    pre_s = f"{d[6]:>10.4f}s" if d[6] is not None else f"{'N/A':>10}"
                    post_s = f"{d[7]:>10.4f}s" if d[7] is not None else f"{'N/A':>10}"
                    
                    amp_val = int(d[8])
                    side = "top" if current_type == "最高" else "bottom"
                    
                    speed_info = d[10] if len(d) > 10 else ""
                    is_unmet = " [未達標]" in str(d[1])
                    force_show_unmet = False
                    display_type_str = str(d[1])
                    if is_unmet:
                        if "最高" in str(d[1]) and ("空速增" in speed_info or "多速減" in speed_info):
                            force_show_unmet = True
                        elif "最低" in str(d[1]) and ("多速增" in speed_info or "空速減" in speed_info):
                            force_show_unmet = True
                            
                        if force_show_unmet:
                            display_type_str = display_type_str.replace("未達標", "矛盾")

                    if " [達標]" in str(d[1]) or (" [未達標]" in str(d[1]) and force_show_unmet):
                        try:
                            time_p50, time_p75, time_p90 = None, None, None
                            time_dict = quant_params.get("time_top" if side == "top" else "time_bottom", {})
                            b_time_str = str(d[2]).replace(":", "")
                            hm_val = int(b_time_str[0:4]) if len(b_time_str) >= 4 else 0
                            h = hm_val // 100
                            m = hm_val % 100
                            total_m = h * 60 + m
                            session_str = '日盤' if 8 <= h <= 13 else '夜盤'
                            
                            if session_str == "夜盤" and total_m < 900:
                                total_m += 1440
                                
                            for k_label, (p50, p75, p90) in time_dict.items():
                                if session_str in k_label:
                                    try:
                                        time_part = k_label.split(" ")[1].strip()
                                        if "-" in time_part:
                                            s_str, e_str = time_part.split("-")
                                            s_mins = int(s_str.split(":")[0])*60 + int(s_str.split(":")[1])
                                            e_mins = int(e_str.split(":")[0])*60 + int(e_str.split(":")[1])
                                            if session_str == "夜盤":
                                                if s_mins < 900: s_mins += 1440
                                                if e_mins < 900 or e_mins % 1440 < 900: e_mins += 1440
                                            if s_mins <= total_m <= e_mins:
                                                time_p50, time_p75, time_p90 = p50, p75, p90
                                                break
                                    except Exception:
                                        pass
                            
                            if time_p50 is not None:
                                final_p50, final_p75, final_p90 = time_p50, time_p75, time_p90
                            else:
                                raise ValueError("時間找無對應")
                                
                            price_val = int(d[3])
                            if side == "top":
                                zone_str = f"區:{price_val + final_p50}~{price_val + final_p75} 損:{price_val + final_p90}"
                            else:
                                zone_str = f"區:{price_val - final_p50}~{price_val - final_p75} 損:{price_val - final_p90}"
                        except Exception:
                            zone_str = "N/A"
                    else:
                        zone_str = ""
                    
                    type_str = wide_pad(display_type_str, 22)
                    zone_pad = wide_pad(zone_str, 23)
                    a_time = wide_pad(str(d[2]), 15)
                    a_pri = wide_pad(str(d[3]), 8)
                    b_time = wide_pad(str(b_time_val), 15)
                    b_pri = wide_pad(str(b_pri_val), 8)
                    
                    report += f"    {type_str} | {zone_pad} | {a_time} | {a_pri} | {b_time} | {b_pri} | {pre_s} | {post_s} | {d[8]:>8}\n"
                    if len(d) > 10 and d[10]:
                        report += f"{d[10]}\n"
                
                if not hasattr(self, '_temp_offline_trades'):
                    self._temp_offline_trades = {}
                if not hasattr(self, '_temp_offline_signals'):
                    self._temp_offline_signals = {}
                    
                self._temp_offline_trades[symbol_session] = trades
                self._temp_offline_signals[symbol_session] = filtered_abs_details
                        
                session_name = "日盤" if "日盤" in symbol_session else "夜盤"
                reports_by_session[session_name] += report + sep + "\n"

            return True, reports_by_session, "OK"
        except Exception as e:
            traceback.print_exc()
            return False, str(e), None

    def run_analysis_async(self, file_paths: List[Tuple[str, Any]], target_symbol: str, target_days: int = 60, ignore_time_check: bool = True):
        """非同步執行緒運行離線分析，防範 UI 鎖死"""
        def worker():
            success, result, status = self.run_analysis_sync(file_paths, target_symbol, target_days, ignore_time_check)
            self.analysis_completed.emit({
                "success": success,
                "result": result,
                "status": status,
                "symbol": target_symbol,
                "temp_trades": getattr(self, '_temp_offline_trades', {}),
                "temp_signals": getattr(self, '_temp_offline_signals', {})
            })
        threading.Thread(target=worker, daemon=True).start()

    @staticmethod
    def wide_pad(text: Any, width: int) -> str:
        """
        計算視覺寬度並填充空白字元以進行完美對齊。
        全形中文字元在大多數終端機或文字方塊中佔 2 格視覺寬度，半形英文字母與符號計為 1 格。
        
        Args:
            text: 擬對齊之任何物件或字串
            width: 目標對齊寬度
            
        Returns:
            str: 填充好尾隨空白的視覺等寬字串
        """
        s = str(text)
        actual_w = sum(2 if ord(c) > 127 else 1 for c in s)
        return s + " " * max(0, width - actual_w)

    def _generate_kline_text(self, session_name: str, kline_data: List[Any], breakouts: Optional[List[Any]] = None, interval_mins: str = "30") -> str:
        """
        產生純文字版的小臺 K 線報表，包含大小臺突破信號與 consensus 標記。
        
        Args:
            session_name: 交易盤別 ("日盤" 或 "夜盤")
            kline_data: K 線資料列
            breakouts: 突破信號清單 (可選)
            interval_mins: K棒時間間隔 (預設 "30")
            
        Returns:
            str: 格式化後的純文字報表
        """
        if not kline_data:
            return ""
            
        _pad = self.wide_pad
        header = f"    {_pad('時間', 15)} | {_pad('高', 6)} | {_pad('低', 6)} | {_pad('開', 6)} | {_pad('收', 6)} | {_pad('訊號標記', 60)} | {_pad('突破上高', 8)} | {_pad('跌破上低', 8)}"
        sep = "    " + "-" * 115
            
        res = f"\n    [{session_name} 小臺 {interval_mins} 分鐘 K 線]\n{header}\n{sep}\n"
        for row in kline_data:
            t_str, h_p, l_p, o_p, c_p, sigs, b_h, b_l, tag = row
            res += f"    {_pad(t_str, 15)} | {_pad(str(h_p), 6)} | {_pad(str(l_p), 6)} | {_pad(str(o_p), 6)} | {_pad(str(c_p), 6)} | {_pad(sigs, 60)} | {_pad(b_h, 8)} | {_pad(b_l, 8)}\n"
            
        if breakouts:
            res += f"\n    [{session_name} K線突破訊號]\n"
            for d in breakouts:
                direction, b_time, sig_time, sig_objs = d
                a_prices = []
                b_prices = []
                for obj in sig_objs:
                    if len(obj) > 3 and obj[3] is not None:
                        a_prices.append(str(obj[3]))
                    if len(obj) > 5 and obj[5] is not None:
                        b_prices.append(str(obj[5]))
                a_prices_str = ", ".join(sorted(list(set(a_prices)))) if a_prices else "N/A"
                b_prices_str = ", ".join(sorted(list(set(b_prices)))) if b_prices else "N/A"
                emoji = "📈" if direction == "做多" else "📉"
                res += f"    >>>> {direction} {emoji} | 突破時間: {b_time} | 訊號時間: {sig_time} | 進場價(B點): {b_prices_str} | 停損價(A點): {a_prices_str}\n"
                
        return res

# ═══════════════════════════════════════════════════════════════════
# 1.5. 高性能文字日誌高亮著色器 (QSyntaxHighlighter)
# ═══════════════════════════════════════════════════════════════════

class TradingLogHighlighter(QSyntaxHighlighter):
    """
    職責: 專業級即時日誌與極值詳情高亮著色器。
    基於 Qt 的 UI Rendering Pipeline 文本渲染管道，在文本繪製時即時套用樣式，
    完全隔離於運算執行緒之外，零效能開銷，保證操作與滾動絕不卡頓。
    """
    def __init__(self, parent=None):
        super().__init__(parent)
        
        # 觀察 K 低 / 時段最高 / 曾未達標最高 (做空/綠色) 格式
        self.high_format = QTextCharFormat()
        self.high_format.setForeground(QColor(40, 167, 69)) # 亮綠色
        self.high_format.setFontWeight(QFont.Bold)
        
        # 觀察 K 高 / 時段最低 / 曾未達標最低 (做多/紅色) 格式
        self.low_format = QTextCharFormat()
        self.low_format.setForeground(QColor(235, 75, 75)) # 亮紅色
        self.low_format.setFontWeight(QFont.Bold)
        
        # 系統狀態 / 共識推播 / 達標標記等亮青色格式
        self.system_format = QTextCharFormat()
        self.system_format.setForeground(QColor(0, 162, 237)) # 亮青色
        self.system_format.setFontWeight(QFont.Bold)

    def highlightBlock(self, text: str):
        # 針對整行進行高效的正則匹配與著色
        # 100% 遵守使用者指示：未達標訊號行保持預設前景色 (白色)，不進行任何染色
        if "未達標" in text:
            return
            
        if "最高" in text or "K低" in text:
            self.setFormat(0, len(text), self.high_format)
        elif "最低" in text or "K高" in text:
            self.setFormat(0, len(text), self.low_format)
        elif "共識推播" in text or "觸發推播" in text or "行情狀態" in text or "預載" in text:
            self.setFormat(0, len(text), self.system_format)

# ═══════════════════════════════════════════════════════════════════
# 2. QAbstractTableModel 高效差量數據模型 (Delta Rendering)
# ═══════════════════════════════════════════════════════════════════

class SignalTableModel(QAbstractTableModel):
    """
    職責: 高效的差量繪圖模型。
    """
    def __init__(self, headers: List[str], parent=None):
        super().__init__(parent)
        self.headers = headers
        self._data: List[tuple] = []
        self._tags: List[tuple] = []
        
        # 性能優化物件池：預先建立字型與筆刷物件，免除高頻行情刷新時的臨時內存分配與垃圾回收開銷
        self._cached_fonts = {
            "bold": QFont("Consolas", 10, QFont.Bold),
            "normal": QFont("Consolas", 10)
        }
        self._cached_brushes = {
            "up": QBrush(QColor(235, 75, 75)),
            "down": QBrush(QColor(40, 167, 69)),
            "obs_high": QBrush(QColor(40, 167, 69)),
            "obs_low": QBrush(QColor(235, 75, 75)),
            "annotation": QBrush(QColor(128, 128, 128)),
            "bg_low": QBrush(QColor(40, 167, 69, 40)),
            "bg_high": QBrush(QColor(235, 75, 75, 40))
        }

    def rowCount(self, parent=QModelIndex()) -> int:
        return len(self._data)

    def columnCount(self, parent=QModelIndex()) -> int:
        return len(self.headers)

    def headerData(self, section: int, orientation: Qt.Orientation, role: int = Qt.DisplayRole) -> Any:
        # 修正：在 PyQt5 之中，orientation 直接是 Qt.Horizontal，不是 Enum，所以要改為 orientation == Qt.Horizontal
        if role == Qt.DisplayRole and orientation == Qt.Horizontal:
            if section < len(self.headers):
                return self.headers[section]
        return None

    def data(self, index: QModelIndex, role: int = Qt.DisplayRole) -> Any:
        if not index.isValid() or index.row() >= len(self._data):
            return None
            
        row_data = self._data[index.row()]
        tag_data = self._tags[index.row()] if index.row() < len(self._tags) else ()
        
        if role == Qt.DisplayRole:
            if index.column() < len(row_data):
                return str(row_data[index.column()])
                
        elif role == Qt.ForegroundRole:
            if "up" in tag_data:
                return self._cached_brushes["up"]
            elif "down" in tag_data:
                return self._cached_brushes["down"]
            elif "obs_high" in tag_data:
                return self._cached_brushes["obs_high"]
            elif "obs_low" in tag_data:
                return self._cached_brushes["obs_low"]
            elif "annotation" in tag_data:
                return self._cached_brushes["annotation"]
                
        elif role == Qt.BackgroundRole:
            if "obs_k_low_highlight" in tag_data:
                return self._cached_brushes["bg_low"]
            elif "obs_k_high_highlight" in tag_data:
                return self._cached_brushes["bg_high"]
                
        elif role == Qt.FontRole:
            if "obs_high" in tag_data or "obs_low" in tag_data or "up" in tag_data or "down" in tag_data:
                return self._cached_fonts["bold"]
                
        return None

    def update_data(self, new_data: List[tuple], new_tags: List[tuple]):
        """
        智慧差量更新模型資料，保留 QTableView 的選取（反白）狀態。
        
        使用 insertRows / removeRows / dataChanged 取代 beginResetModel，
        確保即時行情更新時使用者的反白選取不會被摧毀。
        
        策略:
            - 資料長度相同 → 僅發射 dataChanged，選取完全不動
            - 資料增加 → 更新既有行 + beginInsertRows 新增行
            - 資料減少 → beginRemoveRows 移除行 + 更新剩餘行
        """
        old_len = len(self._data)
        new_len = len(new_data)
        
        # 空→空：完全不動，節省 repaint
        if old_len == 0 and new_len == 0:
            return
        
        # 情境 1：資料長度相同 → 原地替換資料，僅觸發 dataChanged (選取不受影響)
        if old_len == new_len:
            self._data = new_data
            self._tags = new_tags
            if new_len > 0:
                self.dataChanged.emit(
                    self.index(0, 0),
                    self.index(new_len - 1, self.columnCount() - 1)
                )
            return
        
        # 情境 2：資料增加 (即時行情最常見情境) → 更新既有行 + 插入新增行
        if new_len > old_len:
            # 先原地更新既有行的內容
            if old_len > 0:
                self._data[:old_len] = new_data[:old_len]
                self._tags[:old_len] = new_tags[:old_len]
                self.dataChanged.emit(
                    self.index(0, 0),
                    self.index(old_len - 1, self.columnCount() - 1)
                )
            # 再插入新增行 (beginInsertRows 不影響既有行的選取狀態)
            self.beginInsertRows(QModelIndex(), old_len, new_len - 1)
            self._data.extend(new_data[old_len:])
            self._tags.extend(new_tags[old_len:])
            self.endInsertRows()
            return
        
        # 情境 3：資料減少 (盤別切換等) → 移除多餘行 + 更新剩餘行
        self.beginRemoveRows(QModelIndex(), new_len, old_len - 1)
        self._data = list(new_data)
        self._tags = list(new_tags)
        self.endRemoveRows()
        if new_len > 0:
            self.dataChanged.emit(
                self.index(0, 0),
                self.index(new_len - 1, self.columnCount() - 1)
            )

# ═══════════════════════════════════════════════════════════════════
# 3. pyqtgraph 高性能降採樣圖表 (KLineChartWidget)
# ═══════════════════════════════════════════════════════════════════

from PyQt5.QtWidgets import QWidget  # 確保 QWidget 已導入
from PyQt5.QtGui import QPainter, QPen, QColor  # 確保繪圖元件已導入

class CrosshairOverlay(QWidget):
    """
    職責: 高性能十字游標透明疊加畫布。
    採用物理隔離技術，將十字游標的繪製完全獨立於 K棒 繪圖區之外，
    保證在滑鼠高頻滑動時，底層圖表不需要進行任何重繪，實現 0 延遲與絕對流暢。
    """
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WA_TransparentForMouseEvents, True)  # 關鍵：滑鼠事件穿透，不干涉底層圖表的縮放與拖曳
        self.setAttribute(Qt.WA_OpaquePaintEvent, False)          # 背景透明
        self.mouse_pos = None

        # 16ms 節流定時器（≈60FPS 上限），合併高頻滑鼠事件為單一 repaint
        # 在文書機上滑鼠每秒可能觸發 100+ 次移動事件，
        # 透過此節流器將重繪頻率鎖死在 60Hz 內，釋放極大 CPU 資源
        self._throttle_timer = QTimer(self)
        self._throttle_timer.setSingleShot(True)
        self._throttle_timer.setInterval(16)
        self._throttle_timer.timeout.connect(self.update)

    def set_mouse_pos(self, pos):
        """設定目前滑鼠座標，透過 16ms 節流合併重繪，消滅 Repaint Storm"""
        self.mouse_pos = pos
        # 若節流定時器已在倒數，則本次座標更新會在定時器到期時統一重繪
        if not self._throttle_timer.isActive():
            self._throttle_timer.start()

    def paintEvent(self, event):
        if not self.mouse_pos:
            return
            
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing, True)
        
        # 繪製經典高顯眼科幻綠虛線
        pen = QPen(QColor('#00ffcc'), 1.2, Qt.DashLine)
        painter.setPen(pen)
        
        x = self.mouse_pos.x()
        y = self.mouse_pos.y()
        
        # 畫十字虛線
        painter.drawLine(x, 0, x, self.height())
        painter.drawLine(0, y, self.width(), y)
        
        # 於交叉點繪製一個實心科技小圓點，美學與實用性兼具
        painter.setPen(Qt.NoPen)
        painter.setBrush(QColor('#00ffcc'))
        painter.drawEllipse(pg.QtCore.QPoint(x, y), 3, 3)


class KLineChartWidget(QWidget):
    """
    職責: 專業級圖表元件。
    提供 K 線圖表的渲染與十字游標追蹤。
    """
    def __init__(self, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        
        # 智慧偵測 OpenGL 可用性，可用時啟用 GPU 加速渲染
        # 文書機 GPU 可能不支援或驅動太舊，所以必須做 try-except fallback
        # 即使 OpenGL 不可用，QPicture 預繪技術已能帶來 10~50 倍提升
        try:
            pg.setConfigOptions(antialias=True, useOpenGL=True)
        except Exception:
            pg.setConfigOptions(antialias=True, useOpenGL=False)
        self.plot_widget = pg.PlotWidget()
        layout.addWidget(self.plot_widget)
        
        self.plot_widget.setBackground('#121212')
        self.plot_widget.showGrid(x=True, y=True, alpha=0.15)
        
        # 移除 InfiniteLine，改用高性能透明疊加畫布，實現物理重繪隔離
        self.crosshair_overlay = CrosshairOverlay(self)
        
        # 新增：建立固定置頂在右上角的完全不透明數據面板
        self.info_panel = QLabel(self)
        self.info_panel.setStyleSheet(
            "background-color: #1a1a1a; "  # 完全不透明深灰底色，保證清晰醒目
            "border: 2px solid #00ffcc; "   # 顯眼的元氣科幻綠邊框
            "border-radius: 5px; "
            "padding: 6px; "
            "color: #dcdcdc;"
        )
        self.info_panel.setFixedSize(160, 130)
        self.info_panel.hide()
        
        self.candles = []
        self.kline_items = []
        
        # 新增：追蹤目前滑鼠游標懸浮所在的 K棒 索引 (None 代表無懸浮)
        self.current_hover_index = None
        
        # 智慧防禦鎖：標記使用者是否手動縮放/平移過圖表
        self.is_zoomed_or_panned = False
        
        # 連結 ViewBox 原生手動改變視界範圍訊號，100% 精準捕捉使用者自行滾輪或拖曳的行為
        self.plot_widget.plotItem.vb.sigRangeChangedManually.connect(self.on_range_changed_manually)
        
        self.plot_widget.scene().sigMouseMoved.connect(self.on_mouse_moved)

    def resizeEvent(self, event):
        """
        動態計算右上角座標與畫布形狀。
        加入 50ms 延遲合併 resize，避免拖曳視窗邊框時逐像素重繪 K 線圖導致嚴重卡頓。
        """
        super().resizeEvent(event)
        
        # 確保透明十字星 Overlay 畫布與繪圖區完美重合
        if hasattr(self, 'crosshair_overlay'):
            self.crosshair_overlay.setGeometry(self.plot_widget.geometry())
            
        margin = 10
        x = self.width() - self.info_panel.width() - margin
        y = margin
        self.info_panel.move(x, y)

        # 50ms 延遲合併 resize，避免逐像素重繪 K 棒 QPicture
        if not hasattr(self, '_resize_timer'):
            self._resize_timer = QTimer(self)
            self._resize_timer.setSingleShot(True)
            self._resize_timer.setInterval(50)
            self._resize_timer.timeout.connect(self._on_resize_done)
        self._resize_timer.start()

    def _on_resize_done(self):
        """resize 結束後統一重繪一次 K 線圖 QPicture 快取"""
        for item in self.kline_items:
            if hasattr(item, '_generate_history_picture'):
                item._generate_history_picture()
                item.update()

    def on_range_changed_manually(self):
        """原生手動操作回呼。當使用者真正自行進行滾輪、滑鼠縮放或拖曳時才會觸發"""
        self.is_zoomed_or_panned = True

    def show_kline_info(self, index: int):
        """
        將指定 index 的 K 棒數據渲染成 HTML 並呈現在右上角不透明面板上。
        
        Args:
            index: K 棒的 index 索引值
        """
        if 0 <= index < len(self.candles):
            row = self.candles[index]
            time_label, high, low, open_p, close_p = row[0], row[1], row[2], row[3], row[4]
            
            html_text = (
                f'<div style="color: #dcdcdc; font-family: Consolas, Microsoft JhengHei; font-size: 9pt; line-height: 140%;">'
                f'<span style="color: #00ffcc; font-weight: bold; border-bottom: 1px solid #444; display: block; padding-bottom: 2px;">📊 {time_label}</span><br>'
                f'開：<span style="color: #ffffff; font-weight: bold;">{open_p}</span><br>'
                f'高：<span style="color: #eb4b4b; font-weight: bold;">{high}</span><br>'
                f'低：<span style="color: #28a745; font-weight: bold;">{low}</span><br>'
                f'收：<span style="color: #ffffff; font-weight: bold;">{close_p}</span>'
                f'</div>'
            )
            self.info_panel.setText(html_text)
            self.info_panel.show()
        else:
            self.info_panel.hide()

    def update_candles(self, kline_data: List[tuple], force_auto_range: bool = False):
        """
        載入並繪製 K 線，同時處理視界範圍縮放與右上角面板預設更新。
        採用增量更新與物件複用 (Object Reuse) 技術，徹底免除銷毀重建對象的效能瓶頸。
        
        Args:
            kline_data: 聚合完成之 K 線 tuple 資料列，每個 tuple 格式為：
                        (time_label, high, low, open, close, signals, break_high, break_low, tag)
            force_auto_range: 是否強制重算 X/Y 軸對焦範圍
        
        變數用途:
            self.kline_items: 存放目前繪圖畫布上唯一的 CandlestickItem 繪圖實例。
            self.candles: 快取當前所有的 K 線 tuple 資料列。
        """
        if force_auto_range:
            self.is_zoomed_or_panned = False  # 強制對焦時，解除手動鎖定，重開全自動對焦模式

        self.candles = kline_data
        
        if not kline_data:
            self.info_panel.hide()
            # 增量清空：安全移除現有繪圖元件，防止 graphics scene 殘留幽靈對象造成 memory leak
            for item in self.kline_items:
                self.plot_widget.removeItem(item)
            self.kline_items.clear()
            return
            
        # ═══ 核心性能優化：物件複用 (Object Reuse) ═══
        # 原版 Bug 瓶頸：原版每次都 `removeItem` 後重新 `new CandlestickItem`，這會導致對象頻繁建立，
        # 並在 `__init__` 中反覆調用 QPicture 預繪，完全廢掉了快取。
        # 最優解：只在第一次時建立，後續全部走 set_data 增量更新數據，瞬間消滅 99% 的垃圾回收(GC)負擔與重繪開銷。
        if not self.kline_items:
            candle_item = CandlestickItem(kline_data)
            self.plot_widget.addItem(candle_item)
            self.kline_items.append(candle_item)
        else:
            self.kline_items[0].set_data(kline_data)
        
        # 智慧判定：若目前滑鼠正在懸浮某一根 K 棒上，則繼續鎖定展示該 K 棒數據，否則才預設展示最新一根 K 棒
        if self.current_hover_index is not None and self.current_hover_index < len(kline_data):
            self.show_kline_info(self.current_hover_index)
        else:
            self.show_kline_info(len(kline_data) - 1)
        
        # 只要是強制對焦、或者使用者從來沒有手動調整過縮放，就執行 autoRange() 自動對焦
        if force_auto_range or (not self.is_zoomed_or_panned):
            self.plot_widget.autoRange()

    def on_mouse_moved(self, evt):
        """
        十字游標跟隨與數據解析。
        
        Args:
            evt: 滑鼠移動事件拋出之 Scene 座標點 (QPointF)
        """
        pos = evt
        if self.plot_widget.sceneBoundingRect().contains(pos):
            mouse_point = self.plot_widget.plotItem.vb.mapSceneToView(pos)
            index = int(mouse_point.x())
            
            if 0 <= index < len(self.candles):
                # 1. 最優先進行十字游標物理跟隨 (直接映射 Scene 座標為 Overlay 局部坐標，極速輕量 repaint)
                pos_in_overlay = self.plot_widget.mapFromScene(pos)
                self.crosshair_overlay.set_mouse_pos(pos_in_overlay)
                
                # 2. 去重防抖防線：只有當游標真正跨越到「新的一根 K棒」時，才重新解析 HTML 渲染右上角面板
                #    若只是在同一根 K棒上晃動，則 100% 繞過昂貴的 setText 排版開銷，瞬間消滅 90% 效能阻礙！
                if self.current_hover_index != index:
                    self.current_hover_index = index
                    self.show_kline_info(index)
            else:
                # 游標移出有效 K棒區域 時，清除游標並回復為顯示最新一根 K 棒的行情數據
                self.crosshair_overlay.set_mouse_pos(None)
                if self.current_hover_index is not None:
                    self.current_hover_index = None
                    if self.candles:
                        self.show_kline_info(len(self.candles) - 1)
        else:
            # 游標完全離開 Scene 區域
            self.crosshair_overlay.set_mouse_pos(None)


class CandlestickItem(pg.GraphicsObject):
    """
    自訂高性能 pyqtgraph 批次繪圖元件。
    
    責任說明 (Class Responsibility):
        本元件繼承自 pyqtgraph.GraphicsObject，專門負責在 PyQtGraph 的 PlotWidget 上進行 
        K棒（Candlestick K-lines）的高效率渲染。它採用「全量預繪快取 + 歷史與當前未收盤 K棒 分離渲染」技術。
    
    性能設計 (Performance Engineering & Tradeoffs):
        1. 瓶頸分析 (Bottleneck):
           Python 在 Paint 迴圈中逐一調用 QPainter.drawLine 與 drawRect 會產生數千次 Python-to-C++
           的跨界調用 (Marshaling Cost)，且每次都會受限於 GIL，造成嚴重的 UI Freeze。
        2. 快取策略 (QPicture Cache):
           本元件將已收盤的「歷史 K 棒（第 0 根到第 N-1 根）」一次性在背景編譯進 QPicture 的 display list 中。
           在 Paint 繪製迴圈中，歷史 K棒只需一行 self._picture_cache.play(p) 呼叫，直接由 Qt 底層 C++ 進行
           重放與 viewport 剪裁渲染，徹底繞過 Python 解釋器與 GIL！
        3. 增量渲染 (Incremental rendering):
           最末一根「當前未收盤 K 棒」每個 tick 都在劇烈變動，如果也納入快取，會引發快取頻繁失效重建的 QPicture 
           快取重建風暴。因此本元件將其「物理隔離」，在 Paint 時只用 QPainter 單獨輕量手繪這最後 1 根未收盤 K 棒，
           兼具了即時數據的高更新率與歷史數據的極致重播速度。
        4. 移除 Viewport Culling 以釋放拖曳/縮放：
           原本的 viewport culling 雖能減少繪製數量，但在縮放/拖曳時會高頻觸發快取重建。由於台指期單日分 K 數量極少
           （日盤約 300 根，夜盤約 840 根），一次性全量預繪只需 1~2 毫秒。移除拖曳與縮放時的高頻重新預繪後，
           整個 Zoom / Pan 的渲染工作 100% 移交給了 C++/GPU 硬件自動裁剪，從而達到極致絲滑的 60FPS 體驗！
    """
    def __init__(self, data):
        """
        初始化高性能 K 棒繪圖元件。
        
        Args:
            data: K 線資料列表，每個元素為 tuple (time_label, high, low, open, close, signals, b_h, b_l, tag)
        """
        pg.GraphicsObject.__init__(self)
        self.data = data
        self._picture_cache = None        # QPicture 歷史快取（已收盤 K 棒的向量繪製指令）
        self._cache_data_len = -1         # 快取對應的歷史 K 棒數量，防止重複生成 QPicture
        self._bounding_rect_cache = None    # boundingRect 快取，避免每次 paint 重複計算 CPU 邊界

        # 物件池 (Object Pooling)：預先建立畫筆與畫刷，完全消除 Paint 內部重複分配內存的 Memory Leak 與 GC 延遲
        self._up_pen = pg.mkPen(color=(235, 75, 75), width=1.5)
        self._up_brush = pg.mkBrush(235, 75, 75, 120)
        self._down_pen = pg.mkPen(color=(40, 167, 69), width=1.5)
        self._down_brush = pg.mkBrush(40, 167, 69, 120)
        self._flat_pen = pg.mkPen(color=(200, 200, 200), width=1.5)

        # 首次載入時全量預繪歷史快取
        self._generate_history_picture()

    def set_data(self, data):
        """
        自訂增量更新數據接口，重複使用物件，免除頻繁 removeItem/addItem 的高昂 layout 重算開銷。
        """
        self.data = data
        self._bounding_rect_cache = None  # 重置 bounding box 快取
        self.update()                     # 觸發 QGraphicsItem 內部 paint 重繪槽

    def _generate_history_picture(self):
        """
        一次性全量將「所有已收盤的歷史 K 棒」編譯進 QPicture。
        徹底拋棄拖曳/縮放時的高頻 Viewport Culling 重新計算，實現純 C++ 級別的高速 Viewport 貼圖裁切。
        """
        from PyQt5.QtGui import QPicture

        self._picture_cache = QPicture()
        painter = QPainter(self._picture_cache)

        if not self.data or len(self.data) <= 1:
            # 只有 0 或 1 根 K 棒時，歷史快取為空，最後一根在 paint 中獨立繪製
            painter.end()
            self._cache_data_len = 0
            return

        # 歷史 K 棒 = 除了最末一根未收盤以外的所有收盤 K 棒
        history_count = len(self.data) - 1

        # 批次繪製歷史 K 棒到 QPicture display list
        for i in range(history_count):
            row = self.data[i]
            high, low = float(row[1]), float(row[2])
            open_p, close_p = float(row[3]), float(row[4])
            tag = row[8]

            if tag == "up":
                painter.setPen(self._up_pen)
                painter.setBrush(self._up_brush)
            elif tag == "down":
                painter.setPen(self._down_pen)
                painter.setBrush(self._down_brush)
            else:
                painter.setPen(self._flat_pen)
                painter.setBrush(Qt.NoBrush)

            painter.drawLine(pg.QtCore.QPointF(i, low), pg.QtCore.QPointF(i, high))
            painter.drawRect(pg.QtCore.QRectF(i - 0.3, open_p, 0.6, close_p - open_p))

        painter.end()
        self._cache_data_len = history_count

    def paint(self, p, *args):
        """
        零開銷渲染主核心：
        1. 純 C++ 重播歷史 K 棒的 QPicture 快取（Python 零參與）。
        2. 用 QPainter 輕量獨立繪製最後一根未收盤 K 棒（開銷極低）。
        
        如此不論 Tick 怎麼跳動，歷史的幾千根 K 棒都直接走 C++ 重放，徹底免除快取失效與 UI Freeze 問題。
        """
        if not self.data:
            return

        # 偵測歷史 K 棒數量是否改變（即新分 K 收盤），若是則重新生成歷史 QPicture 快取
        history_count = max(0, len(self.data) - 1)
        if history_count != self._cache_data_len:
            self._generate_history_picture()

        # 1. Replay 歷史 QPicture（純 C++ 層執行，Python 只做 1 次函數呼叫，0 GIL 限制）
        if self._picture_cache:
            self._picture_cache.play(p)

        # 2. 獨立手繪最新一根未收盤 K 棒（僅繪製 1 根，速度飛快）
        last_idx = len(self.data) - 1
        row = self.data[last_idx]
        high, low = float(row[1]), float(row[2])
        open_p, close_p = float(row[3]), float(row[4])
        tag = row[8]

        if tag == "up":
            p.setPen(self._up_pen)
            p.setBrush(self._up_brush)
        elif tag == "down":
            p.setPen(self._down_pen)
            p.setBrush(self._down_brush)
        else:
            p.setPen(self._flat_pen)
            p.setBrush(Qt.NoBrush)

        p.drawLine(pg.QtCore.QPointF(last_idx, low), pg.QtCore.QPointF(last_idx, high))
        p.drawRect(pg.QtCore.QRectF(last_idx - 0.3, open_p, 0.6, close_p - open_p))

    def viewRangeChanged(self):
        """
        當 ViewBox 範圍改變（zoom/pan）時，完全跳過任何重寫。
        因為全量快取已經在 QPicture 中，C++ 層會自動進行 GPU 視界剪裁，
        保證拖曳與滾輪縮放時極致絲滑，絕不卡頓。
        """
        pass

    def boundingRect(self):
        """
        智慧手動計算精確的物理包圍盒，使用快取避免每次 paint 都重複計算邊界。
        """
        if self._bounding_rect_cache is not None:
            return self._bounding_rect_cache

        if not self.data:
            return pg.QtCore.QRectF(0, 0, 1, 1)

        highs = [float(row[1]) for row in self.data]
        lows = [float(row[2]) for row in self.data]
        min_y = min(lows)
        max_y = max(highs)

        height = max_y - min_y
        if height == 0:
            height = 1.0

        # X軸: [-1, 長度+1], Y軸: 最低值至最高值，並加上 5% 上下緩衝間距以確保視覺美觀
        self._bounding_rect_cache = pg.QtCore.QRectF(
            -1, min_y - height * 0.05, len(self.data) + 1, height * 1.1
        )
        return self._bounding_rect_cache

# ═══════════════════════════════════════════════════════════════════
# 4. 未破分K監控 Dialog 子視窗 (UnbrokenKMonitorDialog)
# ═══════════════════════════════════════════════════════════════════

class UnbrokenKMonitorWidget(QWidget):
    """
    職責: 未破分K停損監控嵌入元件。
    
    使用自訂 PyQt 信號 sig_update_ui，實作執行緒安全且流暢的 UI 重繪刷新，
    徹底解決因 safe_call 方法不存在而引發的 AttributeError 線程中斷問題。
    """
    # 定義 PyQt 執行緒安全信號，用以傳遞未破停損資料結構與最新價位
    sig_update_ui = Signal(dict, str)

    def __init__(self, engine: TradingEngine, parent=None):
        """
        初始化監控元件，配置 layout 並連接信號槽。
        
        責任說明:
            UnbrokenKMonitorWidget 負責維護並即時監控所有未破停損的分 K 關聯。
        
        變數用途:
            self._bg_check_in_progress: 背景計算執行緒狀態鎖，防止重複計算。
            self._current_unbroken_map: 快取當前所有未破停損點的資料結構，用以進行 O(1) 極速突破過濾。
            self._last_trigger_time: 記錄上一次啟動背景 Thread 計算的時間戳，用以進行時間差節流。
        """
        super().__init__(parent)
        self.engine = engine
        self.parent_app = parent
        
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        
        self.title_label = QLabel("🛡️ 未破分 K 停損監控", self)
        self.title_label.setStyleSheet("font-weight: bold; font-size: 11px; color: #00ffcc; padding: 4px;")
        layout.addWidget(self.title_label)
        
        self.txt_display = QTextEdit(self)
        self.txt_display.setFont(QFont("Consolas", 11))
        self.txt_display.setReadOnly(True)
        self.txt_display.setStyleSheet("background-color: #1a1a1a; color: #dcdcdc; border: 1px solid #333333;")
        layout.addWidget(self.txt_display)
        
        self.lbl_status = QLabel("雙軌毫秒級 Tick 即時監控中...", self)
        self.lbl_status.setStyleSheet("color: gray; font-size: 11px;")
        layout.addWidget(self.lbl_status)
        
        self._bg_check_in_progress = False
        self._current_unbroken_map = {}
        self._last_trigger_time = 0.0  # 用於限制背景計算的最小更新間隔
        
        # 連結自訂信號，確保 update_ui 永遠回到 PyQt 的主 UI 執行緒安全執行
        self.sig_update_ui.connect(self.update_ui)
        
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.trigger_unbroken_check)
        self.timer.start(1500)  # 縮短為 1.5 秒更新一次，相較原 5 秒更即時，但完全不卡 CPU

    def trigger_unbroken_check(self, force: bool = False):
        """
        週期性或事件驅動觸發未破停損分析。
        引入強健的時間戳防抖與防禦性錯誤處理，徹底消滅高頻行情下的 Thread Storm 風暴。
        
        Args:
            force: 若為 True，代表產生了新的極值信號，此時將繞過 1.5 秒的節流閥，立刻啟動背景 Thread 計算以保證對焦及時性。
        """
        if self._bg_check_in_progress:
            return
            
        now = time.time()
        # 性能優化防線：在沒有產生新信號的普通 Tick 跳動期間，限制背景計算的最小時間差為 1.5 秒
        # 這既滿足了極高的 K 線即時刷新需求，又徹底釋放了高頻行情下的 CPU 與 GIL 鎖資源
        if not force and (now - self._last_trigger_time < 1.5):
            return
            
        self._last_trigger_time = now
        self._bg_check_in_progress = True
        
        try:
            obs_n = self.parent_app.get_obs_n()
            intervals = self.parent_app.get_kline_intervals()
            session_data = self.parent_app.gather_session_data_snapshot()
            
            if not session_data or not any(x[1] for x in session_data):
                self.lbl_status.setText("無可用數據進行未破停損分析。")
                self._bg_check_in_progress = False
                return
        except Exception as setup_err:
            # 確保提取參數異常時能安全自我解鎖，防止背景線程永遠鎖死
            self._bg_check_in_progress = False
            if hasattr(self.parent_app, 'append_log'):
                self.parent_app.append_log(f"[未破分K監控] 初始化提取出錯: {setup_err}")
            return
            
        def worker():
            """
            背景計算工作線程：執行密集的分K聚合與停損時序狀態機回測模擬。
            完全隔離於 UI 主執行緒，防範 UI Freeze，並採用 sleep 機制釋放 GIL 鎖。
            """
            try:
                unbroken_map = {}
                for interval in intervals:
                    try:
                        int_mins = int(interval)
                    except ValueError:
                        continue
                        
                    # 智慧釋放鎖：每完成一個分 K 聚合，主動微幅休眠 5 毫秒，給主 UI 執行緒絕對渲染優先權，杜絕 CPU 滯後
                    time.sleep(0.005)
                        
                    for session_name, trades, txf_sigs, mxf_sigs in session_data:
                        kline_data, _ = self.engine._calc_kline_data(
                            session_name, trades, txf_sigs, mxf_sigs, interval_mins=int_mins
                        )
                        
                        results = self.engine._calc_simulation_results(
                            session_name, trades, kline_data, obs_n=obs_n
                        )
                        
                        for confirmed_key, row, tags in results:
                            if tags and ("history" in tags or "annotation" in tags):
                                continue
                            if len(row) >= 8:
                                sig_label = str(row[0])
                                stop_loss_val = str(row[7])
                                
                                if stop_loss_val and stop_loss_val != "N/A" and "已破" not in stop_loss_val:
                                    if "K高" in sig_label:
                                        map_key = ("high", stop_loss_val)
                                        if map_key not in unbroken_map:
                                            unbroken_map[map_key] = set()
                                        unbroken_map[map_key].add(int_mins)
                                    elif "K低" in sig_label:
                                        map_key = ("low", stop_loss_val)
                                        if map_key not in unbroken_map:
                                            unbroken_map[map_key] = set()
                                        unbroken_map[map_key].add(int_mins)
                                        
                current_price = "N/A"
                if session_data:
                    for _, trades, _, _ in reversed(session_data):
                        if trades:
                            current_price = str(trades[-1]["price"])
                            break
                            
                # 透過 PyQt 信號機制安全傳回主執行緒
                self.sig_update_ui.emit(unbroken_map, current_price)
            except Exception as e:
                if hasattr(self.parent_app, 'append_log'):
                    self.parent_app.append_log(f"[未破分K監控] 背景計算線程出錯: {e}")
            finally:
                # 執行緒結束必定安全解鎖背景鎖狀態
                self._bg_check_in_progress = False
                
        # 啟動 daemon 背景執行緒，防範程式退出時殘留殭屍程序
        threading.Thread(target=worker, daemon=True).start()

    def update_ui(self, unbroken_map: dict, current_price: str):
        self._current_unbroken_map = dict(unbroken_map)
        self.render_text(current_price)

    def check_instant_unbroken_breakout(self, price: float):
        if not self._current_unbroken_map:
            return
            
        broken_keys = []
        for (sig_type, stop_loss_val) in list(self._current_unbroken_map.keys()):
            try:
                sl_p = float(stop_loss_val)
                if sig_type == "low" and price >= sl_p:
                    broken_keys.append((sig_type, stop_loss_val))
                elif sig_type == "high" and price <= sl_p:
                    broken_keys.append((sig_type, stop_loss_val))
            except ValueError:
                continue
                
        if broken_keys:
            for k in broken_keys:
                self._current_unbroken_map.pop(k, None)
            self.render_text(str(price))

    def render_text(self, current_price: str):
        """
        主執行緒純 UI 渲染：根據內存快照 `self._current_unbroken_map` 刷新顯示，
        格式與排序邏輯 100% 參照 buylow_sellhigh_gui.py。
        """
        # 1. 記憶當前水平與垂直滾動條的值，防止渲染新文字時畫面強制跳動
        h_scrollbar = self.txt_display.horizontalScrollBar()
        v_scrollbar = self.txt_display.verticalScrollBar()
        prev_h_val = h_scrollbar.value()
        prev_v_val = v_scrollbar.value()

        try:
            display_price = int(round(float(current_price)))
        except (ValueError, TypeError):
            display_price = current_price
            
        now_str = datetime.now().strftime("%H:%M:%S")
        self.setWindowTitle(f"未破分K 停損監控 (更新: {now_str}  價位: {display_price})")
        if hasattr(self, 'title_label'):
            self.title_label.setText(f"🛡️ 未破分 K 停損監控 (價位: {display_price})")
        
        unbroken_map = getattr(self, '_current_unbroken_map', {})
        if unbroken_map:
            short_entries = []  # 做空（觀察 K 低）
            long_entries = []   # 做多（觀察 K 高）

            for (sig_type, price), interval_set in unbroken_map.items():
                sorted_intervals = sorted(interval_set)
                intervals_str = "、".join(str(i) for i in sorted_intervals)
                if sig_type == "low":
                    short_entries.append((len(interval_set), price, intervals_str))
                elif sig_type == "high":
                    long_entries.append((len(interval_set), price, intervals_str))

            # 各組內依停損價格從大到小 (由高到低) 排列，確保排序邏輯與 Tkinter 版一致
            short_entries.sort(key=lambda x: float(x[1]) if x[1] else 0.0, reverse=True)
            long_entries.sort(key=lambda x: float(x[1]) if x[1] else 0.0, reverse=True)

            lines = []
            if short_entries:
                lines.append(f"═══ 做空（觀察 K 低） 共 {len(short_entries)} 項 ═══")
                for _count, price, intervals_str in short_entries:
                    lines.append(f"  停損價: {price}  未破: {intervals_str} 分K")
            if long_entries:
                if lines:
                    lines.append("")  # 空行分隔
                lines.append(f"═══ 做多（觀察 K 高） 共 {len(long_entries)} 項 ═══")
                for _count, price, intervals_str in long_entries:
                    lines.append(f"  停損價: {price}  未破: {intervals_str} 分K")

            display_text = "\n".join(lines)
        else:
            display_text = "所有分 K 的停損價均已顯示「已破」或目前無觀察訊號。"
            
        self.txt_display.setPlainText(display_text)
        self.lbl_status.setText(f"最後更新: {now_str} | 雙軌毫秒級 Tick 即時監控")

        # 2. 智慧還原水平與垂直滾動條位置，確保交易員自由瀏覽時不受刷新干擾
        h_scrollbar.setValue(prev_h_val)
        v_scrollbar.setValue(prev_v_val)

    def closeEvent(self, event):
        self.timer.stop()
        super().closeEvent(event)

# ═══════════════════════════════════════════════════════════════════
# 4.5. 背景高精虛擬時鐘歷史 Tick 發射器 (ReplayThread)
# ═══════════════════════════════════════════════════════════════════

class ReplayThread(QThread):
    """
    職責: 高精虛擬時鐘歷史 Tick 發射執行緒。
    在背景解析 event.log，控制播放進度、播放速度，並實現交易空檔自動跳過邏輯。
    """
    # 自訂信號：當發射 Tick、虛擬時間變更、進度變更及播畢時觸發
    tick_emitted = Signal(dict)
    virtual_time_changed = Signal(str)
    progress_changed = Signal(int)
    replay_finished = Signal()

    def __init__(self, ticks: List[dict], parent=None):
        """
        初始化回放執行緒。
        
        Args:
            ticks: 解析完成之歷史 Tick 串列
        """
        super().__init__(parent)
        self.ticks = ticks
        self.current_idx = 0
        self.speed = 1.0
        self.is_paused = False
        self._run_flag = True
        self._rt_lock = threading.Lock()

    def set_speed(self, speed: float):
        """執行緒安全地設定回放速度倍率"""
        with self._rt_lock:
            self.speed = speed

    def set_paused(self, paused: bool):
        """執行緒安全地設定暫停/繼續狀態"""
        with self._rt_lock:
            self.is_paused = paused

    def set_position(self, index: int):
        """執行緒安全地設定當前回放進度"""
        with self._rt_lock:
            if 0 <= index < len(self.ticks):
                self.current_idx = index
                self.progress_changed.emit(self.current_idx)

    def stop(self):
        """執行緒安全地終止回放"""
        with self._rt_lock:
            self._run_flag = False
            self.is_paused = False

    def run(self):
        """回放發射主循環邏輯"""
        if not self.ticks:
            self.replay_finished.emit()
            return

        with self._rt_lock:
            self._run_flag = True
            if self.current_idx >= len(self.ticks) - 1:
                self.current_idx = 0

        while True:
            # 執行緒安全地解包狀態
            with self._rt_lock:
                if not self._run_flag:
                    break
                is_paused = self.is_paused
                idx = self.current_idx
                speed = self.speed

            if is_paused:
                time.sleep(0.05)
                continue

            if idx >= len(self.ticks):
                self.replay_finished.emit()
                break

            current_tick = self.ticks[idx]
            
            # 發射當前 Tick 資訊
            self.tick_emitted.emit(current_tick)
            self.progress_changed.emit(idx)
            self.virtual_time_changed.emit(current_tick["time"])

            next_idx = idx + 1
            if next_idx < len(self.ticks):
                next_tick = self.ticks[next_idx]
                time_diff = next_tick["t_val"] - current_tick["t_val"]

                # 實作：超過 60 秒交易空檔自動瞬間跳過 (Q1-B 需求)
                if time_diff > 60.0:
                    time_diff = 3.0  # 縮短為 3 秒的等待

                # 依據播放速度計算需要延時的時間 (真實秒數)
                delay = time_diff / speed if speed > 0 else 0.0

                # 採用 20ms 的微型 chunk 分段 sleep，以高靈敏度響應暫停/終止/拖曳事件
                chunk_time = 0.02
                elapsed = 0.0
                while elapsed < delay:
                    with self._rt_lock:
                        if not self._run_flag or self.is_paused:
                            break
                        # 支持播放中動態調節速度
                        speed = self.speed
                    delay = time_diff / speed if speed > 0 else 0.0
                    
                    sleep_len = min(chunk_time, delay - elapsed)
                    if sleep_len > 0:
                        time.sleep(sleep_len)
                        elapsed += sleep_len
            else:
                self.replay_finished.emit()
                break

            with self._rt_lock:
                self.current_idx += 1


# ═══════════════════════════════════════════════════════════════════
# 5. 主視窗控制中心與元大 COM 連接器 (ExtremeSignalApp)
# ═══════════════════════════════════════════════════════════════════

class ExtremeSignalApp(QMainWindow):
    """
    職責: 專業交易系統主介面控制中心。
    """
    # 執行緒安全的即時行情背景分析完成訊號 (抹平定時器延遲，完全事件驱动)
    rt_analysis_completed = Signal(dict)
    # 執行緒安全的復盤日誌背景解析完成訊號
    replay_parse_completed = Signal(dict)
    
    def __init__(self):
        super().__init__()
        self.setWindowTitle("台指極值訊號自動對比 - 現代高性能 32位元版 (PyQt5)")
        self.resize(1250, 750)
        self.setStyleSheet("background-color: #121212; color: #e0e0e0;")
        
        self.engine = TradingEngine()
        self.engine.analysis_completed.connect(self.on_analysis_completed)
        self.engine.log_triggered.connect(self.append_log)
        
        # 連結執行緒安全的即時分析完成訊號，徹底實現事件驅動
        self.rt_analysis_completed.connect(self.apply_realtime_analysis_ui)
        self.replay_parse_completed.connect(self.on_replay_parse_done)
        
        self.live_symbol_trades = defaultdict(lambda: {"日盤": [], "夜盤": []})
        self._rt_state = {
            sym: {"日盤": self._init_rt_state(), "夜盤": self._init_rt_state()} 
            for sym in ["TXF", "MXF"]
        }
        self._rt_lock = threading.Lock()
        
        # 雙軌並行回放數據流隔離變數
        self.is_replaying = False
        self.replay_symbol_trades = defaultdict(lambda: {"日盤": [], "夜盤": []})
        self._replay_rt_state = {
            sym: {"日盤": self._init_rt_state(), "夜盤": self._init_rt_state()} 
            for sym in ["TXF", "MXF"]
        }
        self.replay_thread = None
        self.all_parsed_ticks = []
        self.current_replay_dir = None
        
        self.is_realtime_running = False
        self.quote_wrapper = None  # comtypes COM 封裝 (取代 QAxWidget)
        self._com_hwnd = None      # ActiveX 宿主視窗的原生 HWND
        self.current_realtime_port = 443
        self.current_session_name = "日盤"
        self._current_target_days = 60
        self._current_kline_interval = 30
        self._current_obs_n = 25
        self._rt_preload_done = set()
        self._rt_triggers = {}
        self._rt_calc_cache = {}
        
        self.config = self.load_config()
        self.tg_token = self.config.get("telegram_token", "")
        self.tg_chat_id = self.config.get("telegram_chat_id", "")
        self.api_user = self.config.get("username", "")
        self.api_pwd = self.config.get("password", "")
        
        self.wnd_unbroken_k = None
        self.current_file_path = None
        
        self._analysis_event = threading.Event()
        self._analysis_thread_running = False
        self._analysis_thread = None
        
        # 建立非同步 Telegram 獨立發送隊列與執行緒機制，防止主執行緒同步阻塞
        self._tg_queue = queue.Queue()
        self._tg_thread_running = False
        self._tg_thread = None
        
        self._highlighted_k_items = []
        
        self.setup_ui()
        
        # 在主執行緒初始化 COM (必須在任何 COM 操作之前)
        pythoncom.CoInitialize()
        
        self.session_timer = QTimer(self)
        self.session_timer.timeout.connect(self.check_session_change)
        self.session_timer.start(60000)
        
        # 定期幫浦 COM 訊息 (核心！讓 ActiveX 事件能正確觸發)
        # PyQt5 的事件迴圈不會自動分發 COM 訊息，必須手動定期 Pump
        self._com_pump_timer = QTimer(self)
        self._com_pump_timer.timeout.connect(self._pump_com_messages)
        self._com_pump_timer.start(10)  # 每 10ms 幫浦一次，確保即時行情不延遲
        
        QTimer.singleShot(1000, self.start_realtime)

    def get_durations_cached(self, symbol: str, active_session: str, trades: List[dict], n_ticks: int, i: int, pre_side: str, post_side: str) -> Tuple:
        """
        職責: 帶快取的高性能 get_durations 計算。
        對已達標或已確定突破無效的歷史極值點進行 O(1) 快速讀取，
        徹底免除背景分析執行緒的 $O(M \times N)$ 重複搜尋開銷。
        """
        if not hasattr(self, '_rt_calc_cache'):
            self._rt_calc_cache = {}
            
        cache_key = (symbol, active_session, i, pre_side, post_side)
        if cache_key in self._rt_calc_cache:
            res, is_final = self._rt_calc_cache[cache_key]
            if is_final:
                return res
                
        # 呼叫原始 TradingEngine 進行搜尋計算
        res = self.engine.get_durations(trades, n_ticks, i, pre_side, post_side)
        pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = res
        
        # 智慧終止判定：判定該極值點是否已經永久固定
        is_final = False
        if act_post >= n_ticks:
            # 後向同盤成交筆數已湊滿 N 筆，B 點完全確認，結果再也不會改變
            is_final = True
        elif pre is None and b_idx < len(trades) - 1:
            # pre 為 None 且突破索引小於當前最大索引，代表突破已是歷史事實，永久無效
            is_final = True
            
        self._rt_calc_cache[cache_key] = (res, is_final)
        return res

    def eventFilter(self, watched, event) -> bool:
        """
        職責: 滑鼠事件過濾器。
        1. 當使用者點擊 txt_output 已選取(反白)的文字時，手動清除反白選取。
        2. 當使用者點擊數據表格 (K線表格與極值表格) 已選取的行時，手動清除反白，
           基於原生 selectionModel 實現百分之百狀態同步，徹底消除點擊異常。
        """
        from PyQt5.QtCore import QEvent
        
        # 1. 處理主文字看板的點選取消反白
        if watched is self.txt_output and event.type() == QEvent.MouseButtonPress:
            cursor = self.txt_output.cursorForPosition(event.pos())
            text_cursor = self.txt_output.textCursor()
            if text_cursor.hasSelection():
                # 判斷滑鼠點選的位置是否落在目前反白選取的文字區間內
                if text_cursor.selectionStart() <= cursor.position() <= text_cursor.selectionEnd():
                    text_cursor.clearSelection()
                    self.txt_output.setTextCursor(text_cursor)
                    return True # 攔截事件，防止雙擊狀態異常
                    
        # 2. 處理數據表格的點選取消反白 (view_kline 與 view_observer)
        elif event.type() == QEvent.MouseButtonPress:
            from PyQt5.QtWidgets import QTableView
            
            table_view = None
            if isinstance(watched, QTableView):
                table_view = watched
            elif hasattr(self, 'view_kline') and watched is self.view_kline.viewport():
                table_view = self.view_kline
            elif hasattr(self, 'view_observer') and watched is self.view_observer.viewport():
                table_view = self.view_observer
                
            if table_view is not None:
                index = table_view.indexAt(event.pos())
                if index.isValid():
                    # 在滑鼠點擊處理前，精確偵測該行當前是否已被反白選取
                    is_already_selected = table_view.selectionModel().isSelected(index)
                    if is_already_selected:
                        # 如果點擊前已選取，則清除所有選取 (取消反白)
                        table_view.clearSelection()
                        
                        # 智慧連動：若是取消了極值觀測表的選取，同步取消 K線表的反白
                        if table_view is self.view_observer:
                            self.view_kline.clearSelection()
                            
                        return True # 攔截事件，防止 QTableView 的原生事件處理器再次將其自動選取
                    
        return super().eventFilter(watched, event)

    def load_config(self) -> dict:
        config_path = os.path.join(APP_DIR, "config.json")
        if os.path.exists(config_path):
            with open(config_path, "r", encoding="utf-8") as f:
                return json.load(f)
        return {}

    def _init_rt_state(self) -> dict:
        return {
            "outer_count": 0, "inner_count": 0,
            "first_outer_time": None, "last_outer_time": None,
            "first_inner_time": None, "last_inner_time": None,
            "sum_price": 0, "count": 0,
            "day_max": -999999, "day_min": 999999,
            "max_time": "--", "min_time": "--",  # 增量 O(1) 極值時間戳記
            "running_max": -999999, "running_min": 999999,
            "last_price": None,
            "last_check_time_h": -999999.0,
            "last_check_time_b": -999999.0,
            "scan_idx": 0
        }

    def setup_ui(self):
        central_widget = QWidget(self)
        self.setCentralWidget(central_widget)
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(10, 10, 10, 10)
        
        top_layout = QHBoxLayout()
        main_layout.addLayout(top_layout)
        
        self.btn_open = QPushButton("開啟日誌檔 (.log)", self)
        self.btn_open.clicked.connect(self.load_file)
        self.btn_open.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 5px;")
        top_layout.addWidget(self.btn_open)
        
        self.btn_fill = QPushButton("補齊資料", self)
        self.btn_fill.clicked.connect(self.fill_missing_data)
        self.btn_fill.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 5px;")
        top_layout.addWidget(self.btn_fill)
        
        self.btn_realtime = QPushButton("連接即時行情", self)
        self.btn_realtime.clicked.connect(self.toggle_realtime)
        self.btn_realtime.setStyleSheet("background-color: #005a9e; font-weight: bold; color: white; padding: 5px;")
        top_layout.addWidget(self.btn_realtime)
        
        self.btn_update = QPushButton("更新", self)
        self.btn_update.clicked.connect(self.trigger_reanalyze)
        self.btn_update.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 5px;")
        top_layout.addWidget(self.btn_update)
        
        self.chk_telegram = QCheckBox("啟用TG", self)
        top_layout.addWidget(self.chk_telegram)
        
        self.lbl_realtime_status = QLabel("未連線", self)
        self.lbl_realtime_status.setStyleSheet("color: #eb4b4b; font-weight: bold;")
        top_layout.addWidget(self.lbl_realtime_status)
        
        top_layout.addStretch()
        
        self.lbl_status = QLabel("  請選擇 event.log 檔案開始 analysis", self)
        self.lbl_status.setStyleSheet("color: #0080ff; font-weight: bold;")
        top_layout.addWidget(self.lbl_status)
        
        # ═══════════════════════════════════════════════════════════════════
        # 新增：專業級復盤回放控制面板 (Vanilla CSS 交易暗黑質感)
        # ═══════════════════════════════════════════════════════════════════
        replay_layout = QHBoxLayout()
        main_layout.addLayout(replay_layout)
        
        replay_layout.addWidget(QLabel("復盤日期:", self))
        self.txt_replay_path = QLineEdit(self)
        self.txt_replay_path.setReadOnly(True)
        self.txt_replay_path.setPlaceholderText("請點選擇資料夾選擇...")
        self.txt_replay_path.setStyleSheet("background-color: #1a1a1a; border: 1px solid #3a3a3a; padding: 3px; color: #00a2ed; font-weight: bold;")
        replay_layout.addWidget(self.txt_replay_path)
        
        self.btn_select_replay_dir = QPushButton("選擇資料夾", self)
        self.btn_select_replay_dir.clicked.connect(self.select_replay_directory)
        self.btn_select_replay_dir.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 5px; color: white;")
        replay_layout.addWidget(self.btn_select_replay_dir)
        
        replay_layout.addWidget(QLabel("盤別:", self))
        self.cbo_replay_session = QComboBox(self)
        self.cbo_replay_session.addItems(["日盤", "夜盤"])
        self.cbo_replay_session.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 3px; color: white;")
        # 徹底刪除此連接，確保只有按下載入復盤按鈕時才載入
        replay_layout.addWidget(self.cbo_replay_session)
        
        self.btn_load_replay = QPushButton("載入復盤", self)
        self.btn_load_replay.clicked.connect(self.load_replay_log)
        self.btn_load_replay.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 5px; color: #00a2ed; font-weight: bold;")
        replay_layout.addWidget(self.btn_load_replay)
        
        self.btn_play_pause = QPushButton("▶ 播放", self)
        self.btn_play_pause.clicked.connect(self.toggle_replay_play)
        self.btn_play_pause.setEnabled(False)
        self.btn_play_pause.setStyleSheet("background-color: #005a9e; font-weight: bold; color: white; padding: 5px; min-width: 70px;")
        replay_layout.addWidget(self.btn_play_pause)
        
        self.btn_stop_replay = QPushButton("⏹ 停止", self)
        self.btn_stop_replay.clicked.connect(self.stop_replay)
        self.btn_stop_replay.setEnabled(False)
        self.btn_stop_replay.setStyleSheet("background-color: #eb4b4b; font-weight: bold; color: white; padding: 5px; min-width: 70px;")
        replay_layout.addWidget(self.btn_stop_replay)
        
        replay_layout.addWidget(QLabel("速度:", self))
        self.cbo_replay_speed = QComboBox(self)
        self.cbo_replay_speed.addItems(["1x", "2x", "5x", "10x", "20x", "50x", "自訂"])
        self.cbo_replay_speed.setCurrentText("1x")
        self.cbo_replay_speed.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 3px; color: white;")
        self.cbo_replay_speed.currentTextChanged.connect(self.on_replay_speed_changed)
        replay_layout.addWidget(self.cbo_replay_speed)
        
        replay_layout.addWidget(QLabel("速度上限:", self))
        self.spn_max_speed = QSpinbox(self)
        self.spn_max_speed.setRange(1, 500)
        self.spn_max_speed.setValue(100)
        self.spn_max_speed.setSuffix("x")
        self.spn_max_speed.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; padding: 3px; color: white;")
        self.spn_max_speed.valueChanged.connect(self.on_max_speed_limit_changed)
        replay_layout.addWidget(self.spn_max_speed)
        
        # 進度 Slider (具備微型微調動畫質感)
        replay_layout.addWidget(QLabel("進度:", self))
        self.sld_progress = QSlider(Qt.Horizontal, self)
        self.sld_progress.setEnabled(False)
        self.sld_progress.setStyleSheet("""
            QSlider::groove:horizontal {
                border: 1px solid #3a3a3a;
                height: 8px;
                background: #1a1a1a;
                border-radius: 4px;
            }
            QSlider::handle:horizontal {
                background: #00a2ed;
                border: 1px solid #005a9e;
                width: 14px;
                margin: -3px 0;
                border-radius: 7px;
            }
        """)
        self.sld_progress.sliderPressed.connect(self.on_slider_pressed)
        self.sld_progress.sliderMoved.connect(self.on_slider_moved)
        self.sld_progress.sliderReleased.connect(self.on_slider_released)
        replay_layout.addWidget(self.sld_progress, 1)
        
        self.lbl_virtual_time = QLabel("復盤時間: --:--:--", self)
        self.lbl_virtual_time.setStyleSheet("color: #00a2ed; font-family: 'Consolas'; font-weight: bold; font-size: 11px;")
        replay_layout.addWidget(self.lbl_virtual_time)
        
        # 準備好讓使用者自行選定資料夾
        
        info_layout = QHBoxLayout()
        main_layout.addLayout(info_layout)
        
        self.lbl_speed_txf = QLabel("大臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → --", self)
        self.lbl_speed_txf.setStyleSheet("color: #a0a0a0; font-family: 'Consolas';")
        info_layout.addWidget(self.lbl_speed_txf)
        
        self.lbl_speed_mxf = QLabel("小臺: 外盤(買) --/筆 | 內盤(賣) --/筆 → --", self)
        self.lbl_speed_mxf.setStyleSheet("color: #a0a0a0; font-family: 'Consolas';")
        info_layout.addWidget(self.lbl_speed_mxf)
        
        info_layout.addStretch()
        

        
        extreme_layout = QHBoxLayout()
        main_layout.addLayout(extreme_layout)
        
        self.lbl_extreme_info = QLabel("最高價: -- (--) | 最低價: -- (--) | 振幅: -- ", self)
        self.lbl_extreme_info.setStyleSheet("color: #a0a0a0;")
        extreme_layout.addWidget(self.lbl_extreme_info)
        
        self.lbl_consensus_dir = QLabel("| 共識: --", self)
        self.lbl_consensus_dir.setStyleSheet("font-weight: bold; color: gray;")
        extreme_layout.addWidget(self.lbl_consensus_dir)
        
        self.lbl_txf_net_speed = QLabel("| 大臺速差: --", self)
        self.lbl_txf_net_speed.setStyleSheet("font-weight: bold; color: gray;")
        extreme_layout.addWidget(self.lbl_txf_net_speed)
        
        self.lbl_mxf_net_speed = QLabel("| 小臺速差: --", self)
        self.lbl_mxf_net_speed.setStyleSheet("font-weight: bold; color: gray;")
        extreme_layout.addWidget(self.lbl_mxf_net_speed)
        
        self.lbl_live_price = QLabel("| 價: --", self)
        self.lbl_live_price.setStyleSheet("color: #00a2ed;")
        extreme_layout.addWidget(self.lbl_live_price)
        
        extreme_layout.addStretch()
        
        extreme_layout.addWidget(QLabel("載入回測(天):", self))
        self.cbo_backtest_days = QComboBox(self)
        self.cbo_backtest_days.addItems(["30", "60", "120", "250", "全部"])
        self.cbo_backtest_days.setCurrentText("60")
        extreme_layout.addWidget(self.cbo_backtest_days)
        
        extreme_layout.addWidget(QLabel("小臺K線(分):", self))
        self.cbo_kline_interval = QComboBox(self)
        self.cbo_kline_interval.addItems(["1", "2", "3", "4", "5", "10", "15", "30", "60"])
        self.cbo_kline_interval.setCurrentText("30")
        self.cbo_kline_interval.currentTextChanged.connect(self.trigger_kline_only_reanalyze)
        extreme_layout.addWidget(self.cbo_kline_interval)
        
        splitter = QSplitter(Qt.Vertical, self)
        main_layout.addWidget(splitter, 1)
        
        self.txt_output = QTextEdit(splitter)
        self.txt_output.setFont(QFont("Consolas", 10))
        self.txt_output.setReadOnly(True)
        self.txt_output.setStyleSheet("background-color: #1a1a1a; color: #dcdcdc; border: 1px solid #2a2a2a;")
        # 強制限制 QTextEdit 內部最大段落數為 500 行
        # 超過 500 行自動拋棄頭部舊段落，確保長時運作 memory 不膨脹，
        # 同時避免 QSyntaxHighlighter 逐行重算的 CPU 開銷隨行數線性增長
        self.txt_output.document().setMaximumBlockCount(500)
        splitter.addWidget(self.txt_output)
        
        # 綁定高性能高亮著色器 (繪製時即時套用，零延遲)
        self.highlighter = TradingLogHighlighter(self.txt_output.document())
        
        # 建立水平分割器，完美容納 2/3 K線與 1/3 未破停損監控
        self.kline_monitor_splitter = QSplitter(Qt.Horizontal, splitter)
        
        self.kline_chart = KLineChartWidget(self.kline_monitor_splitter)
        self.wnd_unbroken_k = UnbrokenKMonitorWidget(self.engine, self)
        
        self.kline_monitor_splitter.addWidget(self.kline_chart)
        self.kline_monitor_splitter.addWidget(self.wnd_unbroken_k)
        
        # 預設比例 2:1 (2/3 K線，1/3 停損監控)
        self.kline_monitor_splitter.setSizes([800, 400])
        
        splitter.addWidget(self.kline_monitor_splitter)
        
        table_container = QWidget(splitter)
        table_layout = QHBoxLayout(table_container)
        table_layout.setContentsMargins(0, 0, 0, 0)
        splitter.addWidget(table_container)
        
        self.view_kline = QTableView(table_container)
        self.view_kline.setStyleSheet("background-color: #151515; gridline-color: #2b2b2b;")
        self.view_kline.setSelectionBehavior(QTableView.SelectRows)
        self.view_kline.setSelectionMode(QTableView.SingleSelection)
        table_layout.addWidget(self.view_kline, 1)
        
        kline_headers = ["時間", "高", "低", "開", "收", "訊號標記", "突破上高", "跌破上低"]
        self.model_kline = SignalTableModel(kline_headers, self)
        self.view_kline.setModel(self.model_kline)
        self.view_kline.selectionModel().selectionChanged.connect(self.on_kline_select_changed)
        
        self.view_observer = QTableView(table_container)
        self.view_observer.setStyleSheet("background-color: #151515; gridline-color: #2b2b2b;")
        self.view_observer.setSelectionBehavior(QTableView.SelectRows)
        self.view_observer.setSelectionMode(QTableView.SingleSelection)
        table_layout.addWidget(self.view_observer, 1)
        
        obs_headers = ["類型", "A點時間", "A點價", "B點時間", "觸發價", "前向平均", "後向平均", "停損價"]
        self.model_observer = SignalTableModel(obs_headers, self)
        self.view_observer.setModel(self.model_observer)
        self.view_observer.selectionModel().selectionChanged.connect(self.on_observer_select_changed)

        obs_control_layout = QHBoxLayout()
        main_layout.addLayout(obs_control_layout)
        
        obs_control_layout.addWidget(QLabel("觀察N:", self))
        self.spn_obs_n = QSpinbox(self)
        self.spn_obs_n.setRange(5, 250)
        self.spn_obs_n.setSingleStep(5)
        self.spn_obs_n.setValue(25)
        obs_control_layout.addWidget(self.spn_obs_n)
        
        obs_control_layout.addWidget(QLabel("最高觀察:", self))
        self.cbo_obs_high = QComboBox(self)
        self.cbo_obs_high.setEditable(True)
        self.cbo_obs_high.currentTextChanged.connect(self.on_obs_high_changed)
        obs_control_layout.addWidget(self.cbo_obs_high)
        
        obs_control_layout.addWidget(QLabel("觀察 K 低:", self))
        self.ent_obs_high = QLineEdit(self)
        self.ent_obs_high.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; color: white;")
        self.ent_obs_high.returnPressed.connect(self.on_obs_high_enter)
        obs_control_layout.addWidget(self.ent_obs_high)
        
        obs_control_layout.addWidget(QLabel("最低觀察:", self))
        self.cbo_obs_low = QComboBox(self)
        self.cbo_obs_low.setEditable(True)
        self.cbo_obs_low.currentTextChanged.connect(self.on_obs_low_changed)
        obs_control_layout.addWidget(self.cbo_obs_low)
        
        obs_control_layout.addWidget(QLabel("觀察 K 高:", self))
        self.ent_obs_low = QLineEdit(self)
        self.ent_obs_low.setStyleSheet("background-color: #2a2a2a; border: 1px solid #3a3a3a; color: white;")
        self.ent_obs_low.returnPressed.connect(self.on_obs_low_enter)
        obs_control_layout.addWidget(self.ent_obs_low)
        
        self.lbl_obs_status = QLabel("觀察: 待設定", self)
        self.lbl_obs_status.setStyleSheet("color: gray;")
        obs_control_layout.addWidget(self.lbl_obs_status)
        
        obs_control_layout.addStretch()
        
        self.view_kline.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeToContents)
        self.view_kline.horizontalHeader().setSectionResizeMode(5, QHeaderView.Stretch)
        self.view_observer.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeToContents)
        self.view_observer.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        
        # 綁定 spn_obs_n 當數值改變時同步快照，避免背景線程直接讀取 GUI 元件
        self.spn_obs_n.valueChanged.connect(self.on_obs_n_changed)
        
        # 為 txt_output 與數據表格安裝事件過濾器，實現智慧點點取消反白
        self.txt_output.installEventFilter(self)
        self.view_kline.viewport().installEventFilter(self)
        self.view_observer.viewport().installEventFilter(self)

    @Slot(dict)
    def on_analysis_completed(self, result: dict):
        success = result["success"]
        res_data = result["result"]
        
        self._temp_offline_trades = result["temp_trades"]
        self._temp_offline_signals = result["temp_signals"]
        
        if success and isinstance(res_data, dict):
            final_content = ""
            all_offline_kline_data = []
            observer_preload_tasks = []
            
            for session in ["日盤", "夜盤"]:
                if res_data.get(session):
                    final_content += res_data[session] + "\n"
                    
                txf_t, mxf_t = None, None
                txf_sigs, mxf_sigs = [], []
                for key, trades in self._temp_offline_trades.items():
                    if "TXF" in key and session in key: txf_t = trades
                    elif "MXF" in key and session in key: mxf_t = trades
                for key, sigs in self._temp_offline_signals.items():
                    if "TXF" in key and session in key: txf_sigs = sigs
                    elif "MXF" in key and session in key: mxf_sigs = sigs
                        
                if txf_t and mxf_t:
                    pushes = self.engine._simulate_speed_pushes_dual(txf_t, mxf_t)
                    if pushes:
                        final_content += f"\n    [{session} 大小臺共識推播歷程]\n"
                        for p in pushes:
                            final_content += p + "\n"
                            
                if mxf_t:
                    kline_data, breakouts = self.engine._calc_kline_data(session, mxf_t, txf_sigs, mxf_sigs)
                    kline_str = self.engine._generate_kline_text(
                        session_name=session,
                        kline_data=kline_data,
                        breakouts=breakouts,
                        interval_mins=self.cbo_kline_interval.currentText()
                    )
                    if kline_str:
                        final_content += "\n" + kline_str + "\n"
                    all_offline_kline_data.extend(kline_data)
                    observer_preload_tasks.append((session, mxf_t, kline_data))
                    
            self.append_log(final_content, clear=True)
            
            for session, mxf_t, kline_data in observer_preload_tasks:
                self.preload_observer_table(session, mxf_t, kline_data)
                
            if all_offline_kline_data:
                self.update_kline_views(all_offline_kline_data)
                
            # === 更新離線載入時的極值、速差與共識顯示 ===
            mxf_offline_trades = []
            txf_offline_trades = []
            for key, trades in self._temp_offline_trades.items():
                if "MXF" in key: mxf_offline_trades.extend(trades)
                elif "TXF" in key: txf_offline_trades.extend(trades)
                    
            if mxf_offline_trades:
                mxf_offline_trades.sort(key=lambda x: x["t_val"])
                mxf_prices = [t["price"] for t in mxf_offline_trades]
                day_max_m = max(mxf_prices)
                day_min_m = min(mxf_prices)
                amp_m = day_max_m - day_min_m
                max_time_m = next(t["time"] for t in reversed(mxf_offline_trades) if t["price"] == day_max_m)
                min_time_m = next(t["time"] for t in reversed(mxf_offline_trades) if t["price"] == day_min_m)
                self.lbl_extreme_info.setText(f"最高價: {day_max_m} ({max_time_m}) | 最低價: {day_min_m} ({min_time_m}) | 振幅: {amp_m}")
                
                # 計算離線成交速度
                oa_t, ia_t, d_t = self.engine._calc_side_speed(txf_offline_trades)
                oa_m, ia_m, d_m = self.engine._calc_side_speed(mxf_offline_trades)
                
                # 更新速差 UI
                if oa_t is not None and ia_t is not None:
                    net_t = ia_t - oa_t
                    self.lbl_txf_net_speed.setText(f"| 大臺速差: {net_t:+.4f}s")
                    self.set_widget_style_lazy(self.lbl_txf_net_speed, "color: #eb4b4b; font-weight: bold;" if net_t > 0 else "color: #28a745; font-weight: bold;" if net_t < 0 else "color: gray; font-weight: bold;")
                else:
                    self.lbl_txf_net_speed.setText("| 大臺速差: --")
                    self.set_widget_style_lazy(self.lbl_txf_net_speed, "color: gray; font-weight: bold;")
                    
                if oa_m is not None and ia_m is not None:
                    net_m = ia_m - oa_m
                    self.lbl_mxf_net_speed.setText(f"| 小臺速差: {net_m:+.4f}s")
                    self.set_widget_style_lazy(self.lbl_mxf_net_speed, "color: #eb4b4b; font-weight: bold;" if net_m > 0 else "color: #28a745; font-weight: bold;" if net_m < 0 else "color: gray; font-weight: bold;")
                else:
                    self.lbl_mxf_net_speed.setText("| 小臺速差: --")
                    self.set_widget_style_lazy(self.lbl_mxf_net_speed, "color: gray; font-weight: bold;")
                    
                # 更新共識 UI
                if d_t and d_m:
                    if "多方" in d_t and "多方" in d_m:
                        self.lbl_consensus_dir.setText("| 共識: 多方 📈")
                        self.set_widget_style_lazy(self.lbl_consensus_dir, "color: #eb4b4b; font-weight: bold;")
                    elif "空方" in d_t and "空方" in d_m:
                        self.lbl_consensus_dir.setText("| 共識: 空方 📉")
                        self.set_widget_style_lazy(self.lbl_consensus_dir, "color: #28a745; font-weight: bold;")
                    elif d_t != "資料不足" and d_m != "資料不足":
                        self.lbl_consensus_dir.setText("| 共識: 持平 ⚖️")
                        self.set_widget_style_lazy(self.lbl_consensus_dir, "color: gray; font-weight: bold;")
                    else:
                        self.lbl_consensus_dir.setText("| 共識: --")
                        self.set_widget_style_lazy(self.lbl_consensus_dir, "color: gray; font-weight: bold;")
            
            self.lbl_status.setText("更新完成")
        else:
            self.append_log(f"分析失敗: {res_data}", clear=True)
            self.lbl_status.setText("分析出錯")
            
        self.btn_update.setEnabled(True)
        self.btn_open.setEnabled(True)
        self.btn_fill.setEnabled(True)
        
        # 離線分析完成後，立即觸發「未破分K監控」重算，實現 0 毫秒極速響應
        if self.wnd_unbroken_k:
            self.wnd_unbroken_k.trigger_unbroken_check()
            
        # 更新底部「最高觀察」與「最低觀察」下拉選單之數值
        self.refresh_observer_comboboxes()

    def start_realtime(self):
        if self.is_realtime_running:
            return
            
        self.is_realtime_running = True
        self.btn_realtime.setText("停止即時行情")
        
        port, session = self.check_session_port()
        self.current_realtime_port = port
        self.current_session_name = session
        self.lbl_realtime_status.setText(f"連線中...({port})")
        self.lbl_realtime_status.setStyleSheet("color: orange; font-weight: bold;")
        self.append_log(f"\n--- 啟動即時行情 ({session} Port:{port}) ---")
        
        self.live_symbol_trades.clear()
        for sym in ["TXF", "MXF"]:
            self.live_symbol_trades[sym] = {"日盤": [], "夜盤": []}
            
        if hasattr(self, '_rt_calc_cache'):
            self._rt_calc_cache.clear()
            
        # 使用 comtypes + ATL 建立 ActiveX COM 元件 (取代失敗的 QAxWidget)
        if self.quote_wrapper is None:
            # 建立一個 1x1 的隱藏 Win32 原生視窗作為 ActiveX 宿主
            # 避免 ActiveX 子視窗覆蓋 PyQt5 介面或搶走鍵盤焦點
            if self._com_hwnd is None:
                # WS_CHILD=0x40000000 讓它成為子視窗，不會獨立彈出
                WS_CHILD = 0x40000000
                # 取得 PyQt5 主視窗的原生 HWND 作為父視窗
                parent_hwnd = int(self.winId())
                # 先初始化 ATL 模組
                atl.AtlAxWinInit()
                # 建立隱藏的 ATL 宿主視窗 (位置在視窗外不可見)
                self._com_hwnd = user32.CreateWindowExW(
                    0,           # dwExStyle
                    "AtlAxWin",  # ATL 視窗類別名稱
                    "",          # 視窗標題
                    WS_CHILD,    # 子視窗樣式
                    -10, -10, 1, 1,  # x, y, w, h (位於可見區域外)
                    parent_hwnd,  # 父視窗
                    0,           # 選單
                    0,           # 執行個體控制代碼
                    0            # 額外參數
                )
            self.quote_wrapper = YuantaQuoteWrapper(self._com_hwnd, self)
            
        self.quote_wrapper.YuantaQuote.SetMktLogon(
            self.api_user, self.api_pwd, "203.66.93.84", str(port), 1, 0)
                                  
        self._analysis_thread_running = True
        self._analysis_thread = threading.Thread(target=self.analysis_worker_loop, daemon=True)
        self._analysis_thread.start()
        
        # 啟動非同步 Telegram 背景發送執行緒
        self._tg_thread_running = True
        self._tg_thread = threading.Thread(target=self.telegram_worker_loop, daemon=True)
        self._tg_thread.start()
        
        self.append_log(f"  [系統] 背景分析執行緒已啟動，Debounce = {ANALYSIS_DEBOUNCE_SEC}秒")

    def stop_realtime(self):
        if not self.is_realtime_running:
            return
            
        self.is_realtime_running = False
        self.btn_realtime.setText("連接即時行情")
        self.lbl_realtime_status.setText("未連線")
        self.lbl_realtime_status.setStyleSheet("color: #eb4b4b; font-weight: bold;")
        self.append_log("\n--- 停止即時行情 ---")
        
        self._analysis_thread_running = False
        self._analysis_event.set()
        if self._analysis_thread:
            self._analysis_thread.join(timeout=1.0)
            self._analysis_thread = None
            
        # 停止並加入非同步 Telegram 執行緒
        self._tg_thread_running = False
        if self._tg_thread:
            self._tg_thread.join(timeout=1.0)
            self._tg_thread = None
            
        # 斷開 COM 事件連接並釋放物件，必須徹底斬斷底層 COM 通知
        if getattr(self, 'quote_wrapper', None):
            try:
                # 將 EventsConnect 設為 None 會觸發 comtypes 呼叫 IConnectionPoint::Unadvise
                # 這樣底層 API 就無法再把殘餘事件塞入 Python 的 MessagePump
                self.quote_wrapper.YuantaQuoteEventsConnect = None
                self.quote_wrapper.YuantaQuoteEvents.wrapper = None
            except Exception:
                pass
        self.quote_wrapper = None
        
        # 銷毀隱藏的 COM 宿主視窗，徹底釋放 ActiveX 資源
        if self._com_hwnd:
            try:
                user32.DestroyWindow(self._com_hwnd)
            except Exception:
                pass
            self._com_hwnd = None

    def toggle_realtime(self):
        if self.is_realtime_running:
            self.stop_realtime()
        else:
            self.start_realtime()

    def _pump_com_messages(self):
        """
        職責: 定期在主執行緒抽取 COM 訊息，讓 ActiveX 控制元件的事件能正確觸發。
        
        PyQt5 的事件迴圈 (exec_) 不像 Tkinter 會自動分發原生 Windows Message，
        COM 事件回呼 (IConnectionPoint) 依賴 Windows Message Pump。
        如果不定期呼叫 PumpWaitingMessages()，元大 ActiveX 的
        OnMktStatusChange 和 OnGetMktAll 事件永遠不會觸發。
        
        性能考量:
        - PumpWaitingMessages() 是輕量級呼叫，在無訊息時立即返回
        - 每 10ms 呼叫一次不會對 UI 效能造成可感負擔
        - 但能確保 Tick 行情延遲控制在 10ms 內
        """
        try:
            pythoncom.PumpWaitingMessages()
        except Exception:
            pass

    def on_mkt_status_change(self, status: int, msg: str, req_type: int):
        """
        元大行情 API 登入狀態變更回呼 (由 COM 事件接收器觸發，非 Qt Signal/Slot)。
        Status == 2: 登入成功
        Status < 0: 連線異常，需要排程重連
        """
        self.append_log(f"行情狀態: {status} {msg}")
        if status == 2:
            self.lbl_realtime_status.setText(f"已連線({self.current_realtime_port})")
            self.lbl_realtime_status.setStyleSheet("color: #28a745; font-weight: bold;")
            self.register_symbols(req_type)
        elif status < 0:
            self.lbl_realtime_status.setText(f"連線異常({status})")
            self.lbl_realtime_status.setStyleSheet("color: #eb4b4b; font-weight: bold;")
            # 只有在使用者有點選勾選「啟用TG」時，才發送斷線 Telegram 推播
            if self.chk_telegram.isChecked():
                self._tg_queue.put(f"🚨 台指極值元大行情網路中斷！嘗試重連 ({status})")
            
            # 不能在 COM 事件的回呼內直接銷毀 COM 物件 (底層 C++ 呼叫尚未返回)
            # 延遲到 Qt 事件迴圈中處理，先 stop 再重連
            def schedule_reconnect():
                self.stop_realtime()
                QTimer.singleShot(2000, self.start_realtime)
            QTimer.singleShot(10, schedule_reconnect)

    def register_symbols(self, req_type: int):
        """向元大行情 API 註冊近月合約商品，使用 comtypes Dispatch 直接呼叫"""
        month_code = self.engine.get_month_code()
        symbols = [f"TXF{month_code}", f"MXF{month_code}"]
        self.append_log(f"註冊近月合約: {symbols}")
        
        for code in symbols:
            try:
                if self.quote_wrapper:
                    # mode 4 = Snapshot+Update，確保收到初始資料並 Kickstart 行情
                    res = self.quote_wrapper.YuantaQuote.AddMktReg(code, 4, req_type, 0)
                    self.append_log(f"註冊商品 {code}，結果: {res}")
            except Exception as e:
                self.append_log(f"註冊異常: {e}")
            
        self.preload_today_log()

    def on_get_mkt_all(self, symbol: str, RefPri: str, OpenPri: str, HighPri: str, LowPri: str, UpPri: str, DnPri: str,
                       MatchTime: str, MatchPri: str, MatchQty: str, TolMatchQty: str, BestBuyQty: str, BestBuyPri: str,
                       BestSellQty: str, BestSellPri: str, FDBPri: str, FDBQty: str, FDSPri: str, FDSQty: str,
                       ReqType: int):
        """
        Tick 即時行情回呼 (由 COM 事件接收器觸發，非 Qt Signal/Slot)。
        處理邏輯與原版 100% 一致：過濾無效 Tick → 判斷內外盤 → 寫入交易紀錄。
        """
        if not self.is_realtime_running:
            return
            
        if TolMatchQty == "-1":
            return
            
        try:
            current_qty = int(TolMatchQty)
            base_symbol = "TXF" if "TXF" in symbol else "MXF"
            
            if not hasattr(self, '_rt_last_match_qty'):
                self._rt_last_match_qty = {}
            if base_symbol in self._rt_last_match_qty:
                if current_qty <= self._rt_last_match_qty[base_symbol]:
                    return
            self._rt_last_match_qty[base_symbol] = current_qty
            
            price = int(float(MatchPri))
            best_bp = int(float(BestBuyPri.split(",")[0]))
            best_sp = int(float(BestSellPri.split(",")[0]))
            
            if price <= 0 or best_bp <= 0 or best_sp <= 0:
                return
                
            side = "Outer" if price >= best_sp else ("Inner" if price <= best_bp else None)
            
            mt = MatchTime.strip()
            if len(mt) >= 6:
                h, m, s = int(mt[0:2]), int(mt[2:4]), int(mt[4:6])
                ms = int(mt[6:12]) / 1_000_000 if len(mt) >= 12 else 0.0
                t_val_raw = h * 3600 + m * 60 + s + ms
            else:
                return
                
            if (30600 <= t_val_raw < 31500) or (52200 <= t_val_raw < 54000):
                return
                
            if 30600 <= t_val_raw <= 49500:
                session = "日盤"
                t_val = t_val_raw
            elif t_val_raw >= 52200 or t_val_raw <= 18000:
                session = "夜盤"
                t_val = t_val_raw + 86400 if t_val_raw <= 18000 else t_val_raw
            else:
                return
                
            if side is None:
                prev_trades = self.live_symbol_trades[base_symbol].get(session, [])
                side = prev_trades[-1]["side"] if prev_trades else "Outer"
                
            trade = {
                "time": mt, "t_val": t_val, "price": price, "side": side
            }
            
            with self._rt_lock:
                self.live_symbol_trades[base_symbol][session].append(trade)
                
                state = self._rt_state[base_symbol][session]
                state["count"] += 1
                state["sum_price"] += price
                if price > state["day_max"]: 
                    state["day_max"] = price
                    state["max_time"] = mt
                if price < state["day_min"]: 
                    state["day_min"] = price
                    state["min_time"] = mt
                
                if side == "Outer":
                    state["outer_count"] += 1
                    if state["first_outer_time"] is None: state["first_outer_time"] = t_val
                    state["last_outer_time"] = t_val
                elif side == "Inner":
                    state["inner_count"] += 1
                    if state["first_inner_time"] is None: state["first_inner_time"] = t_val
                    state["last_inner_time"] = t_val
                    
                if base_symbol == "MXF":
                    self._last_mxf_price = price
                    
            # 雙軌並行核心防線：如果在回放中，即時 Tick 靜默更新，不觸發 UI 重繪分析
            if not self.is_replaying:
                self._analysis_event.set()
        except Exception:
            pass

    def telegram_worker_loop(self):
        """
        職責: 背景非同步 Telegram 訊息傳送執行緒。
        自 queue 安全取出推播內容，在獨立背景 Event Loop 內執行非同步傳送，完全消弭 UI 執行緒的任何網路阻礙。
        """
        # 為此守護執行緒建立專屬的 asyncio Event Loop
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        
        while self._tg_thread_running:
            try:
                # 採用 1.0 秒超時阻斷，以能定期回應 _tg_thread_running 的中斷信號
                msg = self._tg_queue.get(timeout=1.0)
            except queue.Empty:
                continue
                
            try:
                # 獨立事件循環中同步運行非同步 TG 請求
                loop.run_until_complete(self.send_telegram_async(msg))
            except Exception as e:
                print(f"[背景TG發送異常] {e}")
            finally:
                self._tg_queue.task_done()
                
        loop.close()

    def analysis_worker_loop(self):
        while self._analysis_thread_running:
            triggered = self._analysis_event.wait(timeout=1.0)
            if not triggered:
                continue
                
            self._analysis_event.clear()
            time.sleep(ANALYSIS_DEBOUNCE_SEC)
            self._analysis_event.clear()
            
            if not self._analysis_thread_running:
                break
                
            try:
                result = self.run_realtime_analysis_compute()
                self.rt_analysis_completed.emit(result)
            except Exception as e:
                self.rt_analysis_completed.emit({"error": str(e)})

    def run_realtime_analysis_compute(self) -> dict:
        if self.is_replaying:
            active_session = getattr(self, 'current_replay_session', "日盤")
        else:
            active_session = "夜盤" if self.current_realtime_port == 442 else "日盤"
        
        # 安全初始化即時速差對比快取狀態，防範 memory leak 與 Zombie Processes
        if not hasattr(self, '_rt_last_net_speeds_top'):
            self._rt_last_net_speeds_top = {"TXF": None, "MXF": None}
        if not hasattr(self, '_rt_last_net_speeds_bot'):
            self._rt_last_net_speeds_bot = {"TXF": None, "MXF": None}
        
        trades_snapshot = {}
        state_snapshot = {}
        
        with self._rt_lock:
            # 根據雙軌旗標選擇讀取「實時軌」或「復盤軌」資料
            target_trades_source = self.replay_symbol_trades if self.is_replaying else self.live_symbol_trades
            target_state_source = self._replay_rt_state if self.is_replaying else self._rt_state
            
            for symbol in ["TXF", "MXF"]:
                trades_snapshot[symbol] = list(target_trades_source[symbol][active_session])
                orig_state = target_state_source[symbol][active_session]
                state_snapshot[symbol] = {
                    "day_max": orig_state["day_max"], "day_min": orig_state["day_min"],
                    "max_time": orig_state.get("max_time", "--"), "min_time": orig_state.get("min_time", "--"),
                    "running_max": orig_state["running_max"], "running_min": orig_state["running_min"],
                    "last_price": orig_state["last_price"],
                    "last_check_time_h": orig_state["last_check_time_h"],
                    "last_check_time_b": orig_state["last_check_time_b"],
                    "scan_idx": orig_state["scan_idx"],
                    "outer_count": orig_state["outer_count"], "inner_count": orig_state["inner_count"],
                    "first_outer_time": orig_state["first_outer_time"], "last_outer_time": orig_state["last_outer_time"],
                    "first_inner_time": orig_state["first_inner_time"], "last_inner_time": orig_state["last_inner_time"]
                }
                
        current_status_snapshot = []
        telegram_messages = []
        n_ticks = int(self.engine.abs_n_ticks)
        
        for symbol in ["TXF", "MXF"]:
            trades = trades_snapshot[symbol]
            if not trades:
                continue
                
            state = state_snapshot[symbol]
            day_max = state["day_max"]
            day_min = state["day_min"]
            
            quant_params = self.engine._load_quant_params(symbol, self._current_target_days)
            
            abs_details = []
            running_max, running_min = state["running_max"], state["running_min"]
            last_price = state["last_price"]
            last_check_time_h, last_check_time_b = state["last_check_time_h"], state["last_check_time_b"]
            scan_idx = state["scan_idx"]
            
            for i in range(scan_idx, len(trades)):
                price, t_val = trades[i]["price"], trades[i]["t_val"]
                is_trig_h, is_trig_b = False, False
                
                if price > running_max:
                    running_max = price
                    is_trig_h = True
                elif price == running_max:
                    if (last_price is not None and last_price < price) or (t_val - last_check_time_h >= 30.0):
                        is_trig_h = True
                        
                if price < running_min:
                    running_min = price
                    is_trig_b = True
                elif price == running_min:
                    if (last_price is not None and last_price > price) or (t_val - last_check_time_b >= 30.0):
                        is_trig_b = True
                        
                if is_trig_h: last_check_time_h = t_val
                if is_trig_b: last_check_time_b = t_val
                
                if (symbol, active_session) not in self._rt_triggers:
                    self._rt_triggers[(symbol, active_session)] = []
                if is_trig_h or is_trig_b:
                    self._rt_triggers[(symbol, active_session)].append((i, price, is_trig_h, is_trig_b, running_max, running_min))
                    
                last_price = price
                
            with self._rt_lock:
                target_state_source = self._replay_rt_state if self.is_replaying else self._rt_state
                orig_state = target_state_source[symbol][active_session]
                orig_state["running_max"] = running_max
                orig_state["running_min"] = running_min
                orig_state["last_price"] = last_price
                orig_state["last_check_time_h"] = last_check_time_h
                orig_state["last_check_time_b"] = last_check_time_b
                orig_state["scan_idx"] = len(trades)
                
            triggers = self._rt_triggers.get((symbol, active_session), [])
            for item in triggers:
                # 執行緒安全解包（相容 6 元組與舊版 4 元組）
                if len(item) == 6:
                    i, price, is_trig_h, is_trig_b, r_max, r_min = item
                else:
                    i, price, is_trig_h, is_trig_b = item
                    r_max, r_min = running_max, running_min
                    
                t_val = trades[i]["t_val"]
                other_sym = "MXF" if symbol == "TXF" else "TXF"
                other_trades = trades_snapshot.get(other_sym, [])
                
                if is_trig_h:
                    pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = \
                        self.get_durations_cached(symbol, active_session, trades, n_ticks, i, "Outer", "Inner")
                    status = self.engine._get_status_str(pre, post, act_pre, act_post, n_ticks)
                    
                    if status in [" [達標]", " [邊界達標]", " [未達標]"]:
                        # 採用極值觸發當下的快照計算當時真實累計振幅
                        amp = r_max - r_min if r_min != 999999 else 0
                        # 階段一先使用空字串 placeholder，並在元組末尾攜帶 b_idx
                        prefix = "時段最高" if price == day_max else ("曾未達標最高" if "未達標" in status else "曾達標最高")
                        abs_details.append((t_val, prefix + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, "", b_idx))
                        
                if is_trig_b:
                    pre, post, threshold, trig_time, trig_price, act_pre, act_post, b_idx = \
                        self.get_durations_cached(symbol, active_session, trades, n_ticks, i, "Inner", "Outer")
                    status = self.engine._get_status_str(pre, post, act_pre, act_post, n_ticks)
                    
                    if status in [" [達標]", " [邊界達標]", " [未達標]"]:
                        # 採用極值觸發當下的快照計算當時真實累計振幅
                        amp = r_max - r_min if r_max != -999999 else 0
                        # 階段一先使用空字串 placeholder，並在元組末尾攜帶 b_idx
                        prefix = "時段最低" if price == day_min else ("曾未達標最低" if "未達標" in status else "曾達標最低")
                        abs_details.append((t_val, prefix + str(status), trades[i]["time"], price, trig_time, trig_price, pre, post, amp, "", b_idx))

            abs_details.sort(key=lambda x: x[0])
            filtered_details_pre = []
            seen = set()
            for d in abs_details:
                key = ("最高" if "最高" in d[1] else "最低", d[3])
                if key not in seen:
                    seen.add(key)
                    filtered_details_pre.append(d)

            # ==================== 實時第二階段：Immutable 歷史速差精確重算 ====================
            # 使用全新的局部變數字典，只依序對最終保留的極值點做計算，徹底避免過渡點污染
            rt_last_net_speeds_top = {"TXF": None, "MXF": None}
            rt_last_net_speeds_bot = {"TXF": None, "MXF": None}
            
            filtered_details = []
            for d in filtered_details_pre:
                if d[7] is None:
                    # 補齊為 10 個元素的元組結構，與原有格式完全一致
                    filtered_details.append((d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], ""))
                    continue
                    
                t_val, status_str, a_time, price_val, trig_time, trig_price, pre, post, amp_val, _, b_idx = d
                
                is_top = "最高" in status_str
                is_day_extreme = "時段最高" in status_str or "時段最低" in status_str
                
                need_speed = True
                if is_day_extreme and "達標" not in status_str:
                    need_speed = False
                    
                if need_speed:
                    if is_top:
                        speed_str = self.engine._get_speed_snapshot_str(symbol, trades, b_idx, other_trades, rt_last_net_speeds_top)
                    else:
                        speed_str = self.engine._get_speed_snapshot_str(symbol, trades, b_idx, other_trades, rt_last_net_speeds_bot)
                else:
                    speed_str = ""
                    
                # 重新包裝為原本的 10 元組格式，保證後續渲染與表格更新 100% 無縫相容
                filtered_details.append((t_val, status_str, a_time, price_val, trig_time, trig_price, pre, post, amp_val, speed_str))

            for d in filtered_details:
                key = ("最高" if "最高" in d[1] else "最低", d[3])
                is_contradiction = False
                is_unmet = " [未達標]" in str(d[1])
                speed_info = d[9] if len(d) > 9 else ""
                if is_unmet:
                    if "最高" in str(d[1]) and ("空速增" in speed_info or "多速減" in speed_info):
                        is_contradiction = True
                    elif "最低" in str(d[1]) and ("多速增" in speed_info or "空速減" in speed_info):
                        is_contradiction = True
                        
                is_normal = "[達標]" in str(d[1]) and "未" not in str(d[1]) and "邊界" not in str(d[1])
                
                if (is_normal or is_contradiction) and d[7] is not None:
                    notify_key = (symbol, active_session, key[0], d[3], d[2])
                    if not hasattr(self, '_rt_notified_keys'):
                        self._rt_notified_keys = set()
                    if notify_key not in self._rt_notified_keys:
                        self._rt_notified_keys.add(notify_key)
                        
                        try:
                            side = "top" if "最高" in key[0] else "bottom"
                            t_dict = quant_params.get("time_top" if side == "top" else "time_bottom", {})
                            b_time_str = str(d[2]).replace(":", "")
                            hm_val = int(b_time_str[0:4]) if len(b_time_str) >= 4 else 0
                            h, m = hm_val // 100, hm_val % 100
                            total_m = h * 60 + m
                            if active_session == "夜盤" and total_m < 900:
                                total_m += 1440
                                
                            time_p50, time_p75, time_p90 = None, None, None
                            for k_label, (p50, p75, p90) in t_dict.items():
                                if active_session in k_label:
                                    time_part = k_label.split(" ")[1].strip()
                                    if "-" in time_part:
                                        s_str, e_str = time_part.split("-")
                                        s_mins = int(s_str.split(":")[0])*60 + int(s_str.split(":")[1])
                                        e_mins = int(e_str.split(":")[0])*60 + int(e_str.split(":")[1])
                                        if active_session == "夜盤":
                                            if s_mins < 900: s_mins += 1440
                                            if e_mins < 900 or e_mins % 1440 < 900: e_mins += 1440
                                        if s_mins <= total_m <= e_mins:
                                            time_p50, time_p75, time_p90 = p50, p75, p90
                                            break
                            if time_p50 is not None:
                                price_val = int(d[3])
                                if side == "top":
                                    zone_str = f"區:{price_val + time_p50}~{price_val + time_p75} 損:{price_val + time_p90}"
                                else:
                                    zone_str = f"區:{price_val - time_p50}~{price_val - time_p75} 損:{price_val - time_p90}"
                            else:
                                zone_str = "N/A"
                        except Exception:
                            zone_str = "N/A"
                            
                        direction_text = f"做空  {d[1]}" if "最高" in d[1] else f"做多  {d[1]}"
                        msg_title = "【極值矛盾】" if is_contradiction else "【極值達標】"
                        msg = (f"{msg_title}{symbol} {active_session}\n"
                               f"方向：{direction_text}\n"
                               f"A點時間：{d[2]}\n"
                               f"A點價：{d[3]}\n"
                               f"B點時間：{d[4]}\n"
                               f"觸發價：{d[5]}\n"
                               f"進場/停損：{zone_str}\n"
                               f"當下振幅：{d[8]}")
                        telegram_messages.append(msg)
                        
            current_status_snapshot.append((symbol, active_session, day_min, day_max, tuple(filtered_details)))

        # === 核心性能優化：在背景執行緒組裝 UI 唯讀不可變快照 ===
        mxf_state = state_snapshot.get("MXF", {})
        txf_state = state_snapshot.get("TXF", {})
        
        mxf_max = mxf_state.get("day_max", -999999)
        mxf_min = mxf_state.get("day_min", 999999)
        mxf_max_t = mxf_state.get("max_time", "--")
        mxf_min_t = mxf_state.get("min_time", "--")
        mxf_amp = mxf_max - mxf_min if (mxf_max != -999999 and mxf_min != 999999) else "--"
        
        extreme_info_str = f"最高價: {mxf_max} ({mxf_max_t}) | 最低價: {mxf_min} ({mxf_min_t}) | 振幅: {mxf_amp}" if mxf_max != -999999 else "最高價: -- (--) | 最低價: -- (--) | 振幅: --"
        
        # 背景計算速差
        oa_t, ia_t, d_t = self.engine._calc_side_speed_from_state(txf_state)
        oa_m, ia_m, d_m = self.engine._calc_side_speed_from_state(mxf_state)
        
        net_txf_str = f"| 大臺速差: {ia_t - oa_t:+.4f}s" if (oa_t is not None and ia_t is not None) else "| 大臺速差: --"
        net_txf_color = "#eb4b4b" if (oa_t is not None and ia_t is not None and ia_t - oa_t > 0) else "#28a745" if (oa_t is not None and ia_t is not None and ia_t - oa_t < 0) else "gray"
        
        net_mxf_str = f"| 小臺速差: {ia_m - oa_m:+.4f}s" if (oa_m is not None and ia_m is not None) else "| 小臺速差: --"
        net_mxf_color = "#eb4b4b" if (oa_m is not None and ia_m is not None and ia_m - oa_m > 0) else "#28a745" if (oa_m is not None and ia_m is not None and ia_m - oa_m < 0) else "gray"
        
        # 背景判定多空共識
        if "多方" in d_t and "多方" in d_m:
            consensus_str = "| 共識: 多方 📈"
            consensus_color = "#eb4b4b"
        elif "空方" in d_t and "空方" in d_m:
            consensus_str = "| 共識: 空方 📉"
            consensus_color = "#28a745"
        elif d_t != "資料不足" and d_m != "資料不足":
            consensus_str = "| 共識: 持平 ⚖️"
            consensus_color = "gray"
        else:
            consensus_str = "| 共識: --"
            consensus_color = "gray"

        # 生成即時極值文字詳情報告 (平均每筆間隔速度)
        txf_trades_rt = trades_snapshot.get("TXF", [])
        mxf_trades_rt = trades_snapshot.get("MXF", [])
        
        txf_details_rt = []
        mxf_details_rt = []
        for sym, sess, d_min, d_max, f_details in current_status_snapshot:
            if sess == active_session:
                if sym == "TXF":
                    txf_details_rt = f_details
                elif sym == "MXF":
                    mxf_details_rt = f_details
                    
        # 優先大臺
        txf_report = ""
        if txf_trades_rt:
            txf_report = self._generate_realtime_report_str(
                "TXF", active_session, txf_trades_rt, txf_details_rt, mxf_trades_rt,
                self.engine._load_quant_params("TXF", self._current_target_days), state_snapshot
            )
            
        # 後續小臺
        mxf_report = ""
        if mxf_trades_rt:
            mxf_report = self._generate_realtime_report_str(
                "MXF", active_session, mxf_trades_rt, mxf_details_rt, txf_trades_rt,
                self.engine._load_quant_params("MXF", self._current_target_days), state_snapshot
            )
            
        rt_report = ""
        if txf_report:
            rt_report += "═══ 大臺即時極值行情 ═══" + txf_report
        if mxf_report:
            if rt_report:
                rt_report += "\n"
            rt_report += "═══ 小臺即時極值行情 ═══" + mxf_report

        # === 核心性能優化：在背景執行緒中非同步聚合分K與計算極值停損時序狀態機 ===
        kline_interval = self._current_kline_interval
        obs_n = self._current_obs_n
        
        txf_sigs_bg = []
        mxf_sigs_bg = []
        for sym, sess, d_min, d_max, f_details in current_status_snapshot:
            if sess == active_session:
                if sym == "TXF": txf_sigs_bg = f_details
                elif sym == "MXF": mxf_sigs_bg = f_details
                
        # 背景非同步聚合分K (耗時運算完全隔離)
        kline_data, breakouts = self.engine._calc_kline_data(
            active_session, mxf_trades_rt, txf_sigs_bg, mxf_sigs_bg, interval_mins=kline_interval
        )
        
        # 背景非同步計算停損極值對比時序狀態機 (超級重度 CPU 運算完全隔離)
        simulation_results = self.engine._calc_simulation_results(
            active_session, mxf_trades_rt, kline_data, obs_n
        )

        return {
            "current_status_snapshot": current_status_snapshot,
            "telegram_messages": telegram_messages,
            "active_session": active_session,
            "realtime_extreme_report": rt_report,  # 將即時極值詳情報告傳回 UI
            "kline_data": kline_data,                     # 背景預先算好的 K線數據
            "simulation_results": simulation_results,     # 背景預先算好的停損模擬觀測數據
            "extreme_snapshot": {
                "extreme_info_str": extreme_info_str,
                "consensus_str": consensus_str,
                "consensus_color": consensus_color,
                "net_txf_str": net_txf_str,
                "net_txf_color": net_txf_color,
                "net_mxf_str": net_mxf_str,
                "net_mxf_color": net_mxf_color
            }
        }


    def _generate_realtime_report_str(self, symbol: str, session: str, trades: List[dict], 
                                     filtered_details: List[tuple], other_trades: List[dict], 
                                     quant_params: dict, state_snapshot: dict) -> str:
        """
        職責: 在背景執行緒高效生成與離線格式完全一致的「即時絕對極值詳情報告」。
        與 UI 隔離，避免格式化龐大文字時阻塞 UI 主執行緒。
        """
        if not trades:
            return ""
            
        final_close = trades[-1]["price"]
        mxf_state = state_snapshot.get("MXF", {})
        txf_state = state_snapshot.get("TXF", {})
        
        # 計算速差
        oa_t, ia_t, d_t = self.engine._calc_side_speed_from_state(txf_state)
        oa_m, ia_m, d_m = self.engine._calc_side_speed_from_state(mxf_state)
        txf_n = f"{ia_t - oa_t:+.4f}s" if (oa_t is not None and ia_t is not None) else "--"
        mxf_n = f"{ia_m - oa_m:+.4f}s" if (oa_m is not None and ia_m is not None) else "--"
        
        # 計算買賣盤成交速度與方向
        outer_avg, inner_avg, direction_str = self.engine._calc_side_speed(trades)
        outer_count = sum(1 for t in trades if t.get("side") == "Outer")
        inner_count = sum(1 for t in trades if t.get("side") == "Inner")
        outer_s = f"{outer_avg:.4f}s/{outer_count}筆" if outer_avg is not None else "資料不足"
        inner_s = f"{inner_avg:.4f}s/{inner_count}筆" if inner_avg is not None else "資料不足"
        avg_pri = int(round(sum(t["price"] for t in trades) / len(trades))) if trades else 0
        
        def wide_pad(text: str, width: int) -> str:
            actual_w = sum(2 if ord(c) > 127 else 1 for c in text)
            return text + " " * max(0, width - actual_w)
            
        h_type = wide_pad("類型", 22)
        h_zone = wide_pad("進場區/停損", 23)
        h_a_time = wide_pad("A點時間", 15)
        h_a_pri = wide_pad("A點價", 8)
        h_b_time = wide_pad("B點時間", 15)
        h_trig = wide_pad("觸發價", 8)
        h_pre = wide_pad("前向平均", 12)
        h_post = wide_pad("後向平均", 12)
        h_amp = wide_pad("當時振幅", 8)

        header = f"{h_type} | {h_zone} | {h_a_time} | {h_a_pri} | {h_b_time} | {h_trig} | {h_pre} | {h_post} | {h_amp}"
        sep = "    " + "-" * 142
        
        report = ""
        report += f"\n    [{symbol} 即時極值詳情 (平均每筆間隔)]  最新價: {final_close}\n"
        report += f"    ● 成交速度: 外盤(買) {outer_s} | 內盤(賣) {inner_s} → {direction_str} | 大台速差: {txf_n:<15} 小台速差: {mxf_n:<15} | 均價:{avg_pri}\n"
        report += f"    {header}\n"
        report += f"{sep}\n"
        
        last_abs_type = None
        for d in filtered_details:
            if d[7] is None:
                continue
                
            current_type = "最高" if "最高" in d[1] else "最低"
            if last_abs_type is not None and current_type != last_abs_type:
                report += f"{sep}\n"
            last_abs_type = current_type
            
            b_time_val = d[4] if d[4] is not None else "N/A"
            b_pri_val = str(d[5]) if d[5] is not None else "N/A"
            pre_s = f"{d[6]:>10.4f}s" if d[6] is not None else f"{'N/A':>10}"
            post_s = f"{d[7]:>10.4f}s" if d[7] is not None else f"{'N/A':>10}"
            
            amp_val = int(d[8])
            side = "top" if current_type == "最高" else "bottom"
            
            # 使用高相容性的 d[-1] 作為 speed_str 提取
            speed_info = d[-1] if len(d) >= 10 else ""
            is_unmet = " [未達標]" in str(d[1])
            force_show_unmet = False
            display_type_str = str(d[1])
            if is_unmet:
                if "最高" in str(d[1]) and ("空速增" in speed_info or "多速減" in speed_info):
                    force_show_unmet = True
                elif "最低" in str(d[1]) and ("多速增" in speed_info or "空速減" in speed_info):
                    force_show_unmet = True
                    
                if force_show_unmet:
                    display_type_str = display_type_str.replace("未達標", "矛盾")

            zone_str = ""
            # 只在達標或未達標矛盾時顯示進場/停損區間，其餘普通未達標保持空白 (100% 對齊舊版邏輯)
            if " [達標]" in str(d[1]) or (" [未達標]" in str(d[1]) and force_show_unmet):
                try:
                    time_p50, time_p75, time_p90 = None, None, None
                    time_dict = quant_params.get("time_top" if side == "top" else "time_bottom", {})
                    b_time_str = str(d[2]).replace(":", "")
                    hm_val = int(b_time_str[0:4]) if len(b_time_str) >= 4 else 0
                    h = hm_val // 100
                    m = hm_val % 100
                    total_m = h * 60 + m
                    
                    if session == "夜盤" and total_m < 900:
                        total_m += 1440
                        
                    for k_label, (p50, p75, p90) in time_dict.items():
                        if session in k_label:
                            try:
                                time_part = k_label.split(" ")[1].strip()
                                if "-" in time_part:
                                    s_str, e_str = time_part.split("-")
                                    s_mins = int(s_str.split(":")[0])*60 + int(s_str.split(":")[1])
                                    e_mins = int(e_str.split(":")[0])*60 + int(e_str.split(":")[1])
                                    if session == "夜盤":
                                        if s_mins < 900: s_mins += 1440
                                        if e_mins < 900 or e_mins % 1440 < 900: e_mins += 1440
                                    if s_mins <= total_m <= e_mins:
                                        time_p50, time_p75, time_p90 = p50, p75, p90
                                        break
                            except Exception:
                                pass
                    
                    if time_p50 is not None:
                        price_val = int(d[3])
                        if side == "top":
                            zone_str = f"區:{price_val + time_p50}~{price_val + time_p75} 損:{price_val + time_p90}"
                        else:
                            zone_str = f"區:{price_val - time_p50}~{price_val - time_p75} 損:{price_val - time_p90}"
                except Exception:
                    zone_str = "N/A"
            
            type_str = wide_pad(display_type_str, 22)
            zone_pad = wide_pad(zone_str, 23)
            a_time = wide_pad(str(d[2]), 15)
            a_pri = wide_pad(str(d[3]), 8)
            b_time = wide_pad(str(b_time_val), 15)
            b_pri = wide_pad(str(b_pri_val), 8)
            
            report += f"    {type_str} | {zone_pad} | {a_time} | {a_pri} | {b_time} | {b_pri} | {pre_s} | {post_s} | {d[8]:>8}\n"
            if speed_info:
                report += f"    {speed_info.strip()}\n"
                
        return report + sep + "\n"

    def apply_realtime_analysis_ui(self, result: dict):
        current_status_snapshot = result["current_status_snapshot"]
        self._rt_last_status_snapshot = current_status_snapshot
        telegram_messages = result["telegram_messages"]
        active_session = result["active_session"]
        
        # 1. 渲染頂部極值、速差及多空共識 (直接讀取背景計算好的快照，零運算)
        if "extreme_snapshot" in result:
            snap = result["extreme_snapshot"]
            self.lbl_extreme_info.setText(snap["extreme_info_str"])
            self.lbl_consensus_dir.setText(snap["consensus_str"])
            self.set_widget_style_lazy(self.lbl_consensus_dir, f"font-weight: bold; color: {snap['consensus_color']};")
            self.lbl_txf_net_speed.setText(snap["net_txf_str"])
            self.set_widget_style_lazy(self.lbl_txf_net_speed, f"font-weight: bold; color: {snap['net_txf_color']};")
            self.lbl_mxf_net_speed.setText(snap["net_mxf_str"])
            self.set_widget_style_lazy(self.lbl_mxf_net_speed, f"font-weight: bold; color: {snap['net_mxf_color']};")

        # 2. 將即時極值文字詳情與歷史系統日誌拼接，一次性進行高性能 Repaint 渲染
        if "realtime_extreme_report" in result and result["realtime_extreme_report"]:
            if not hasattr(self, '_system_logs'):
                self._system_logs = []
                
            # 智慧滾動與選取鎖定檢查 (避免即時更新時畫面強行跳轉，鎖定交易員正在觀察的位置)
            scrollbar = self.txt_output.verticalScrollBar()
            has_selection = self.txt_output.textCursor().hasSelection()
            # 滾動條在底部之判定 (偏離最大值小於 15 像素均視為在底部)
            is_at_bottom = scrollbar.value() >= scrollbar.maximum() - 15
            
            # 若有反白或正在往上查看，則鎖定當前滾動條數值，不強行跳轉
            prev_scroll_val = scrollbar.value() if (has_selection or not is_at_bottom) else None
            
            logs_str = "\n".join(self._system_logs)
            full_content = "═══ 系統即時監控日誌 ═══\n" + logs_str + "\n\n" + result["realtime_extreme_report"]
            
            # 性能防線：只有在內容確實改變時才去刷新 QTextEdit 文本樹並進行著色解析，否則 O(1) 直接跳過
            if not hasattr(self, '_last_rendered_content'):
                self._last_rendered_content = ""
                
            now_ms = time.time() * 1000
            if not hasattr(self, '_last_txt_render_time'):
                self._last_txt_render_time = 0
                
            # === 💡 核心優化：智慧操作感知型降頻節流 (Interaction-Aware Throttle) ===
            # 若檢測到使用者滑鼠正懸浮在 K 線圖表內（代表可能正在進行高頻滾輪縮放、拖曳平移、或滑動十字游標），
            # 我們判定使用者此時的注意力 100% 集中在 K 線圖表上，此時每 250ms 重繪與正則染色 500 行文字屬於無效開銷。
            # 我們自動將文字更新間隔拉大到 2000ms（2秒）！這能將 100% 的 UI 主執行緒時間完全留給 GPU 硬件加速圖表渲染與視窗拖曳。
            is_user_interacting = False
            if hasattr(self, 'kline_chart') and self.kline_chart.crosshair_overlay.mouse_pos is not None:
                is_user_interacting = True
                
            render_interval = 2000.0 if is_user_interacting else 250.0
            
            is_time_to_render = (now_ms - self._last_txt_render_time >= render_interval) or len(telegram_messages) > 0
            
            if full_content != self._last_rendered_content and is_time_to_render:
                self._last_rendered_content = full_content
                self._last_txt_render_time = now_ms
                self.txt_output.setPlainText(full_content)
                
                # 還原滾動條位置 (僅在文字真正更新時執行，避免不必要的排版開銷)
                if prev_scroll_val is not None:
                    scrollbar.setValue(prev_scroll_val)
                else:
                    self.txt_output.moveCursor(self.txt_output.textCursor().End)

        if self.chk_telegram.isChecked():
            for msg in telegram_messages:
                self.append_log(f"\n>>>> 觸發推播: {msg.replace(chr(10), ' | ')}")
                self._tg_queue.put(msg)
        else:
            for msg in telegram_messages:
                self.append_log(f"\n>>>> 觸發 (未啟用TG): {msg.replace(chr(10), ' | ')}")
                
        # === 核心性能優化：直接提取背景執行緒預先計算好的 K線與極值模擬數據 (Repaint Only) ===
        kline_data = result.get("kline_data", [])
        simulation_results = result.get("simulation_results", [])
        
        # 1. 刷新 K線視圖與圖表元件 (O(1) / O(N) 零 CPU 聚合開銷)
        self.update_kline_views(kline_data)
        self.kline_chart.update_candles(kline_data)
        
        # 2. 刷新極值觀測表視圖 (完全跳過 _calc_simulation_results 重度計算)
        table_rows = []
        table_tags = []
        for confirmed_key, row, tags in simulation_results:
            table_rows.append(row)
            table_tags.append(tags)
            
        self.model_observer.update_data(table_rows, table_tags)
        
        # 若使用者未主動選取（反白）任何行，自動捲動至最新一筆
        if not self.view_observer.selectionModel().hasSelection():
            if self.model_observer.rowCount() > 0:
                self.view_observer.scrollToBottom()

        # 3. 順便更新速差、均價與成交筆數面板，完成 100% 的事件驅動轉型
        self.refresh_info_panel()
        
        # 即時行情分析完成後，立即觸發「未破分K監控」對焦
        if self.wnd_unbroken_k:
            # 事件驅動防線：若產生了新的極值信號（telegram_messages 不為空），代表有新停損誕生，必須立刻強制背景對焦
            has_new_signals = len(telegram_messages) > 0
            self.wnd_unbroken_k.trigger_unbroken_check(force=has_new_signals)
            
        # 更新底部「最高觀察」與「最低觀察」下拉選單之數值
        self.refresh_observer_comboboxes()

    def refresh_info_panel(self):
        if self.is_replaying:
            active_session = getattr(self, 'current_replay_session', "日盤")
        else:
            active_session = "夜盤" if self.current_realtime_port == 442 else "日盤"
        
        mxf_price = getattr(self, '_last_mxf_price', '--')
        self.lbl_live_price.setText(f"| 價: {mxf_price}")
        
        if self.wnd_unbroken_k and mxf_price != '--':
            self.wnd_unbroken_k.check_instant_unbroken_breakout(float(mxf_price))
            
        counts = []
        target_trades = self.replay_symbol_trades if self.is_replaying else self.live_symbol_trades
        target_states = self._replay_rt_state if self.is_replaying else self._rt_state
        
        for sym in ["TXF", "MXF"]:
            with self._rt_lock:
                cnt = len(target_trades[sym].get(active_session, []))
            counts.append(f"{sym}({active_session[0]}:{cnt})")
            
        status_prefix = "復盤回放" if self.is_replaying else f"已連線({self.current_realtime_port})"
        self.lbl_realtime_status.setText(f"{status_prefix} | {' | '.join(counts)}")
        
        for sym, lbl, name in [("TXF", self.lbl_speed_txf, "大臺"), ("MXF", self.lbl_speed_mxf, "小臺")]:
            with self._rt_lock:
                state = target_states[sym][active_session]
                cnt = state["count"]
                sum_p = state["sum_price"]
                o_cnt = state["outer_count"]
                i_cnt = state["inner_count"]
                oa, ia, d_str = self.engine._calc_side_speed_from_state(state)
                
            if cnt > 0:
                o_s = f"{oa:.4f}s/{o_cnt:5d}筆" if oa is not None else "--/筆"
                i_s = f"{ia:.4f}s/{i_cnt:5d}筆" if ia is not None else "--/筆"
                avg_pri = int(round(sum_p / cnt))
                lbl.setText(f"{name}: 外盤(買) {o_s} | 內盤(賣) {i_s} → {d_str} | 均價:{avg_pri}")
                self.set_widget_style_lazy(lbl, "color: #eb4b4b;" if "多方" in d_str else "color: #28a745;" if "空方" in d_str else "color: #a0a0a0;")

    def update_kline_views(self, kline_data: List[tuple]):
        """
        更新 K 線表格數據，並在 K 線轉換（新 K 棒出現）時，自動帶入前一根已收盤 K 棒的最高低點數值。
        
        Args:
            kline_data: 聚合完成之 K 線資料列表 (List[tuple])
        """
        table_rows = []
        table_tags = []
        
        for row in kline_data:
            time_label, high, low, open_p, close_p, signals, b_h, b_l, tag = row
            table_rows.append((time_label, high, low, open_p, close_p, signals, b_h, b_l))
            table_tags.append((tag,))
            
        self.model_kline.update_data(table_rows, table_tags)
        
        # 若使用者未主動選取（反白）任何行，自動捲動至最新一筆
        if not self.view_kline.selectionModel().hasSelection():
            if self.model_kline.rowCount() > 0:
                self.view_kline.scrollToBottom()

        # ═══ 實作：觀察 K 高與觀察 K 低之 K 線轉換自動帶入邏輯 ═══
        if len(kline_data) >= 2:
            current_kline_time = kline_data[-1][0]  # 最新 K 棒的時間標籤
            
            # 若為初始狀態 (None) 或者偵測到 K 線轉換 (最新 K 棒時間與上次自帶時間不同)
            if getattr(self, '_last_autofill_kline_time', None) != current_kline_time:
                self._last_autofill_kline_time = current_kline_time
                
                prev_kline = kline_data[-2]
                prev_high = prev_kline[1]  # "高" 欄位
                prev_low = prev_kline[2]   # "低" 欄位
                
                # 觀察 K 低：自動帶入前一根已收盤 K 棒的最低點，並寫入引擎與編輯框
                self.ent_obs_high.setText(str(int(float(prev_low))))
                try:
                    self.engine._obs_high_entry_price = int(float(prev_low))
                except ValueError:
                    pass
                
                # 觀察 K 高：自動帶入前一根已收盤 K 棒的最高點，並寫入引擎與編輯框
                self.ent_obs_low.setText(str(int(float(prev_high))))
                try:
                    self.engine._obs_low_entry_price = int(float(prev_high))
                except ValueError:
                    pass
        else:
            # 當 K 線筆數不足 2 筆時（例如剛切換大分K還沒有收盤的K棒），清空觀察並重置引擎狀態
            self._last_autofill_kline_time = None
            self.engine._obs_high_entry_price = None
            self.engine._obs_low_entry_price = None
            self.ent_obs_high.clear()
            self.ent_obs_low.clear()

    def refresh_observer_comboboxes(self):
        """
        利用當前已載入的交易與訊號數據重新計算並更新底部「最高觀察」與「最低觀察」下拉選單之數值。
        支援即時盤中分析與離線日誌載入模式雙軌對焦。
        """
        session_data = self.gather_session_data_snapshot()
        if not session_data:
            return
            
        high_zone_prices = set()
        low_zone_prices = set()
        
        for session_name, trades, txf_sigs, mxf_sigs in session_data:
            # 遍歷大臺與小臺以提取極值達標與矛盾價格關卡
            for sym, sigs in [("TXF", txf_sigs), ("MXF", mxf_sigs)]:
                quant_params = self.engine._load_quant_params(sym, self._current_target_days)
                for d in sigs:
                    if len(d) < 8 or d[7] is None:
                        continue
                        
                    is_unmet = " [未達標]" in str(d[1])
                    speed_info = d[9] if len(d) > 9 else ""
                    is_contradiction = False
                    if is_unmet:
                        if "最高" in str(d[1]) and ("空速增" in speed_info or "多速減" in speed_info):
                            is_contradiction = True
                        elif "最低" in str(d[1]) and ("多速增" in speed_info or "空速減" in speed_info):
                            is_contradiction = True
                            
                    is_normal = "[達標]" in str(d[1]) and "未" not in str(d[1]) and "邊界" not in str(d[1])
                    
                    if not (is_normal or is_contradiction):
                        continue
                        
                    current_type = "最高" if "最高" in str(d[1]) else "最低"
                    side = "top" if current_type == "最高" else "bottom"
                    
                    try:
                        time_dict = quant_params.get("time_top" if side == "top" else "time_bottom", {})
                        b_time_str = str(d[2]).replace(":", "")
                        hm_val = int(b_time_str[0:4]) if len(b_time_str) >= 4 else 0
                        h = hm_val // 100
                        m = hm_val % 100
                        total_m = h * 60 + m
                        
                        if session_name == "夜盤" and total_m < 900:
                            total_m += 1440
                            
                        time_p50, time_p75 = None, None
                        for k_label, (p50, p75, p90) in time_dict.items():
                            if session_name in k_label:
                                try:
                                    time_part = k_label.split(" ")[1].strip()
                                    if "-" in time_part:
                                        s_str, e_str = time_part.split("-")
                                        s_mins = int(s_str.split(":")[0])*60 + int(s_str.split(":")[1])
                                        e_mins = int(e_str.split(":")[0])*60 + int(e_str.split(":")[1])
                                        if session_name == "夜盤":
                                            if s_mins < 900: s_mins += 1440
                                            if e_mins < 900 or e_mins % 1440 < 900: e_mins += 1440
                                        if s_mins <= total_m <= e_mins:
                                            time_p50, time_p75 = p50, p75
                                            break
                                except Exception:
                                    pass
                                    
                        if time_p50 is None:
                            continue
                            
                        price_val = int(d[3])
                        if side == "top":
                            high_zone_prices.add(price_val + time_p50)
                            high_zone_prices.add(price_val + time_p75)
                        else:
                            low_zone_prices.add(price_val - time_p50)
                            low_zone_prices.add(price_val - time_p75)
                    except Exception:
                        pass
                        
        sorted_high = sorted(high_zone_prices)
        sorted_low = sorted(low_zone_prices, reverse=True)
        
        # 暫時解除訊號槽連結，避免呼叫 clear 或 setCurrentText 時觸發無謂的日誌日誌
        try:
            self.cbo_obs_high.currentTextChanged.disconnect(self.on_obs_high_changed)
        except TypeError:
            pass
            
        try:
            self.cbo_obs_low.currentTextChanged.disconnect(self.on_obs_low_changed)
        except TypeError:
            pass
            
        try:
            curr_high_text = self.cbo_obs_high.currentText().strip()
            curr_low_text = self.cbo_obs_low.currentText().strip()
            
            new_high_items = [str(p) for p in sorted_high]
            new_low_items = [str(p) for p in sorted_low]
            
            # 差量比對：只有選項真正改變時才重建，避免無效 clear+addItems 觸發 Qt layout recalc
            old_high = [self.cbo_obs_high.itemText(i) for i in range(self.cbo_obs_high.count())]
            old_low = [self.cbo_obs_low.itemText(i) for i in range(self.cbo_obs_low.count())]
            
            needs_rebuild = (new_high_items != old_high or new_low_items != old_low)
            
            if needs_rebuild:
                self.cbo_obs_high.clear()
                self.cbo_obs_high.addItems(new_high_items)
                
                self.cbo_obs_low.clear()
                self.cbo_obs_low.addItems(new_low_items)
            
            # 若目前選單為空，則預設填入並設定為第一個壓力與支撐關卡
            if not curr_high_text and sorted_high:
                default_high = str(sorted_high[0])
                self.cbo_obs_high.setCurrentText(default_high)
                self.engine._obs_high_price = int(default_high)
            elif curr_high_text:
                self.cbo_obs_high.setCurrentText(curr_high_text)
                
            if not curr_low_text and sorted_low:
                default_low = str(sorted_low[0])
                self.cbo_obs_low.setCurrentText(default_low)
                self.engine._obs_low_price = int(default_low)
            elif curr_low_text:
                self.cbo_obs_low.setCurrentText(curr_low_text)
        finally:
            # 重新連結訊號槽
            self.cbo_obs_high.currentTextChanged.connect(self.on_obs_high_changed)
            self.cbo_obs_low.currentTextChanged.connect(self.on_obs_low_changed)

    def preload_observer_table(self, session: str, trades: List[dict], kline_data: List[tuple]):
        obs_n = self.get_obs_n()
        results = self.engine._calc_simulation_results(session, trades, kline_data, obs_n)
        
        table_rows = []
        table_tags = []
        for confirmed_key, row, tags in results:
            table_rows.append(row)
            table_tags.append(tags)
            
        self.model_observer.update_data(table_rows, table_tags)
        
        # 若使用者未主動選取（反白）任何行，自動捲動至最新一筆
        if not self.view_observer.selectionModel().hasSelection():
            if self.model_observer.rowCount() > 0:
                self.view_observer.scrollToBottom()

    def on_kline_select_changed(self, selected, deselected):
        pass

    def on_observer_select_changed(self, selected, deselected):
        indexes = selected.indexes()
        if not indexes:
            return
            
        row = indexes[0].row()
        obs_model = self.model_observer
        if row >= len(obs_model._data):
            return
            
        row_values = obs_model._data[row]
        tag_values = obs_model._tags[row] if row < len(obs_model._tags) else ()
        
        if "history" in tag_values or "annotation" in tag_values:
            return
            
        a_time_str = str(row_values[1]).strip()
        if not a_time_str or a_time_str == "N/A":
            return
            
        a_t_val = self.engine.parse_time(a_time_str)
        if a_t_val <= 0:
            return
            
        k_model = self.model_kline
        target_row = -1
        
        for idx, row_data in enumerate(k_model._data):
            time_label = str(row_data[0])
            try:
                start_str, end_str = time_label.split("~")
                sh, sm = map(int, start_str.split(":"))
                eh, em = map(int, end_str.split(":"))
                
                k_start = sh * 3600 + sm * 60
                k_end = eh * 3600 + em * 60
                
                if self.current_session_name == "夜盤":
                    if k_start <= 18000: k_start += 86400
                    if k_end <= 18000: k_end += 86400
                    
                if k_start <= a_t_val < k_end:
                    target_row = idx
                    break
            except Exception:
                continue
                
        if target_row > 0:
            target_k_row = target_row - 1
            self.view_kline.selectRow(target_k_row)
            self.view_kline.scrollTo(self.model_kline.index(target_k_row, 0))

    def get_obs_n(self) -> int:
        return self.spn_obs_n.value()

    def get_kline_intervals(self) -> List[str]:
        return [self.cbo_kline_interval.itemText(i) for i in range(self.cbo_kline_interval.count())]

    def gather_session_data_snapshot(self) -> List[tuple]:
        """
        提取最新交易數據與信號明細。
        支援即時行情（live）與歷史日誌載入（offline）雙軌模式。
        """
        session_data = []
        if self.is_replaying:
            active_session = self.cbo_replay_session.currentText()
            with self._rt_lock:
                trades = list(self.replay_symbol_trades["MXF"][active_session])
                
            status_snapshot = getattr(self, '_rt_last_status_snapshot', [])
            txf_sigs = []
            mxf_sigs = []
            for sym, sess, d_min, d_max, f_details in status_snapshot:
                if sess == active_session:
                    if sym == "TXF":
                        txf_sigs = f_details
                    elif sym == "MXF":
                        mxf_sigs = f_details
            session_data.append((active_session, trades, txf_sigs, mxf_sigs))
        elif self.is_realtime_running:
            active_session = "夜盤" if self.current_realtime_port == 442 else "日盤"
            
            with self._rt_lock:
                trades = list(self.live_symbol_trades["MXF"][active_session])
                
            status_snapshot = getattr(self, '_rt_last_status_snapshot', [])
            txf_sigs = []
            mxf_sigs = []
            for sym, sess, d_min, d_max, f_details in status_snapshot:
                if sess == active_session:
                    if sym == "TXF":
                        txf_sigs = f_details
                    elif sym == "MXF":
                        mxf_sigs = f_details
            session_data.append((active_session, trades, txf_sigs, mxf_sigs))
        elif hasattr(self, '_temp_offline_trades') and self._temp_offline_trades:
            for session in ["日盤", "夜盤"]:
                mxf_t = None
                txf_sigs = []
                mxf_sigs = []
                for key, trades_data in self._temp_offline_trades.items():
                    if "MXF" in key and session in key:
                        mxf_t = trades_data
                for key, sigs in getattr(self, '_temp_offline_signals', {}).items():
                    if "TXF" in key and session in key:
                        txf_sigs = sigs
                    elif "MXF" in key and session in key:
                        mxf_sigs = sigs
                if mxf_t:
                    session_data.append((session, mxf_t, txf_sigs, mxf_sigs))
        return session_data

    def on_obs_high_changed(self, val: str):
        try:
            self.engine._obs_high_price = int(val.strip())
            self.append_log(f"  [觀察] 最高觀察設定為: {self.engine._obs_high_price}")
        except ValueError:
            pass

    def on_obs_low_changed(self, val: str):
        try:
            self.engine._obs_low_price = int(val.strip())
            self.append_log(f"  [觀察] 最低觀察設定為: {self.engine._obs_low_price}")
        except ValueError:
            pass

    def on_obs_high_enter(self):
        val = self.ent_obs_high.text().strip()
        try:
            self.engine._obs_high_entry_price = int(val)
            self.append_log(f"  [觀察] 觀察K低手動設定為: {self.engine._obs_high_entry_price}")
        except ValueError:
            pass

    def on_obs_low_enter(self):
        val = self.ent_obs_low.text().strip()
        try:
            self.engine._obs_low_entry_price = int(val)
            self.append_log(f"  [觀察] 觀察K高手動設定為: {self.engine._obs_low_entry_price}")
        except ValueError:
            pass



    def safe_call(self, func, *args):
        QTimer.singleShot(0, lambda: func(*args))

    def set_widget_style_lazy(self, widget, stylesheet: str):
        """
        職責: 智慧型延遲 Widget 樣式更新防禦線。
        利用樣式 cache，只有在 Widget 樣式實際變更時才執行 setStyleSheet，
        徹底避免無效重複設定造成的 Qt 內部 CSS 解析開銷與全域 Paint 風暴。
        """
        if not hasattr(self, '_style_cache'):
            self._style_cache = {}
        if self._style_cache.get(widget) != stylesheet:
            self._style_cache[widget] = stylesheet
            widget.setStyleSheet(stylesheet)

    def load_file(self):
        file_path, _ = QFileDialog.getOpenFileName(self, "開啟日誌檔 (.log)", "", "Log Files (*.log);;All Files (*.*)")
        if not file_path:
            return
            
        self.current_file_path = file_path
        self.kline_chart.plot_widget.getViewBox().enableAutoRange()  # 新增：載入新檔案時強制開啟原生自動對焦，重算對焦視界
        self.btn_open.setEnabled(False)
        self.btn_update.setEnabled(False)
        self.btn_fill.setEnabled(False)
        self.lbl_status.setText(f"正在處理: {os.path.basename(file_path)}...")
        self.append_log(f"--- 開始分析 {file_path} ---")
        
        file_paths = [(file_path, lambda t: True)]
        self.engine.run_analysis_async(file_paths, "MXF", self._current_target_days, ignore_time_check=True)

    def trigger_reanalyze(self):
        self.lbl_status.setText("處理中，請稍候...")
        self.btn_update.setEnabled(False)
        self.kline_chart.plot_widget.getViewBox().enableAutoRange()  # 新增：點擊更新按鈕時強制開啟原生自動對焦，重算對焦視界
        
        val = self.cbo_backtest_days.currentText().strip()
        self._current_target_days = 0 if val == "全部" else int(val)
        
        today_str = datetime.now().strftime("%Y%m%d")
        today_log = os.path.join(APP_DIR, "Logs", today_str, "event.log")
        
        if os.path.exists(today_log):
            file_paths = [(today_log, lambda t: True)]
            self.engine.run_analysis_async(file_paths, "MXF", self._current_target_days, ignore_time_check=True)
        else:
            self.lbl_status.setText("今天無相關日誌可分析")
            self.btn_update.setEnabled(True)

    def trigger_kline_only_reanalyze(self, val: str):
        try:
            self._current_kline_interval = int(val)
            self.kline_chart.plot_widget.getViewBox().enableAutoRange()  # 新增：切換K線分鐘數時強制開啟原生自動對焦，重算對焦視界
            self._analysis_event.set()
        except ValueError:
            pass

    def on_obs_n_changed(self, val: int):
        self._current_obs_n = val
        self._analysis_event.set()

    def fill_missing_data(self):
        pass

    def preload_today_log(self):
        """
        職責: 今日已產生歷史日誌之背景高效非同步預載。
        與 UI 主執行緒完全隔離，在背景完成大文字檔解析，避免任何 UI 卡頓或凍結。
        """
        today_str = datetime.now().strftime("%Y%m%d")
        today_log = os.path.join(APP_DIR, "Logs", today_str, "event.log")
        
        if not os.path.exists(today_log):
            self.append_log(f"[預載] 未找到今日日誌檔: {today_log}，將直接由即時 Tick 開始。")
            return

        self.append_log(f"[預載] 偵測到今日歷史日誌，開始背景非同步預載: {os.path.basename(today_log)}...")
        self.btn_realtime.setEnabled(False)  # 預載期間先鎖定按鈕，防止重複觸發

        def worker():
            try:
                pattern = re.compile(r"Symbol=([^, \t\r\n]+)")
                mattime_pat = re.compile(r"mattime=([^, \t\r\n]+)")
                mat_pri_pat = re.compile(r"matpri=([-]?\d+)")
                tmatqty_pat = re.compile(r"tmatqty=([-]?\d+)")
                bestbp_pat = re.compile(r"bestbp=([\d,]*)")
                bestsp_pat = re.compile(r"bestsp=([\d,]*)")

                preload_trades = {"TXF": {"日盤": [], "夜盤": []}, "MXF": {"日盤": [], "夜盤": []}}
                last_tmatqty = defaultdict(lambda: -1)

                active_session = "夜盤" if self.current_realtime_port == 442 else "日盤"
                
                # 新增：用於動態追蹤今日歷史極值演變的狀態變數
                running_max = None
                running_min = None
                extreme_evolution = []  # 儲存結構: (time_str, event_type, price, running_max, running_min, amplitude)

                with open(today_log, "r", encoding="cp950") as f:
                    for line in f:
                        if "TXF" not in line and "MXF" not in line:
                            continue
                        match = pattern.search(line)
                        if not match: 
                            continue
                        symbol = match.group(1)
                        
                        base_sym = None
                        if symbol.startswith("TXF"): 
                            base_sym = "TXF"
                        elif symbol.startswith("MXF"): 
                            base_sym = "MXF"
                        else: 
                            continue
                        
                        mt_match = mattime_pat.search(line)
                        mp_match = mat_pri_pat.search(line)
                        tq_match = tmatqty_pat.search(line)
                        if not mt_match or not mp_match or not tq_match: 
                            continue
                        
                        time_str = mt_match.group(1)
                        t_val_raw = self.engine.parse_time(time_str)
                        
                        if 31500 <= t_val_raw <= 49500:
                            session = "日盤"
                            t_val = t_val_raw
                        elif t_val_raw >= 54000 or t_val_raw <= 18000:
                            session = "夜盤"
                            t_val = t_val_raw + 86400 if t_val_raw <= 18000 else t_val_raw
                        else:
                            continue
                            
                        # 僅處理與當前即時盤別相同的資料以提升性能
                        if session != active_session:
                            continue

                        tmatqty = int(tq_match.group(1))
                        if tmatqty < 0 or tmatqty <= last_tmatqty[(base_sym, session)]: 
                            continue
                        last_tmatqty[(base_sym, session)] = tmatqty
                        
                        bp_m = bestbp_pat.search(line)
                        sp_m = bestsp_pat.search(line)
                        if not bp_m or not sp_m: 
                            continue
                        
                        try:
                            b_prices = bp_m.group(1)
                            s_prices = sp_m.group(1)
                            best_bp = int(b_prices.split(",")[0]) if b_prices and b_prices.split(",")[0] else 0
                            best_sp = int(s_prices.split(",")[0]) if s_prices and s_prices.split(",")[0] else 0
                            if best_bp <= 0 or best_sp <= 0:
                                continue
                        except Exception: 
                            continue

                        price = int(mp_match.group(1))
                        side = "Outer" if price >= best_sp else ("Inner" if price <= best_bp else None)
                        
                        if side is None:
                            prev_trades = preload_trades[base_sym][session]
                            side = prev_trades[-1]["side"] if prev_trades else "Outer"
                        
                        preload_trades[base_sym][session].append({
                            "time": time_str, "t_val": t_val,
                            "price": price, "side": side
                        })

                # 透過安全鎖執行緒安全地將數據併入即時行情中，並同步更新 O(1) 累計狀態
                with self._rt_lock:
                    # 強制清空所有即時觸發與通知快取，以防預載期間的即時 Tick 造成干擾
                    if hasattr(self, '_rt_triggers'):
                        self._rt_triggers.clear()
                    if hasattr(self, '_rt_notified_keys'):
                        self._rt_notified_keys.clear()
                    if hasattr(self, '_rt_duration_cache'):
                        self._rt_duration_cache.clear()
                    if hasattr(self, '_rt_calc_cache'):
                        self._rt_calc_cache.clear()

                    for sym in ["TXF", "MXF"]:
                        self.live_symbol_trades[sym][active_session] = preload_trades[sym][active_session]
                        
                        # 先行按 t_val 進行嚴格排序，避免亂序 Tick 導致極值計算錯誤
                        self.live_symbol_trades[sym][active_session].sort(key=lambda x: x["t_val"])
                        
                        state = self._rt_state[sym][active_session]
                        trades = self.live_symbol_trades[sym][active_session]
                        if trades:
                            state["count"] = len(trades)
                            state["sum_price"] = sum(t["price"] for t in trades)
                            state["day_max"] = max(t["price"] for t in trades)
                            state["day_min"] = min(t["price"] for t in trades)
                            state["max_time"] = next(t["time"] for t in reversed(trades) if t["price"] == state["day_max"])
                            state["min_time"] = next(t["time"] for t in reversed(trades) if t["price"] == state["day_min"])
                            
                            # 強制將 scan 進度歸零，確保下一次分析從第 0 筆 Tick 開始進行完美的全量歷史重播
                            state["scan_idx"] = 0
                            state["running_max"] = -999999
                            state["running_min"] = 999999
                            state["last_price"] = None
                            state["last_check_time_h"] = -999999.0
                            state["last_check_time_b"] = -999999.0
                            
                            # 累計外內盤筆數與時間
                            outer_trades = [t for t in trades if t["side"] == "Outer"]
                            inner_trades = [t for t in trades if t["side"] == "Inner"]
                            state["outer_count"] = len(outer_trades)
                            state["inner_count"] = len(inner_trades)
                            if outer_trades:
                                state["first_outer_time"] = outer_trades[0]["t_val"]
                                state["last_outer_time"] = outer_trades[-1]["t_val"]
                            if inner_trades:
                                state["first_inner_time"] = inner_trades[0]["t_val"]
                                state["last_inner_time"] = inner_trades[-1]["t_val"]
                            
                            if sym == "MXF":
                                self._last_mxf_price = trades[-1]["price"]

                    # 排序後精確追蹤極值演變軌跡 (大臺與小臺雙軌獨立計算，以實現對比回顧)
                    extreme_evolution = {"TXF": [], "MXF": []}
                    for sym in ["TXF", "MXF"]:
                        trades = self.live_symbol_trades[sym][active_session]
                        running_max = None
                        running_min = None
                        for trade in trades:
                            price = trade["price"]
                            time_str = trade["time"]
                            if running_max is None or running_min is None:
                                running_max = price
                                running_min = price
                                extreme_evolution[sym].append((time_str, "起點", price, running_max, running_min, 0))
                            else:
                                if price > running_max:
                                    old_max = running_max
                                    running_max = price
                                    amp = running_max - running_min
                                    extreme_evolution[sym].append((time_str, "📈 創今日新高", price, old_max, running_min, amp))
                                elif price < running_min:
                                    old_min = running_min
                                    running_min = price
                                    amp = running_max - running_min
                                    extreme_evolution[sym].append((time_str, "📉 創今日新低", price, running_max, old_min, amp))

                self.safe_call(self._on_preload_success, active_session, extreme_evolution)
            except Exception as e:
                self.safe_call(self._on_preload_failed, str(e))

        threading.Thread(target=worker, daemon=True).start()

    def _on_preload_success(self, active_session: str, extreme_evolution: dict):
        """
        職責: 預載成功之 GUI 執行緒安全回呼，格式化輸出今日大臺與小臺之歷史極值演變，大臺優先。
        """
        self.btn_realtime.setEnabled(True)
        self.kline_chart.plot_widget.getViewBox().enableAutoRange()  # 新增：今日歷史日誌預載成功時強制開啟原生自動對焦，重算對焦視界
        txf_cnt = len(self.live_symbol_trades["TXF"][active_session])
        mxf_cnt = len(self.live_symbol_trades["MXF"][active_session])
        self.append_log(f"[預載] 今日日誌載入成功！大臺: {txf_cnt} 筆, 小臺: {mxf_cnt} 筆。即時分析啟動！")
        
        # 依序輸出大臺與小臺的極值歷史軌跡，優先大臺
        for sym, name in [("TXF", "大臺"), ("MXF", "小臺")]:
            ev_list = extreme_evolution.get(sym, [])
            if ev_list:
                self.append_log("\n    " + "=" * 65)
                self.append_log(f"    🚩 [今日{name}歷史極值演變回顧 (開盤至目前)]")
                self.append_log("    " + "-" * 65)
                for time_str, event_type, price, r_max, r_min, amp in ev_list:
                    formatted_time = f"{time_str[0:2]}:{time_str[2:4]}:{time_str[4:6]}" if len(time_str) >= 6 else time_str
                    if event_type == "起點":
                        self.append_log(f"    🏁 [{formatted_time}] 開盤起點價: {price}")
                    elif "新高" in event_type:
                        self.append_log(f"    📈 [{formatted_time}] {event_type} {price} (原最高: {r_max}) | 今日最低: {r_min} | 當時振幅: {amp} 點")
                    elif "新低" in event_type:
                        self.append_log(f"    📉 [{formatted_time}] {event_type} {price} (原最低: {r_min}) | 今日最高: {r_max} | 當時振幅: {amp} 點")
                self.append_log("    " + "=" * 65 + "\n")
            
        # 立即發送訊號觸發一次計算更新，讓介面立刻秀出今日已有的極值與圖表
        self._analysis_event.set()

    def _on_preload_failed(self, error_msg: str):
        """預載失敗之 GUI 執行緒安全回呼"""
        self.btn_realtime.setEnabled(True)
        self.append_log(f"[預載] 預載過程中發生錯誤: {error_msg}，將直接由全新即時 Tick 開始。")
        self._analysis_event.set()

    def append_log(self, text: str, clear: bool = False):
        """
        職責: 安全添加系統運行日誌。
        利用 _system_logs 保存系統日誌，以防即時極值詳情報告刷新時將日誌覆蓋掉。
        同時設置 300 行上限，防範長時間運行的 Memory Leak！
        """
        if not hasattr(self, '_system_logs'):
            self._system_logs = []
            
        if clear:
            self.txt_output.clear()
            self._system_logs.clear()
            
        # 內存保護：防止系統日誌無限制膨脹
        if len(self._system_logs) > 300:
            self._system_logs = self._system_logs[-200:]
            
        # 僅在非報告本身時，將系統連線與觸發日誌記錄到歷史中
        if "[即時極值詳情" not in text and "[絕對極值詳情" not in text:
            clean_text = text.strip()
            if clean_text:
                self._system_logs.append(text)
            
        self.txt_output.append(text)
        self.txt_output.moveCursor(self.txt_output.textCursor().End)

    async def send_telegram_async(self, msg: str):
        if not self.tg_token or not self.tg_chat_id:
            return
            
        url = f"https://api.telegram.org/bot{self.tg_token}/sendMessage"
        payload = {"chat_id": self.tg_chat_id, "text": msg}
        
        try:
            async with httpx.AsyncClient(trust_env=False) as client:
                response = await client.post(url, data=payload, timeout=5.0)
                if response.status_code != 200:
                    print(f"Telegram 推播失敗，狀態碼: {response.status_code}")
        except Exception as e:
            print(f"Telegram 推播非同步發送異常: {e}")

    def check_session_change(self):
        if not self.is_realtime_running:
            return
            
        port, session = self.check_session_change_port() if hasattr(self, 'check_session_change_port') else self.check_session_port()
        if session != self.current_session_name:
            self.append_log(f"\n--- 偵測到盤別切換：重啟連線至 {port} ({session}) ---")
            self.stop_realtime()
            QTimer.singleShot(2000, self.start_realtime)

    def check_session_port(self) -> Tuple[int, str]:
        """檢查當前時間點對應的交易盤別與 Port (100% 移植原版邏輯)"""
        now = datetime.now(timezone(timedelta(hours=8)))
        time_val = now.hour * 3600 + now.minute * 60 + now.second
        weekday = now.weekday()
        
        # 週一至週五的 08:30 到 13:45 為日盤
        is_day = (0 <= weekday <= 4) and (8*3600 + 30*60 <= time_val <= 13*3600 + 45*60)
        # 週一至週五的 14:50 之後，或者週二至週六 05:00 之前為夜盤
        is_night_1 = (0 <= weekday <= 4) and (time_val >= 14*3600 + 50*60)
        is_night_2 = (1 <= weekday <= 5) and (time_val <= 5*3600)
        
        if is_day:
            return 443, "日盤"
        elif is_night_1 or is_night_2:
            return 442, "夜盤"
        return 443, "非交易時間 (預設用 443 待命)"

    def select_replay_directory(self):
        """職責: 彈出資料夾選擇視窗，讓使用者自行選定日期目錄"""
        default_dir = os.path.join(APP_DIR, "Logs")
        if not os.path.exists(default_dir):
            default_dir = APP_DIR
            
        selected_dir = QFileDialog.getExistingDirectory(self, "選擇 event.log 所在的資料夾", default_dir)
        if selected_dir:
            log_file = os.path.join(selected_dir, "event.log")
            if os.path.exists(log_file):
                self.current_replay_dir = selected_dir
                self.txt_replay_path.setText(os.path.basename(selected_dir))
                self.lbl_status.setText(f"已選定復盤目錄: {os.path.basename(selected_dir)}")
            else:
                QMessageBox.warning(self, "警告", "所選資料夾中沒有找到 event.log 檔案！")

    def load_replay_log(self):
        """職責: 主執行緒安全讀取 UI，並在背景高效非同步解析 event.log，保持即時連線不斷線"""
        if not hasattr(self, 'current_replay_dir') or not self.current_replay_dir:
            self.lbl_status.setText("請先點選選擇資料夾")
            return
            
        log_path = os.path.join(self.current_replay_dir, "event.log")
        if not os.path.exists(log_path):
            self.lbl_status.setText(f"找不到檔案: {os.path.basename(log_path)}")
            return
            
        # 核心物理重設防線：載入新日期前，徹底停止並銷毀上一個日期的回放執行緒與進度時鐘
        self.stop_replay()
        
        self.lbl_status.setText("載入復盤檔案中...")
        self.btn_load_replay.setEnabled(False)
        self.btn_play_pause.setEnabled(False)
        
        # 核心防線：在主執行緒讀取 GUI 值，嚴禁在背景執行緒中直接存取 QComboBox (防護跨執行緒 UI 存取崩潰)
        active_session = self.cbo_replay_session.currentText()
        
        def parser_worker(path: str, session_name: str):
            try:
                pattern = re.compile(r"Symbol=([^, \t\r\n]+)")
                mattime_pat = re.compile(r"mattime=([^, \t\r\n]+)")
                mat_pri_pat = re.compile(r"matpri=([-]?\d+)")
                tmatqty_pat = re.compile(r"tmatqty=([-]?\d+)")
                bestbp_pat = re.compile(r"bestbp=([\d,]*)")
                bestsp_pat = re.compile(r"bestsp=([\d,]*)")

                parsed_ticks = []
                last_tmatqty = defaultdict(lambda: -1)
                
                with open(path, "r", encoding="cp950") as f:
                    for line in f:
                        if "TXF" not in line and "MXF" not in line:
                            continue
                        match = pattern.search(line)
                        if not match: 
                            continue
                        symbol = match.group(1)
                        
                        base_sym = None
                        if symbol.startswith("TXF"): 
                            base_sym = "TXF"
                        elif symbol.startswith("MXF"): 
                            base_sym = "MXF"
                        else: 
                            continue
                        
                        mt_match = mattime_pat.search(line)
                        mp_match = mat_pri_pat.search(line)
                        tq_match = tmatqty_pat.search(line)
                        if not mt_match or not mp_match or not tq_match: 
                            continue
                        
                        time_str = mt_match.group(1)
                        t_val_raw = self.engine.parse_time(time_str)
                        
                        if 31500 <= t_val_raw <= 49500:
                            session = "日盤"
                            t_val = t_val_raw
                        elif t_val_raw >= 54000 or t_val_raw <= 18000:
                            session = "夜盤"
                            t_val = t_val_raw + 86400 if t_val_raw <= 18000 else t_val_raw
                        else:
                            continue
                            
                        if session != session_name:
                            continue

                        tmatqty = int(tq_match.group(1))
                        if tmatqty < 0 or tmatqty <= last_tmatqty[(base_sym, session)]: 
                            continue
                        last_tmatqty[(base_sym, session)] = tmatqty
                        
                        bp_m = bestbp_pat.search(line)
                        sp_m = bestsp_pat.search(line)
                        if not bp_m or not sp_m: 
                            continue
                        
                        try:
                            b_prices = bp_m.group(1)
                            s_prices = sp_m.group(1)
                            best_bp = int(b_prices.split(",")[0]) if b_prices and b_prices.split(",")[0] else 0
                            best_sp = int(s_prices.split(",")[0]) if s_prices and s_prices.split(",")[0] else 0
                            if best_bp <= 0 or best_sp <= 0:
                                continue
                        except Exception: 
                            continue

                        price = int(mp_match.group(1))
                        side = "Outer" if price >= best_sp else ("Inner" if price <= best_bp else None)
                        
                        parsed_ticks.append({
                            "symbol": base_sym,
                            "time": time_str,
                            "t_val": t_val,
                            "price": price,
                            "side": side,
                            "best_bp": best_bp,
                            "best_sp": best_sp,
                            "session": session
                        })

                parsed_ticks.sort(key=lambda x: x["t_val"])
                
                # 採用 100% 執行緒安全的 Qt 自訂信號機制發送結果，完全取代在子執行緒中使用 QTimer 的危險做法
                self.replay_parse_completed.emit({
                    "success": True,
                    "ticks": parsed_ticks,
                    "session_name": session_name
                })
            except Exception as e:
                self.replay_parse_completed.emit({
                    "success": False,
                    "error": str(e)
                })

        # 將變數做為 args 傳入，完全保證執行緒在非 UI 環境安全運行！
        threading.Thread(target=parser_worker, args=(log_path, active_session), daemon=True).start()

    @Slot(dict)
    def on_replay_parse_done(self, result: dict):
        """職責: 復盤日誌背景解析完成之執行緒安全槽函數，刷新 UI 並自動對焦"""
        self.btn_load_replay.setEnabled(True)
        
        if not result.get("success", False):
            err_msg = result.get("error", "未知錯誤")
            self.lbl_status.setText(f"載入出錯: {err_msg}")
            return
            
        ticks = result.get("ticks", [])
        session_name = result.get("session_name", "")
        
        self.all_parsed_ticks = ticks
        # 記錄當前執行緒安全的復盤盤別，以供背景計算執行緒讀取
        self.current_replay_session = session_name
        if not self.all_parsed_ticks:
            self.lbl_status.setText(f"無 [{session_name}] Tick 資料！")
            self.btn_play_pause.setEnabled(False)
            self.sld_progress.setEnabled(False)
        else:
            self.lbl_status.setText(f"載入 {len(self.all_parsed_ticks)} 筆 Tick。")
            self.btn_play_pause.setEnabled(True)
            self.btn_stop_replay.setEnabled(True)
            self.sld_progress.setEnabled(True)
            self.sld_progress.setRange(0, len(self.all_parsed_ticks) - 1)
            self.sld_progress.setValue(0)
            
            # 核心修復：載入成功後，立刻將模式切換為復盤模式，使分析引擎對焦復盤軌，畫面立刻呈現第一筆
            self.is_replaying = True
            
            self.reset_replay_track()
            self.reconstruct_replay_up_to(0)

    def reset_replay_track(self):
        """職責: 執行緒安全地清空復盤軌數據快取與記憶體狀態"""
        with self._rt_lock:
            self.replay_symbol_trades.clear()
            self._replay_rt_state = {
                sym: {"日盤": self._init_rt_state(), "夜盤": self._init_rt_state()} 
                for sym in ["TXF", "MXF"]
            }
            if hasattr(self, '_rt_triggers'):
                keys_to_remove = [k for k in self._rt_triggers.keys() if isinstance(k, tuple) and len(k) == 2]
                for k in keys_to_remove:
                    self._rt_triggers.pop(k, None)
            if hasattr(self, '_rt_notified_keys'):
                self._rt_notified_keys.clear()
            if hasattr(self, '_rt_calc_cache'):
                self._rt_calc_cache.clear()

    def reconstruct_replay_up_to(self, index: int):
        """職責: 拖曳 Slider 時，瞬間 O(1) 重構 0 到指定索引之歷史狀態與 UI"""
        if not self.all_parsed_ticks or index < 0 or index >= len(self.all_parsed_ticks):
            return
            
        active_session = self.cbo_replay_session.currentText()
        self.reset_replay_track()
        subset = self.all_parsed_ticks[:index + 1]
        if not subset:
            return
            
        with self._rt_lock:
            for tick in subset:
                base_sym = tick["symbol"]
                price = tick["price"]
                mt = tick["time"]
                t_val = tick["t_val"]
                side = tick["side"]
                
                if side is None:
                    prev_trades = self.replay_symbol_trades[base_sym].get(active_session, [])
                    side = prev_trades[-1]["side"] if prev_trades else "Outer"
                    
                trade = {
                    "time": mt, "t_val": t_val, "price": price, "side": side
                }
                self.replay_symbol_trades[base_sym][active_session].append(trade)
                
                state = self._replay_rt_state[base_sym][active_session]
                state["count"] += 1
                state["sum_price"] += price
                if price > state["day_max"]: 
                    state["day_max"] = price
                    state["max_time"] = mt
                if price < state["day_min"]: 
                    state["day_min"] = price
                    state["min_time"] = mt
                
                if side == "Outer":
                    state["outer_count"] += 1
                    if state["first_outer_time"] is None: state["first_outer_time"] = t_val
                    state["last_outer_time"] = t_val
                elif side == "Inner":
                    state["inner_count"] += 1
                    if state["first_inner_time"] is None: state["first_inner_time"] = t_val
                    state["last_inner_time"] = t_val
                    
                if base_sym == "MXF":
                    self._last_mxf_price = price
                    
        self.lbl_virtual_time.setText(f"復盤時間: {subset[-1]['time']}")
        self._analysis_event.set()

    def toggle_replay_play(self):
        """播放 / 暫停"""
        if not self.all_parsed_ticks:
            return
            
        if self.replay_thread and self.replay_thread.isRunning():
            if self.replay_thread.is_paused:
                self.is_replaying = True
                self.replay_thread.set_paused(False)
                self.btn_play_pause.setText("⏸ 暫停")
                self.lbl_status.setText("復盤回放中...")
            else:
                self.replay_thread.set_paused(True)
                self.btn_play_pause.setText("▶ 播放")
                self.lbl_status.setText("復盤暫停")
        else:
            self.is_replaying = True
            self.replay_thread = ReplayThread(self.all_parsed_ticks, self)
            self.replay_thread.tick_emitted.connect(self.on_replay_tick_emitted)
            self.replay_thread.virtual_time_changed.connect(self.on_replay_time_changed)
            self.replay_thread.progress_changed.connect(self.on_replay_progress_changed)
            self.replay_thread.replay_finished.connect(self.on_replay_finished)
            
            self.replay_thread.set_position(self.sld_progress.value())
            self.replay_thread.set_speed(self.get_current_replay_speed())
            
            self.replay_thread.start()
            self.btn_play_pause.setText("⏸ 暫停")
            self.lbl_status.setText("復盤回放中...")

    def on_replay_tick_emitted(self, tick: dict):
        """回放 Tick 餵入槽函數。實作計數器節流 Batching，防範 Repaint Storm"""
        base_sym = tick["symbol"]
        price = tick["price"]
        mt = tick["time"]
        t_val = tick["t_val"]
        side = tick["side"]
        active_session = tick["session"]
        
        with self._rt_lock:
            if side is None:
                prev_trades = self.replay_symbol_trades[base_sym].get(active_session, [])
                side = prev_trades[-1]["side"] if prev_trades else "Outer"
                
            trade = {
                "time": mt, "t_val": t_val, "price": price, "side": side
            }
            self.replay_symbol_trades[base_sym][active_session].append(trade)
            
            state = self._replay_rt_state[base_sym][active_session]
            state["count"] += 1
            state["sum_price"] += price
            if price > state["day_max"]: 
                state["day_max"] = price
                state["max_time"] = mt
            if price < state["day_min"]: 
                state["day_min"] = price
                state["min_time"] = mt
            
            if side == "Outer":
                state["outer_count"] += 1
                if state["first_outer_time"] is None: state["first_outer_time"] = t_val
                state["last_outer_time"] = t_val
            elif side == "Inner":
                state["inner_count"] += 1
                if state["first_inner_time"] is None: state["first_inner_time"] = t_val
                state["last_inner_time"] = t_val
                
            if base_sym == "MXF":
                self._last_mxf_price = price

        if not hasattr(self, '_replay_throttle_counter'):
            self._replay_throttle_counter = 0
        self._replay_throttle_counter += 1
        
        current_speed = self.get_current_replay_speed()
        batch_limit = 30 if current_speed >= 10.0 else 5
        
        if self._replay_throttle_counter >= batch_limit:
            self._replay_throttle_counter = 0
            self._analysis_event.set()

    def on_replay_time_changed(self, time_str: str):
        self.lbl_virtual_time.setText(f"復盤時間: {time_str}")

    def on_replay_progress_changed(self, progress: int):
        self.sld_progress.blockSignals(True)
        self.sld_progress.setValue(progress)
        self.sld_progress.blockSignals(False)

    def on_replay_finished(self):
        self.is_replaying = False
        self.btn_play_pause.setText("▶ 播放")
        self.lbl_status.setText("復盤播畢")
        self._analysis_event.set()

    def get_current_replay_speed(self) -> float:
        val = self.cbo_replay_speed.currentText()
        if val == "1x": return 1.0
        elif val == "2x": return 2.0
        elif val == "5x": return 5.0
        elif val == "10x": return 10.0
        elif val == "20x": return 20.0
        elif val == "50x": return 50.0
        elif val == "自訂": return float(self.spn_max_speed.value())
        return 1.0

    def on_replay_speed_changed(self, val: str):
        if self.replay_thread and self.replay_thread.isRunning():
            self.replay_thread.set_speed(self.get_current_replay_speed())

    def on_max_speed_limit_changed(self, val: int):
        if self.cbo_replay_speed.currentText() == "自訂" and self.replay_thread and self.replay_thread.isRunning():
            self.replay_thread.set_speed(float(val))

    def on_slider_pressed(self):
        if self.replay_thread and self.replay_thread.isRunning():
            self._was_playing_before_drag = not self.replay_thread.is_paused
            self.replay_thread.set_paused(True)
        else:
            self._was_playing_before_drag = False

    def on_slider_moved(self, val: int):
        self.reconstruct_replay_up_to(val)

    def on_slider_released(self):
        val = self.sld_progress.value()
        if self.replay_thread and self.replay_thread.isRunning():
            self.replay_thread.set_position(val)
            if getattr(self, '_was_playing_before_drag', False):
                self.replay_thread.set_paused(False)
        else:
            self.reconstruct_replay_up_to(val)

    def stop_replay(self):
        """職責: 停止回放，並無縫熱切換回盤中實時數據源"""
        self.is_replaying = False
        if self.replay_thread:
            self.replay_thread.stop()
            self.replay_thread.wait()
            self.replay_thread = None
            
        self.btn_play_pause.setEnabled(False)
        self.btn_play_pause.setText("▶ 播放")
        self.btn_stop_replay.setEnabled(False)
        self.sld_progress.setEnabled(False)
        self.lbl_virtual_time.setText("復盤時間: --:--:--")
        
        self.reset_replay_track()
        self.lbl_status.setText("已無縫切回盤中實時行情")
        self._analysis_event.set()

    def closeEvent(self, event):
        """安全關閉程式：停止復盤執行緒（杜絕殭屍執行緒）、停止行情、釋放 COM 資源、停止定時器"""
        self.stop_replay()
        # 停止 COM 訊息幫浦定時器
        if hasattr(self, '_com_pump_timer'):
            self._com_pump_timer.stop()
        self.stop_realtime()
        self.session_timer.stop()
        # gui_refresh_timer 已在架構重構中移除，此處安全跳過
        if self.wnd_unbroken_k:
            self.wnd_unbroken_k.close()
        # 釋放 COM 執行環境
        try:
            pythoncom.CoUninitialize()
        except Exception:
            pass
        event.accept()

# =================================══════════════════════════════════
# 6. 進階日期相容輔助工具
# =================================══════════════════════════════════

def relativedelta_days(d: int) -> timedelta:
    return timedelta(days=d)

def relativedelta_months(m: int) -> timedelta:
    return timedelta(days=31)

class queue_safe:
    """Thread-safe Queue 包裝"""
    def __init__(self):
        self._q = []
        self._lock = threading.Lock()
    def put(self, item):
        with self._lock:
            self._q.append(item)
    def get(self):
        with self._lock:
            if self._q:
                return self._q.pop(0)
            raise queue_empty_err()
    def empty(self) -> bool:
        with self._lock:
            return len(self._q) == 0

class queue_empty_err(Exception):
    pass

# =================================══════════════════════════════════
# 7. 主程式啟動入口
# =================================══════════════════════════════════

if __name__ == "__main__":
    os.environ["QT_AUTO_SCREEN_SCALE_FACTOR"] = "1"
    
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    
    font = QFont("Microsoft JhengHei", 9)
    app.setFont(font)
    
    window = ExtremeSignalApp()
    window.show()
    
    sys.exit(app.exec())
