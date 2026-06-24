using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeSignalAppCS.Models;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Run = System.Windows.Documents.Run;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// 內部 K 線 DX 預繪繪圖元件。
    /// 繼承自 FrameworkElement，以 OnRender 進行 DirectX GPU 級預繪與局部增量手繪。
    /// </summary>
    public class KLinePainter : FrameworkElement
    {
        private List<KlineBar> _candles = [];
        private double _minX, _maxX; // 當前 X 軸索引可見範圍
        private double _minY;
        private double _maxY;

        private DrawingGroup? _historyDrawingCache;
        private int _cachedHistoryCount = -1;
        private double _cachedW = -1;
        private double _cachedH = -1;

        // 預先建立各種顏色與畫筆（降低 GC 負擔）(優化記憶體分配)
        private readonly Pen _upPen = new(new SolidColorBrush(Color.FromRgb(235, 75, 75)), 1.5);
        private readonly Brush _upBrush = new SolidColorBrush(Color.FromRgb(235, 75, 75));
        private readonly Pen _downPen = new(new SolidColorBrush(Color.FromRgb(40, 167, 69)), 1.5);
        private readonly Brush _downBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
        private readonly Pen _flatPen = new(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1.5);
        private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 0.8);

        // 文字繪製快取
        private readonly Typeface _typeface = new(new FontFamily("Consolas, Microsoft JhengHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly Brush _textBrush = new SolidColorBrush(Color.FromArgb(200, 220, 220, 220));
        private readonly Brush _highTextBrush = new SolidColorBrush(Color.FromRgb(235, 75, 75));
        private readonly Brush _lowTextBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
        private readonly Pen _highlightPen = new(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2.0);
        private readonly Pen _stopLossPen = new(new SolidColorBrush(Color.FromRgb(255, 215, 0)), 1.5);
        private readonly Pen _observerStopLossPen = new(new SolidColorBrush(Color.FromRgb(255, 0, 255)), 2.0);

        private double? _highlightPrice;
        /// <summary>
        /// 選取的反白價格（白色橫線）
        /// </summary>
        public double? HighlightPrice
        {
            get => _highlightPrice;
            set
            {
                _highlightPrice = value;
                InvalidateVisual();
            }
        }

        private int? _highlightIndex;
        /// <summary>
        /// 選取的 K 棒索引（白色外框）
        /// </summary>
        public int? HighlightIndex
        {
            get => _highlightIndex;
            set
            {
                _highlightIndex = value;
                InvalidateVisual();
            }
        }

        private double? _stopLossPrice;
        /// <summary>
        /// 停損價格（黃色橫線），由趨勢事件的前後 K 棒比較而得
        /// </summary>
        public double? StopLossPrice
        {
            get => _stopLossPrice;
            set
            {
                _stopLossPrice = value;
                InvalidateVisual();
            }
        }

        private double? _observerStopLossPrice;
        /// <summary>
        /// 停損價格（桃紅色橫線），由極值觀測表選取連動而得
        /// </summary>
        public double? ObserverStopLossPrice
        {
            get => _observerStopLossPrice;
            set
            {
                _observerStopLossPrice = value;
                InvalidateVisual();
            }
        }

        private int _highlightDirection = 0;
        /// <summary>
        /// 趨勢方向：1=多方, -1=空方, 0=無
        /// </summary>
        public int HighlightDirection
        {
            get => _highlightDirection;
            set
            {
                _highlightDirection = value;
                InvalidateVisual();
            }
        }

        public KLinePainter()
        {
            ClipToBounds = true; // 開啟邊界剪裁
            _upPen.Freeze();
            _upBrush.Freeze();
            _downPen.Freeze();
            _downBrush.Freeze();
            _flatPen.Freeze();
            _gridPen.Freeze();
            _textBrush.Freeze();
            _highTextBrush.Freeze();
            _lowTextBrush.Freeze();
            _highlightPen.Freeze();
            _stopLossPen.Freeze();
            _observerStopLossPen.Freeze();
        }

        public void SetData(List<KlineBar> candles, double minX, double maxX, double minY, double maxY)
        {
            _candles = candles;
            _minX = minX;
            _maxX = maxX;
            _minY = minY;
            _maxY = maxY;

            _historyDrawingCache = null; // 重置快取，強迫縮放平移時重構坐標

            InvalidateVisual(); // 重新繪製
        }

        /// <summary>
        /// 一次性將已收盤的歷史 K 棒編譯進 Drawing 快取中。
        /// 實現 GPU 貼圖硬件剪裁與極致流暢平移。
        /// </summary>
        private void GenerateHistoryDrawingCache(double w, double h)
        {
            if (_candles == null || _candles.Count <= 1)
            {
                _historyDrawingCache = null;
                _cachedHistoryCount = 0;
                _cachedW = w;
                _cachedH = h;
                return;
            }

            int historyCount = _candles.Count - 1; // 歷史收盤 K 棒
            var group = new DrawingGroup();
            
            int startIdx = (int)Math.Max(0, Math.Floor(_minX) - 1);
            int endIdx = (int)Math.Min(historyCount - 1, Math.Ceiling(_maxX) + 1);

            using (var dc = group.Open())
            {
                for (int i = startIdx; i <= endIdx; i++)
                {
                    var c = _candles[i];
                    double x = GetCanvasX(i, w);
                    double openY = GetCanvasY(c.Open, h);
                    double closeY = GetCanvasY(c.Close, h);
                    double highY = GetCanvasY(c.High, h);
                    double lowY = GetCanvasY(c.Low, h);
                    
                    double colW = w / Math.Max(1.0, _maxX - _minX);
                    double barW = colW * 0.6;

                    Pen pen = _flatPen;
                    Brush brush = Brushes.Transparent;

                    if (c.Tag == "up")
                    {
                        pen = _upPen;
                        brush = _upBrush;
                    }
                    else if (c.Tag == "down")
                    {
                        pen = _downPen;
                        brush = _downBrush;
                    }

                    // 畫最高/最低影線
                    dc.DrawLine(pen, new Point(x, highY), new Point(x, lowY));
                    
                    // 畫開收實體
                    double rectH = Math.Abs(closeY - openY);
                    if (rectH < 1.0) rectH = 1.0;
                    dc.DrawRectangle(brush, pen, new Rect(x - barW / 2, Math.Min(openY, closeY), barW, rectH));
                }
            }

            group.Freeze();
            _historyDrawingCache = group;
            _cachedHistoryCount = historyCount;
            _cachedW = w;
            _cachedH = h;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double w = ActualWidth;
            double h = ActualHeight;

            if (w <= 0 || h <= 0 || _candles == null || _candles.Count == 0)
                return;

            // 建立物理硬體邊界剪裁矩形，杜絕任何 K 線外溢
            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            try
            {
                // 1. 繪製背景暗黑网格線 (10% 透明度) 與左側 Y 軸刻度
                DrawGridLines(drawingContext, w, h);

                // 為 K 線繪製建立第二層剪裁，保護右側 Y 軸刻度不被 K 棒疊加
                drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(1.0, w - RightMargin), Math.Max(1.0, h - BottomMargin))));
                try
                {
                    // 2. 檢測歷史 K 棒數量或尺寸是否改變，若有改變則重建 GPU 預繪快取
                int historyCount = Math.Max(0, _candles.Count - 1);
                if (historyCount != _cachedHistoryCount || _historyDrawingCache == null || Math.Abs(_cachedW - w) > 0.1 || Math.Abs(_cachedH - h) > 0.1)
                {
                    GenerateHistoryDrawingCache(w, h);
                }

                // 3. DirectX GPU 硬件貼圖重播歷史 K 線快取 (極速零負擔)
                if (_historyDrawingCache != null)
                {
                    drawingContext.DrawDrawing(_historyDrawingCache);
                }

                // 4. 局部增量手繪最後一根未收盤 K 棒
                int lastIdx = _candles.Count - 1;
                var last = _candles[lastIdx];
                double lx = GetCanvasX(lastIdx, w);
                double lopenY = GetCanvasY(last.Open, h);
                double lcloseY = GetCanvasY(last.Close, h);
                double lhighY = GetCanvasY(last.High, h);
                double llowY = GetCanvasY(last.Low, h);

                double colW = w / Math.Max(1.0, _maxX - _minX);
                double barW = colW * 0.6;

                Pen lpen = _flatPen;
                Brush lbrush = Brushes.Transparent;

                if (last.Tag == "up")
                {
                    lpen = _upPen;
                    lbrush = _upBrush;
                }
                else if (last.Tag == "down")
                {
                    lpen = _downPen;
                    lbrush = _downBrush;
                }

                drawingContext.DrawLine(lpen, new Point(lx, lhighY), new Point(lx, llowY));
                double lrectH = Math.Abs(lcloseY - lopenY);
                if (lrectH < 1.0) lrectH = 1.0;
                drawingContext.DrawRectangle(lbrush, lpen, new Rect(lx - barW / 2, Math.Min(lopenY, lcloseY), barW, lrectH));

                // 5. 動態繪製可見範圍內的最高與最低價標示
                DrawVisibleHighLow(drawingContext, w, h);

                // 6. 繪製選取的反白價格白色橫線
                if (_highlightPrice.HasValue)
                {
                    double hy = GetCanvasY(_highlightPrice.Value, h);
                    drawingContext.DrawLine(_highlightPen, new Point(0, hy), new Point(Math.Max(0, w - RightMargin), hy));
                }

                // 6.5. 繪製停損黃色橫線
                if (_stopLossPrice.HasValue)
                {
                    double sly = GetCanvasY(_stopLossPrice.Value, h);
                    drawingContext.DrawLine(_stopLossPen, new Point(0, sly), new Point(Math.Max(0, w - RightMargin), sly));
                }

                // 6.6. 繪製觀測表反白停損桃紅色橫線
                if (_observerStopLossPrice.HasValue)
                {
                    double osly = GetCanvasY(_observerStopLossPrice.Value, h);
                    drawingContext.DrawLine(_observerStopLossPen, new Point(0, osly), new Point(Math.Max(0, w - RightMargin), osly));

                    // 繪製停損價標籤
                    string labelText = _observerStopLossPrice.Value.ToString("F0");
                    var formattedText = new FormattedText(
                        labelText,
                        CultureInfo.GetCultureInfo("en-us"),
                        System.Windows.FlowDirection.LeftToRight,
                        _typeface,
                        14,
                        Brushes.White,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);

                    double textX = Math.Max(0, w - RightMargin) - formattedText.Width - 6; // 靠右，在價位欄前
                    double textY = osly - formattedText.Height - 4; // 在線上

                    Brush bgBrush = new SolidColorBrush(Color.FromRgb(255, 0, 255)); // 預設桃紅底
                    if (_highlightDirection == 1) // 做多停損，用紅底白字
                        bgBrush = new SolidColorBrush(Color.FromRgb(235, 75, 75));
                    else if (_highlightDirection == -1) // 做空停損，用綠底白字
                        bgBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));

                    var bgRect = new Rect(textX - 4, textY - 2, formattedText.Width + 8, formattedText.Height + 4);
                    
                    Pen borderPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5); // 白色邊框
                    drawingContext.DrawRoundedRectangle(bgBrush, borderPen, bgRect, 3.0, 3.0);
                    drawingContext.DrawText(formattedText, new Point(textX, textY));
                }

                // 7. 繪製選取的 K 棒白色外框
                if (_highlightIndex.HasValue && _highlightIndex.Value >= 0 && _highlightIndex.Value < _candles.Count)
                {
                    int idx = _highlightIndex.Value;
                    var c = _candles[idx];
                    double hx = GetCanvasX(idx, w);
                    double hOpenY = GetCanvasY(c.Open, h);
                    double hCloseY = GetCanvasY(c.Close, h);
                    double hHighY = GetCanvasY(c.High, h);
                    double hLowY = GetCanvasY(c.Low, h);



                    double rectH = Math.Abs(hCloseY - hOpenY);
                    if (rectH < 1.0) rectH = 1.0;
                    
                    // 繪製透明底、白色外框的矩形包住實體
                    drawingContext.DrawRectangle(Brushes.Transparent, _highlightPen, new Rect(hx - barW / 2 - 1.5, Math.Min(hOpenY, hCloseY) - 1.5, barW + 3, rectH + 3));
                    // 上下影線也疊加上白色
                    drawingContext.DrawLine(_highlightPen, new Point(hx, hHighY), new Point(hx, Math.Min(hOpenY, hCloseY)));
                    drawingContext.DrawLine(_highlightPen, new Point(hx, Math.Max(hOpenY, hCloseY)), new Point(hx, hLowY));
                }
                }
                finally
                {
                    drawingContext.Pop(); // 釋放 K 線專屬剪裁
                }
            }
            finally
            {
                drawingContext.Pop(); // 釋放剪裁
            }
        }

        private void DrawGridLines(DrawingContext dc, double w, double h)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double drawWidth = Math.Max(1.0, w - RightMargin);
            double drawHeight = Math.Max(1.0, h - BottomMargin);

            // 繪製 X 軸格線
            int gridCols = 8;
            for (int i = 1; i < gridCols; i++)
            {
                double gx = (drawWidth / gridCols) * i;
                // dc.DrawLine(_gridPen, new Point(gx, 0), new Point(gx, drawHeight)); // 取消縱向格線

                if (_candles != null && _candles.Count > 0)
                {
                    double rangeX = _maxX - _minX;
                    if (rangeX <= 0) rangeX = 1.0;
                    double indexFloat = (gx / drawWidth) * rangeX + _minX;
                    int index = (int)Math.Round(indexFloat);
                    if (index >= 0 && index < _candles.Count)
                    {
                        string tLabel = _candles[index].TimeLabel;
                        if (!string.IsNullOrEmpty(tLabel))
                        {
                            string[] parts = tLabel.Split('~');
                            string timeStr = parts[0].Trim();
                            if (timeStr.Length >= 4 && !timeStr.Contains(':'))
                            {
                                timeStr = timeStr.Substring(0, 2) + ":" + timeStr.Substring(2, 2);
                            }
                            else if (timeStr.Length >= 5 && timeStr.Contains(':'))
                            {
                                timeStr = timeStr.Substring(0, 5);
                            }

                            var timeText = new FormattedText(
                                timeStr,
                                CultureInfo.GetCultureInfo("en-us"),
                                System.Windows.FlowDirection.LeftToRight,
                                _typeface,
                                11,
                                _textBrush,
                                pixelsPerDip);
                            dc.DrawText(timeText, new Point(gx - timeText.Width / 2, drawHeight + 2));
                        }
                    }
                }
            }

            // 繪製 Y 軸格線與右側價位文字
            int gridRows = 6;
            for (int i = 1; i < gridRows; i++)
            {
                double gy = (drawHeight / gridRows) * i;
                // dc.DrawLine(_gridPen, new Point(0, gy), new Point(drawWidth, gy)); // 取消橫向格線

                // 根據 Y 座標反推價格
                double rangeY = _maxY - _minY;
                double price = _maxY - (gy / drawHeight) * rangeY;

                var formattedText = new FormattedText(
                    price.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    11,
                    _textBrush,
                    pixelsPerDip);

                dc.DrawText(formattedText, new Point(drawWidth + 5, gy - formattedText.Height - 2));
            }
        }

        private void DrawVisibleHighLow(DrawingContext dc, double w, double h)
        {
            if (_candles == null || _candles.Count == 0) return;

            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);
            if (startIdx > endIdx) return;

            double highest = -999999;
            double lowest = 999999;
            int highIdx = -1;
            int lowIdx = -1;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest)
                {
                    highest = _candles[i].High;
                    highIdx = i;
                }
                if (_candles[i].Low < lowest)
                {
                    lowest = _candles[i].Low;
                    lowIdx = i;
                }
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (highIdx != -1)
            {
                double hx = GetCanvasX(highIdx, w);
                double hy = GetCanvasY(highest, h);
                var text = new FormattedText(
                    "← " + highest.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    12,
                    _highTextBrush,
                    pixelsPerDip);
                
                double drawWidth = Math.Max(1.0, w - RightMargin);
                double textY = hy - text.Height / 2;
                if (textY < 0) textY = 0; // 防止頂部文字被切掉
                
                if (hx + 5 + text.Width > drawWidth)
                    dc.DrawText(text, new Point(hx - text.Width - 5, textY));
                else
                    dc.DrawText(text, new Point(hx + 5, textY));
            }

            if (lowIdx != -1)
            {
                double lx = GetCanvasX(lowIdx, w);
                double ly = GetCanvasY(lowest, h);
                var text = new FormattedText(
                    "← " + lowest.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    12,
                    _lowTextBrush,
                    pixelsPerDip);

                double drawWidth = Math.Max(1.0, w - RightMargin);
                double drawHeight = Math.Max(1.0, h - BottomMargin);
                double textY = ly - text.Height / 2;
                if (textY + text.Height > drawHeight) textY = drawHeight - text.Height; // 防止底部文字被切掉
                
                if (lx + 5 + text.Width > drawWidth)
                    dc.DrawText(text, new Point(lx - text.Width - 5, textY));
                else
                    dc.DrawText(text, new Point(lx + 5, textY));
            }
        }

        public const double RightMargin = 55.0;
        public const double BottomMargin = 20.0;

        public double GetCanvasX(double index, double width)
        {
            double rangeX = _maxX - _minX;
            if (rangeX <= 0) rangeX = 1.0;
            double drawWidth = Math.Max(1.0, width - RightMargin);
            return ((index - _minX) / rangeX) * drawWidth;
        }

        public double GetCanvasY(double price, double height)
        {
            double rangeY = _maxY - _minY;
            if (rangeY <= 0) rangeY = 1.0;
            // 頂端是 Y=0，底端是 Y=Height，所以要翻轉
            double drawHeight = Math.Max(1.0, height - BottomMargin);
            return (1.0 - (price - _minY) / rangeY) * drawHeight;
        }

        /// <summary>
        /// 重置快取。
        /// </summary>
        public void ResetCache()
        {
            _cachedHistoryCount = -1;
            _cachedW = -1;
            _cachedH = -1;
            _historyDrawingCache = null;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 專業看盤 K 線圖表控制元件。
    /// 封裝 DirectX GPU 預繪繪圖層、獨立透明十字游標 Overlay 層與置頂不透明開高低收面板。
    /// </summary>
    public class KLineChartControl : Grid
    {
        private readonly KLinePainter _painter;
        private readonly CrosshairOverlay _crosshair;
        private readonly Border _infoPanel;
        private readonly TextBlock _infoText;

        private readonly Border _priceTagBorder;
        private readonly TextBlock _priceText;
        private readonly TextBlock _countdownText;
        private readonly TranslateTransform _priceTagTransform;
        private readonly System.Windows.Threading.DispatcherTimer _priceTagTimer;

        private string _lastTickTimeStr = "";

        private List<KlineBar> _candles = [];
        private double _minX, _maxX;
        private double _minY, _maxY;

        // 智慧操作鎖：偵測使用者是否手動 zoom/pan 過，防止行情跳動時強制 autoRange 重對焦
        private bool _isZoomedOrPanned;
        private int _lastCandleCount = 0;
        private bool _isYAutoRanged = true; // Y 軸自動對焦狀態
        
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartMinX, _dragStartMaxX;
        private double _dragStartMinY, _dragStartMaxY;
        private bool _isYAxisDragging;
        private bool _isXAxisDragging;

        // 狀態快取，防止 UpdateCandles 時強制蓋掉十字游標所指的 K 棒
        private bool _isMouseInChart;
        private int _lastHoverIndex = -1;
        private bool _isLockedCrosshair;
        private double _lockedCrosshairPrice = 0;

        public KLineChartControl()
        {
            ClipToBounds = true; // 強制圖表大容器邊界剪裁
            // 1. 背景設為交易暗黑底色
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            // 2. 初始化 K 線 DX 繪圖層
            _painter = new KLinePainter();
            Children.Add(_painter);

            // 3. 初始化透明十字游標層
            _crosshair = new CrosshairOverlay();
            Children.Add(_crosshair);

            // 4. 初始化置頂開高低收面板 (完全不透明交易暗灰，科幻綠邊框)
            _infoText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Consolas, Microsoft JhengHei"),
                FontSize = 14,
                LineHeight = 16,
                TextWrapping = TextWrapping.NoWrap
            };

            _infoPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 204)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6),
                MinWidth = 145,
                MinHeight = 105,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(10, 10, 0, 0),
                Child = _infoText,
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.SizeAll // 提示可拖曳
            };

            var infoTransform = new TranslateTransform();
            _infoPanel.RenderTransform = infoTransform;

            bool isDraggingInfo = false;
            Point infoDragStartMouse = new();
            double infoDragStartX = 0;
            double infoDragStartY = 0;

            _infoPanel.MouseLeftButtonDown += (s, e) =>
            {
                isDraggingInfo = true;
                infoDragStartMouse = e.GetPosition(this);
                infoDragStartX = infoTransform.X;
                infoDragStartY = infoTransform.Y;
                _infoPanel.CaptureMouse();
                e.Handled = true; // 防止觸發底層的圖表平移
            };

            _infoPanel.MouseMove += (s, e) =>
            {
                if (isDraggingInfo)
                {
                    Point currentMouse = e.GetPosition(this);
                    double newX = infoDragStartX + (currentMouse.X - infoDragStartMouse.X);
                    double newY = infoDragStartY + (currentMouse.Y - infoDragStartMouse.Y);

                    double chartW = ActualWidth;
                    double chartH = ActualHeight;
                    double panelW = _infoPanel.ActualWidth > 0 ? _infoPanel.ActualWidth : 145;
                    double panelH = _infoPanel.ActualHeight > 0 ? _infoPanel.ActualHeight : 105;

                    // 限制只能在圖表範圍內移動，並且避開右側與底部的邊界
                    double minX = -10;
                    double maxX = chartW - panelW - 10 - KLinePainter.RightMargin;
                    double minY = -10;
                    double maxY = chartH - panelH - 10 - KLinePainter.BottomMargin;

                    infoTransform.X = Math.Max(minX, Math.Min(newX, maxX));
                    infoTransform.Y = Math.Max(minY, Math.Min(newY, maxY));
                    e.Handled = true;
                }
            };

            _infoPanel.MouseLeftButtonUp += (s, e) =>
            {
                if (isDraggingInfo)
                {
                    isDraggingInfo = false;
                    _infoPanel.ReleaseMouseCapture();
                    e.Handled = true;
                }
            };

            Children.Add(_infoPanel);

            // 當圖表尺寸改變時 (如視窗最大化/還原)，確保資訊面板不會跑出邊界而消失
            this.SizeChanged += (s, e) =>
            {
                double chartW = ActualWidth;
                double chartH = ActualHeight;
                if (chartW <= 0 || chartH <= 0) return;

                double panelW = _infoPanel.ActualWidth > 0 ? _infoPanel.ActualWidth : 145;
                double panelH = _infoPanel.ActualHeight > 0 ? _infoPanel.ActualHeight : 105;

                double minX = -10;
                double maxX = chartW - panelW - 10 - KLinePainter.RightMargin;
                double minY = -10;
                double maxY = chartH - panelH - 10 - KLinePainter.BottomMargin;

                infoTransform.X = Math.Max(minX, Math.Min(infoTransform.X, maxX));
                infoTransform.Y = Math.Max(minY, Math.Min(infoTransform.Y, maxY));
                
                UpdatePriceTag();
            };

            // 5. 動態價格與倒數小視窗
            _priceText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Microsoft JhengHei"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            };

            _countdownText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas, Microsoft JhengHei"),
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var tagStackPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };
            tagStackPanel.Children.Add(_priceText);
            tagStackPanel.Children.Add(_countdownText);

            _priceTagBorder = new Border
            {
                Width = KLinePainter.RightMargin,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Child = tagStackPanel,
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Opacity = 0.6
                }
            };

            _priceTagTransform = new TranslateTransform();
            _priceTagBorder.RenderTransform = _priceTagTransform;
            Children.Add(_priceTagBorder);

            _priceTagTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _priceTagTimer.Tick += (s, e) =>
            {
                if (_priceTagBorder.Visibility == Visibility.Visible)
                {
                    UpdatePriceTagText();
                }
            };
            _priceTagTimer.Start();

            // 6. 註冊滑鼠事件 (Zoom 與 Pan)
            MouseMove += KLineChartControl_MouseMove;
            MouseLeave += KLineChartControl_MouseLeave;
            MouseWheel += KLineChartControl_MouseWheel;
            MouseLeftButtonDown += KLineChartControl_MouseLeftButtonDown;
            MouseLeftButtonUp += KLineChartControl_MouseLeftButtonUp;
            MouseRightButtonDown += KLineChartControl_MouseRightButtonDown;
        }

        /// <summary>
        /// 更新並繪製 K 線，增量更新，免除銷毀重建物件開銷。
        /// </summary>
        public void UpdateCandles(List<KlineBar> klineData, bool forceAutoRange = false, string? currentTickTimeStr = null)
        {
            if (currentTickTimeStr != null) _lastTickTimeStr = currentTickTimeStr;
            _candles = klineData;

            if (forceAutoRange)
            {
                _isZoomedOrPanned = false; // 解鎖，重開全自動對焦模式
                _isYAutoRanged = true;
            }

            if (_candles == null || _candles.Count == 0)
            {
                _lastCandleCount = 0;
                _infoPanel.Visibility = Visibility.Collapsed;
                _painter.SetData([], 0, 0, 0, 0);
                UpdatePriceTag();
                return;
            }

            // 智慧自動對焦
            if (forceAutoRange || !_isZoomedOrPanned)
            {
                AutoRange();
                _lastCandleCount = _candles.Count;
            }
            else
            {
                int lastIdx = _candles.Count - 1;
                // 當手動縮放時，如果產生「新的一根 K 棒」，且使用者正在觀看最新區域，則 X 軸向右平移
                if (_lastCandleCount > 0 && _candles.Count > _lastCandleCount)
                {
                    int addedBars = _candles.Count - _lastCandleCount;
                    if (_maxX >= lastIdx - 2.0)
                    {
                        _minX += addedBars;
                        _maxX += addedBars;
                    }
                }
                _lastCandleCount = _candles.Count;

                // 動態調節功能：選項 A - 確保當前可見範圍內的 K 棒最高/最低價不會超出上下邊界 (Y 軸動態彈簧伸縮)
                if (_isYAutoRanged)
                {
                    AutoRangeYForVisibleX();
                }

                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            }

            if (_isLockedCrosshair && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                double newX = _painter.GetCanvasX(_lastHoverIndex, ActualWidth);
                double newY = _painter.GetCanvasY(_lockedCrosshairPrice, ActualHeight);
                _crosshair.SetMousePos(new Point(newX, newY));
            }

            // 若滑鼠正停留在畫面上觀察，則保持顯示游標所指的 K 棒資訊，否則更新顯示最新的一根
            if (_isMouseInChart && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                ShowKlineInfo(_lastHoverIndex);
            }
            else
            {
                ShowKlineInfo(_candles.Count - 1);
            }
            
            UpdatePriceTag();
        }

        /// <summary>
        /// 提供給 60FPS 渲染迴圈的極速更新接口。只修改最後一根 K 棒並觸發 Dirty Region 重繪，繞過背景 100ms 降頻。
        /// </summary>
        public void UpdateLastCandleInstant(double currentPrice, string tickTimeStr)
        {
            if (_candles == null || _candles.Count == 0) return;
            
            _lastTickTimeStr = tickTimeStr;
            int lastIdx = _candles.Count - 1;
            var last = _candles[lastIdx];
            
            bool changed = false;
            if (currentPrice > last.High)
            {
                last.High = currentPrice;
                changed = true;
            }
            if (currentPrice < last.Low)
            {
                last.Low = currentPrice;
                changed = true;
            }
            if (currentPrice != last.Close)
            {
                last.Close = currentPrice;
                last.Tag = last.Close >= last.Open ? "up" : "down";
                changed = true;
            }
            
            if (changed)
            {
                _candles[lastIdx] = last;
                _painter.InvalidateVisual();
                
                if (_isYAutoRanged && (currentPrice > _maxY || currentPrice < _minY))
                {
                    AutoRangeYForVisibleX();
                }
            }
            
            UpdatePriceTag();

            if (!_isMouseInChart || _lastHoverIndex == lastIdx)
            {
                ShowKlineInfo(lastIdx);
            }
        }

        /// <summary>
        /// 全自動對焦 X/Y 軸 ViewRange (加上 5% 上下緩衝間距以確保視覺美觀)
        /// </summary>
        public void AutoRange()
        {
            _isYAutoRanged = true;
            if (_candles == null || _candles.Count == 0) return;

            // X軸對焦：數學上的完美貼齊邊界。由於 K 棒的繪製寬度佔位是 0.6，
            // 設定左側從 -0.3 開始，能讓第 0 根 K 棒的左邊緣精準切在畫面 x=0。
            // 右側到 _candles.Count - 0.7 結束，能讓最後一根的右邊緣精準切在畫面最右側。
            _minX = -0.3;
            _maxX = _candles.Count - 0.7;

            // Y軸對焦：自動尋找此區間內的價格最高與最低
            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);

            if (startIdx > endIdx)
            {
                startIdx = 0;
                endIdx = _candles.Count - 1;
            }

            double highest = -999999;
            double lowest = 999999;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest) highest = _candles[i].High;
                if (_candles[i].Low < lowest) lowest = _candles[i].Low;
            }

            if (highest == -999999 || lowest == 999999)
            {
                highest = _candles.Max(c => c.High);
                lowest = _candles.Min(c => c.Low);
            }

            double height = highest - lowest;
            if (height <= 0) height = 1.0;

            // 上下剛好貼齊最高與最低價，完全包覆不留多餘空白
            _minY = lowest;
            _maxY = highest;

            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            UpdatePriceTag();
        }

        /// <summary>
        /// 解除手動鎖定並強制執行 AutoRange
        /// </summary>
        public void EnableAutoRange()
        {
            _isZoomedOrPanned = false;
            _isYAutoRanged = true;
            if (_candles != null && _candles.Count > 0)
            {
                AutoRange();
            }
        }

        /// <summary>
        /// 計算當前可視範圍內合理的 Y 軸最大可視高度，防止手動縮放時被壓扁成一條線。
        /// </summary>
        private double GetMaxAllowedRangeY()
        {
            if (_candles == null || _candles.Count == 0) return 10000.0;

            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);

            if (startIdx > endIdx)
            {
                startIdx = 0;
                endIdx = _candles.Count - 1;
            }

            double highest = -999999;
            double lowest = 999999;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest) highest = _candles[i].High;
                if (_candles[i].Low < lowest) lowest = _candles[i].Low;
            }

            if (highest == -999999 || lowest == 999999)
            {
                highest = _candles.Max(c => c.High);
                lowest = _candles.Min(c => c.Low);
            }

            double height = highest - lowest;
            if (height <= 0) height = 100.0;

            return Math.Max(100.0, height * 10.0); // 最大允許縮小到可視高低差的 10 倍
        }

        /// <summary>
        /// 手動調整 X 軸範圍時，智慧對焦 Y 軸可見價格高度。
        /// </summary>
        private void AutoRangeYForVisibleX()
        {
            if (_candles == null || _candles.Count == 0) return;

            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);

            if (startIdx > endIdx)
            {
                startIdx = 0;
                endIdx = _candles.Count - 1;
            }

            double highest = -999999;
            double lowest = 999999;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest) highest = _candles[i].High;
                if (_candles[i].Low < lowest) lowest = _candles[i].Low;
            }

            if (highest == -999999 || lowest == 999999)
                return;

            double height = highest - lowest;
            if (height <= 0) height = 1.0;

            // Y軸對焦：完全包覆，上下剛好貼齊最高與最低價，不留多餘空白
            _minY = lowest;
            _maxY = highest;
        }

        // 預建 Frozen 畫刷快取，供 ShowKlineInfo 高頻呼叫使用，消滅每次 new SolidColorBrush 的 GC 壓力
        private static readonly Brush _infoCyanBrush = CreateFrozenBrush(0, 255, 204);
        private static readonly Brush _infoRedBrush = CreateFrozenBrush(235, 75, 75);
        private static readonly Brush _infoGreenBrush = CreateFrozenBrush(40, 167, 69);
        private static readonly Brush _infoYellowBrush = CreateFrozenBrush(255, 215, 0);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 將指定 K棒 資料渲染成 HTML/簡潔文字並呈現在右上角不透明面板上。
        /// 使用預建 Frozen Brush 快取，避免高頻行情下大量 GC 分配。
        /// </summary>
        private void ShowKlineInfo(int index)
        {
            if (_candles == null || index < 0 || index >= _candles.Count)
            {
                _infoPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var c = _candles[index];
            _infoText.Inlines.Clear();
            
            // 標題 (時間)
            _infoText.Inlines.Add(new Run($"📊 {c.TimeLabel}\n") { Foreground = _infoCyanBrush, FontWeight = FontWeights.Bold });
            
            // 開盤
            _infoText.Inlines.Add(new Run("開："));
            _infoText.Inlines.Add(new Run($"{c.Open}\n") { FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            
            // 最高
            _infoText.Inlines.Add(new Run("高："));
            _infoText.Inlines.Add(new Run($"{c.High}\n") { FontWeight = FontWeights.Bold, Foreground = _infoRedBrush });
            
            // 最低
            _infoText.Inlines.Add(new Run("低："));
            _infoText.Inlines.Add(new Run($"{c.Low}\n") { FontWeight = FontWeights.Bold, Foreground = _infoGreenBrush });
            
            // 收盤
            Brush closeBrush = Brushes.White;
            if (c.Close > c.Open)
                closeBrush = _infoRedBrush;
            else if (c.Close < c.Open)
                closeBrush = _infoGreenBrush;

            _infoText.Inlines.Add(new Run("收："));
            _infoText.Inlines.Add(new Run($"{c.Close}") { FontWeight = FontWeights.Bold, Foreground = closeBrush });

            if (_painter.HighlightDirection != 0)
            {
                _infoText.Inlines.Add(new Run("\n")); // 空行區隔
                _infoText.Inlines.Add(new Run("\n方向："));
                if (_painter.HighlightDirection == 1)
                    _infoText.Inlines.Add(new Run("做多") { FontWeight = FontWeights.Bold, Foreground = _infoRedBrush });
                else
                    _infoText.Inlines.Add(new Run("做空") { FontWeight = FontWeights.Bold, Foreground = _infoGreenBrush });
            }

            if (_painter.HighlightPrice.HasValue)
            {
                _infoText.Inlines.Add(new Run($"\n開倉價："));
                _infoText.Inlines.Add(new Run($"{_painter.HighlightPrice.Value}") { FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            }

            if (_painter.StopLossPrice.HasValue)
            {
                _infoText.Inlines.Add(new Run($"\n停損價："));
                _infoText.Inlines.Add(new Run($"{_painter.StopLossPrice.Value}") { FontWeight = FontWeights.Bold, Foreground = _infoYellowBrush });
            }

            _infoPanel.Visibility = Visibility.Visible;
        }

        // ==================== 互動事件 (滑鼠 Drag & Wheel) ====================

        private void KLineChartControl_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_candles == null || _candles.Count == 0) return;

            Point mousePoint = e.GetPosition(this);
            double drawWidthLocal = Math.Max(1.0, ActualWidth - KLinePainter.RightMargin);
            if (mousePoint.X > drawWidthLocal) return; // 若點擊在 Y 軸區域則忽略

            // 切換為手動鎖定狀態，但保留 Y 軸動態追蹤以適應新行情
            _isZoomedOrPanned = true; 
            _isYAutoRanged = true; 

            // 1. X 軸：保持當前可視數量，將最後一根 K 棒對齊到點擊的 X 坐標
            int lastIdx = _candles.Count - 1;
            double rangeX = _maxX - _minX;
            double relativeX = mousePoint.X / drawWidthLocal;
            
            _minX = lastIdx - relativeX * rangeX;
            _maxX = _minX + rangeX;

            // 2. Y 軸：根據新的可見區間尋找最高與最低價
            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);

            if (startIdx > endIdx)
            {
                startIdx = 0;
                endIdx = _candles.Count - 1;
            }

            double highest = -999999;
            double lowest = 999999;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest) highest = _candles[i].High;
                if (_candles[i].Low < lowest) lowest = _candles[i].Low;
            }

            if (highest == -999999 || lowest == 999999)
            {
                highest = _candles.Max(c => c.High);
                lowest = _candles.Min(c => c.Low);
            }
            
            double height = highest - lowest;
            if (height <= 0) height = 1.0;
            
            // 上下各保留 5% 的視覺緩衝空間
            _minY = lowest - height * 0.05;
            _maxY = highest + height * 0.05;

            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            UpdatePriceTag();
        }

        private void KLineChartControl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_candles == null || _candles.Count == 0) return;

            if (e.ClickCount == 2)
            {
                _isYAutoRanged = true;
                _isZoomedOrPanned = false;
                AutoRange();
                return;
            }

            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartMinX = _minX;
            _dragStartMaxX = _maxX;
            _dragStartMinY = _minY;
            _dragStartMaxY = _maxY;

            double drawWidth = Math.Max(1.0, ActualWidth - KLinePainter.RightMargin);
            double drawHeight = Math.Max(1.0, ActualHeight - KLinePainter.BottomMargin);
            _isYAxisDragging = _dragStartPoint.X > drawWidth;
            _isXAxisDragging = _dragStartPoint.Y > drawHeight && _dragStartPoint.X <= drawWidth;

            CaptureMouse();
            
            _isZoomedOrPanned = true; // 鎖定手動狀態
        }

        private void KLineChartControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
        }

        private void KLineChartControl_MouseMove(object sender, MouseEventArgs e)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0 || _candles == null || _candles.Count == 0) return;

            Point mousePoint = e.GetPosition(this);

            // 1. 拖曳平移 (Pan) 與 XY軸縮放
            if (_isDragging)
            {
                if (_isYAxisDragging)
                {
                    double deltaY = mousePoint.Y - _dragStartPoint.Y;
                    double drawHeight = Math.Max(1.0, h - KLinePainter.BottomMargin);
                    
                    // 以滑鼠初始點擊位置的價格為縮放錨點
                    double startRangeY = _dragStartMaxY - _dragStartMinY;
                    double relativeY = _dragStartPoint.Y / drawHeight; // 0 是頂部(最高價), 1 是底部(最低價)
                    double anchorPrice = _dragStartMaxY - relativeY * startRangeY;

                    double scaleY = 1.0 + (deltaY / drawHeight) * 2.0; 
                    if (scaleY < 0.1) scaleY = 0.1;
                    if (scaleY > 10.0) scaleY = 10.0;
                    
                    double rangeY = startRangeY * scaleY;
                    
                    double maxAllowedY = GetMaxAllowedRangeY();
                    if (rangeY > maxAllowedY) rangeY = maxAllowedY;

                    // 錨點不變，重新計算上下邊界
                    _maxY = anchorPrice + rangeY * relativeY;
                    _minY = _maxY - rangeY;
                    
                    _isYAutoRanged = false; 
                }
                else if (_isXAxisDragging)
                {
                    double deltaX = mousePoint.X - _dragStartPoint.X;
                    double drawWidthLocal = Math.Max(1.0, w - KLinePainter.RightMargin);
                    
                    // 往右拖 (deltaX > 0) 放大，往左拖縮小
                    double scaleX = 1.0 - (deltaX / drawWidthLocal) * 1.5; 
                    if (scaleX < 0.1) scaleX = 0.1;
                    if (scaleX > 10.0) scaleX = 10.0;
                    
                    double startRangeX = _dragStartMaxX - _dragStartMinX;
                    double startRelativeX = _dragStartPoint.X / drawWidthLocal;
                    double anchorIndex = _dragStartMinX + startRelativeX * startRangeX;

                    double newRangeX = startRangeX * scaleX;
                    
                    double maxRangeByPixels = drawWidthLocal * 2.0;
                    double maxRangeByCount = Math.Max(30.0, _candles.Count * 1.2);
                    double maxRange = Math.Min(maxRangeByPixels, maxRangeByCount);
                    if (newRangeX > maxRange) newRangeX = maxRange;
                    if (newRangeX < 5.0) newRangeX = 5.0;

                    _minX = anchorIndex - newRangeX * startRelativeX;
                    _maxX = _minX + newRangeX;
                    
                    if (_isYAutoRanged)
                    {
                        AutoRangeYForVisibleX();
                    }
                }
                else
                {
                    double deltaX = mousePoint.X - _dragStartPoint.X;
                    double drawWidthLocal = Math.Max(1.0, w - KLinePainter.RightMargin);
                    double colW = drawWidthLocal / (_dragStartMaxX - _dragStartMinX);
                    double indexShift = deltaX / colW;

                    _minX = _dragStartMinX - indexShift;
                    _maxX = _dragStartMaxX - indexShift;

                    double deltaY = mousePoint.Y - _dragStartPoint.Y;
                    if (Math.Abs(deltaY) > 0)
                    {
                        if (_isYAutoRanged && Math.Abs(deltaY) > 15)
                        {
                            // 拖拉超過 15 像素，判定為蓄意上下平移，自動解除 Y 軸自動對焦
                            _isYAutoRanged = false;
                        }

                        if (!_isYAutoRanged)
                        {
                            double drawHeight = Math.Max(1.0, h - KLinePainter.BottomMargin);
                            double pricePerPixel = (_dragStartMaxY - _dragStartMinY) / drawHeight;
                            double priceShift = deltaY * pricePerPixel;
                            _minY = _dragStartMinY + priceShift;
                            _maxY = _dragStartMaxY + priceShift;
                        }
                    }
                }

                if (_isYAutoRanged && !_isYAxisDragging)
                {
                    AutoRangeYForVisibleX();
                }

                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
                UpdatePriceTag();
            }

            // 2. 十字游標物理移動
            _crosshair.SetMousePos(mousePoint);
            _isMouseInChart = true;
            _isLockedCrosshair = false;

            // 3. 計算滑鼠所在的 K棒 index
            double rangeX = _maxX - _minX;
            double drawWidth = Math.Max(1.0, w - KLinePainter.RightMargin);
            double relativeX = mousePoint.X / drawWidth;
            double floatIndex = relativeX * rangeX + _minX;

            int nearestIndex = (int)Math.Round(floatIndex);
            double distanceFromCenter = Math.Abs(floatIndex - nearestIndex);

            // K 線實體寬度比例為 0.6 (即半徑 0.3)。當游標觸碰到 K 線實體邊緣時，才切換對焦的 K 棒。
            if (distanceFromCenter <= 0.3)
            {
                _lastHoverIndex = nearestIndex;
            }

            if (_lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                ShowKlineInfo(_lastHoverIndex);
            }
            else
            {
                ShowKlineInfo(_candles.Count - 1);
            }
        }

        private void KLineChartControl_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseInChart = false;
            // 清空十字游標，資訊面板回歸最新一根 K棒
            _crosshair.SetMousePos(null);
            if (_candles != null && _candles.Count > 0)
            {
                ShowKlineInfo(_candles.Count - 1);
            }
        }

        private void KLineChartControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_candles == null || _candles.Count == 0) return;

            _isZoomedOrPanned = true; // 鎖定手動狀態

            double mouseX = e.GetPosition(this).X;
            double mouseY = e.GetPosition(this).Y;
            double w = ActualWidth;
            double h = ActualHeight;
            double drawWidthLocal = Math.Max(1.0, w - KLinePainter.RightMargin);
            double drawHeight = Math.Max(1.0, h - KLinePainter.BottomMargin);
            
            if (mouseX > drawWidthLocal)
            {
                // 在右側 Y 軸上滾輪 -> 縮放 Y 軸
                double rangeY = _maxY - _minY;
                double mousePrice = _maxY - (mouseY / drawHeight) * rangeY;
                double zoomFactorY = e.Delta > 0 ? 0.85 : 1.15;
                double newRangeY = rangeY * zoomFactorY;
                
                // 防呆，不縮到太小或太大
                if (newRangeY < 0.0001) newRangeY = 0.0001; 
                
                double maxAllowedY = GetMaxAllowedRangeY();
                if (newRangeY > maxAllowedY) newRangeY = maxAllowedY;

                double topRatio = (_maxY - mousePrice) / rangeY;
                _maxY = mousePrice + newRangeY * topRatio;
                _minY = _maxY - newRangeY;

                _isYAutoRanged = false;
            }
            else
            {
                // 在圖表上滾輪 -> 只縮放 X 軸 (游標為錨點)，Y 軸依據自動對焦狀態決定
                double rangeX = _maxX - _minX;

                double zoomFactor = e.Delta > 0 ? 0.85 : 1.15;
                double newRange = rangeX * zoomFactor;

                // 限制最大/最小縮放寬度
                if (newRange < 5.0) newRange = 5.0;
                
                // 限制最小縮放 (最大可視範圍)：避免數量少時產生巨大空白，同時允許看見所有K線
                double maxRange = Math.Max(30.0, _candles.Count * 1.2);
                
                if (newRange > maxRange) newRange = maxRange;

                // --- 游標錨點縮放 (TradingView 風格) ---
                double relativeX = mouseX / drawWidthLocal;
                double mouseIndex = _minX + relativeX * rangeX;

                _minX = mouseIndex - newRange * relativeX;
                _maxX = _minX + newRange;

                if (_isYAutoRanged)
                {
                    AutoRangeYForVisibleX();
                }
                // 如果是手動 Y 軸模式 (_isYAutoRanged == false)，則 Y 軸不動，完全對齊 TV 操作邏輯
            }

            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            UpdatePriceTag();

            if (_isLockedCrosshair && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                double newX = _painter.GetCanvasX(_lastHoverIndex, ActualWidth);
                double newY = _painter.GetCanvasY(_lockedCrosshairPrice, ActualHeight);
                _crosshair.SetMousePos(new Point(newX, newY));
            }
        }

        /// <summary>
        /// 安全釋放快取，防止 Zombie 殘留。
        /// </summary>
        public void Reset()
        {
            _painter.ResetCache();
            _candles.Clear();
            _isZoomedOrPanned = false;
            _isYAutoRanged = true;
            _isLockedCrosshair = false;
            _infoPanel.Visibility = Visibility.Collapsed;
            if (_priceTagBorder != null) _priceTagBorder.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 將圖表視界中心平移對焦到指定 K 線，並更新右上角面板與十字游標。
        /// </summary>
        public void FocusCandle(int index, int? price = null)
        {
            if (_candles == null || index < 0 || index >= _candles.Count) return;
            
            _isZoomedOrPanned = true; // 鎖定手動狀態，免除跳動時重對焦
            ShowKlineInfo(index);
            
            double colRange = _maxX - _minX;
            if (colRange <= 0) colRange = 100; // 防呆

            // 而非單純「繪圖區」的中心
            double R = 0.5;

            _minX = index - colRange * R;
            _maxX = index + colRange * (1.0 - R);
            
            AutoRangeYForVisibleX();
            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            UpdatePriceTag();

            double x = _painter.GetCanvasX(index, ActualWidth);
            double targetPrice = price ?? (_candles[index].Open + _candles[index].Close) / 2.0;
            double y = _painter.GetCanvasY(targetPrice, ActualHeight);
            _crosshair.SetMousePos(new Point(x, y));
            _lastHoverIndex = index;
            _isMouseInChart = true;
            _painter.HighlightPrice = price;
            _painter.HighlightIndex = index;
            
            _isLockedCrosshair = true;
            _lockedCrosshairPrice = targetPrice;
        }

        /// <summary>
        /// 設定停損價格黃色橫線與方向
        /// </summary>
        public void SetStopLossPrice(double? stopLossPrice, int direction = 0)
        {
            _painter.StopLossPrice = stopLossPrice;
            _painter.HighlightDirection = direction;
        }

        /// <summary>
        /// 設定極值觀測表選取的停損價格桃紅色橫線與方向
        /// </summary>
        public void SetObserverStopLossPrice(double? stopLossPrice, int direction = 0)
        {
            _painter.ObserverStopLossPrice = stopLossPrice;
            _painter.HighlightDirection = direction;
        }

        /// <summary>
        /// 僅設定高亮與十字游標位置，不平移畫面。用於切換時間級別時維持標記。
        /// </summary>
        public void SetHighlightIndexOnly(int index, int? price = null)
        {
            if (_candles == null || index < 0 || index >= _candles.Count) return;

            _painter.HighlightPrice = price;
            _painter.HighlightIndex = index;

            double targetPrice = price ?? (_candles[index].Open + _candles[index].Close) / 2.0;
            double x = _painter.GetCanvasX(index, ActualWidth);
            double y = _painter.GetCanvasY(targetPrice, ActualHeight);
            
            _crosshair.SetMousePos(new Point(x, y));
            _lastHoverIndex = index;
            _isMouseInChart = true;
            _isLockedCrosshair = true;
            _lockedCrosshairPrice = targetPrice;
        }

        /// <summary>
        /// 取得指定 Index 的 K 棒資料，供外部計算停損價
        /// </summary>
        public KlineBar? GetCandle(int index)
        {
            if (_candles == null || index < 0 || index >= _candles.Count) return null;
            return _candles[index];
        }

        private void UpdatePriceTag()
        {
            if (_candles == null || _candles.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            {
                if (_priceTagBorder != null) _priceTagBorder.Visibility = Visibility.Collapsed;
                return;
            }

            var lastCandle = _candles.Last();
            double latestPrice = lastCandle.Close;

            double y = _painter.GetCanvasY(latestPrice, ActualHeight);
            double x = ActualWidth - KLinePainter.RightMargin;

            _priceText.Text = latestPrice.ToString("F0");
            _priceTagBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            
            // 讓上半部的價格文字垂直置中對齊價格線
            double topOffset = _priceText.DesiredSize.Height / 2.0 + _priceTagBorder.Padding.Top;
            
            double tagY = y - topOffset;
            double tagHeight = _priceTagBorder.DesiredSize.Height;
            if (tagY < 0) tagY = 0;
            if (tagY + tagHeight > ActualHeight) tagY = ActualHeight - tagHeight;

            _priceTagTransform.X = x;
            _priceTagTransform.Y = tagY;
            _priceTagBorder.Visibility = Visibility.Visible;

            if (lastCandle.Close > lastCandle.Open)
                _priceTagBorder.Background = _infoRedBrush;
            else if (lastCandle.Close < lastCandle.Open)
                _priceTagBorder.Background = _infoGreenBrush;
            else
                _priceTagBorder.Background = Brushes.Black;

            UpdatePriceTagText();
        }

        private void UpdatePriceTagText()
        {
            if (_candles == null || _candles.Count == 0) return;
            var lastCandle = _candles.Last();

            string timeLabel = lastCandle.TimeLabel;
            string countdownStr = "--";
            
            if (!string.IsNullOrEmpty(timeLabel) && timeLabel.Contains('~') && !string.IsNullOrEmpty(_lastTickTimeStr) && _lastTickTimeStr.Length >= 6)
            {
                var parts = timeLabel.Split('~');
                if (parts.Length > 1)
                {
                    string endTimeStr = parts[1].Trim();
                    DateTime now = DateTime.Now; // fallback
                    
                    if (int.TryParse(_lastTickTimeStr.Substring(0, 2), out int th) &&
                        int.TryParse(_lastTickTimeStr.Substring(2, 2), out int tm) &&
                        int.TryParse(_lastTickTimeStr.Substring(4, 2), out int ts))
                    {
                        now = DateTime.Today.AddHours(th).AddMinutes(tm).AddSeconds(ts);
                        // 跨日處理
                        if (now > DateTime.Now.AddHours(12)) now = now.AddDays(-1);
                        else if (now < DateTime.Now.AddHours(-12)) now = now.AddDays(1);
                    }

                    DateTime endTime = now;
                    bool parsed = false;

                    if (endTimeStr.Length == 5 && endTimeStr.Contains(':'))
                    {
                        if (TimeSpan.TryParse(endTimeStr, out TimeSpan targetTs))
                        {
                            endTime = now.Date.Add(targetTs);
                            parsed = true;
                        }
                    }
                    else if (endTimeStr.Length >= 4)
                    {
                        if (int.TryParse(endTimeStr.Substring(0, 2), out int hh) && 
                            int.TryParse(endTimeStr.Substring(2, 2), out int mm))
                        {
                            endTime = now.Date.AddHours(hh).AddMinutes(mm);
                            parsed = true;
                        }
                    }

                    if (parsed)
                    {
                        if (endTime < now.AddHours(-12)) endTime = endTime.AddDays(1);
                        else if (endTime > now.AddHours(12)) endTime = endTime.AddDays(-1);

                        TimeSpan diff = endTime - now;
                        if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
                        countdownStr = $"{(int)diff.TotalMinutes:D2}:{diff.Seconds:D2}";
                    }
                }
            }

            _countdownText.Text = countdownStr;
        }

        /// <summary>
        /// 清除十字游標與重置資訊面板
        /// </summary>
        public void ClearCrosshair()
        {
            _crosshair.SetMousePos(null);
            _isMouseInChart = false;
            _isLockedCrosshair = false;
            _painter.HighlightPrice = null;
            _painter.HighlightIndex = null;
            _painter.StopLossPrice = null;
            _painter.HighlightDirection = 0;
            if (_candles != null && _candles.Count > 0)
            {
                ShowKlineInfo(_candles.Count - 1);
            }
            else
            {
                _infoPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 僅清除高亮狀態（白框與白線），但不隱藏十字游標
        /// </summary>
        public void ClearHighlightOnly()
        {
            _painter.HighlightPrice = null;
            _painter.HighlightIndex = null;
            _painter.StopLossPrice = null;
            _painter.HighlightDirection = 0;
        }

        /// <summary>
        /// 為了對應 PyQtGraph 屬性式呼叫的 Dummy ViewBox 屬性。
        /// </summary>
        public static DummyViewBox PlotWidget => new();
    }

    /// <summary>
    /// 對齊 PyQtGraph 程式碼的 Dummy ViewBox 結構。
    /// </summary>
    public class DummyViewBox
    {
        public static DummyViewBox PlotItem => new();
        public static DummyViewBox Vb => new();
        public DummyViewBox GetViewBox() => this;
        public static void EnableAutoRange() { }
    }
}
