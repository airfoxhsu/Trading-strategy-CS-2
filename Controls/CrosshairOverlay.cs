using System;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// 高性能十字游標透明疊加畫布。
    /// 滑鼠事件穿透，將十字游標的繪製物理隔離於 K 線主圖表之外，
    /// 滑鼠高頻滑動時底層圖表無需任何重繪，實現 0 延遲與極致流暢。
    /// </summary>
    public class CrosshairOverlay : FrameworkElement
    {
        private System.Windows.Point? _mousePos;
        private string? _timeString;
        private string? _priceString;
        private readonly System.Windows.Media.Pen _sciFiPen;
        private readonly System.Windows.Media.Brush _sciFiBrush;
        private readonly System.Windows.Threading.DispatcherTimer _throttleTimer;

        public CrosshairOverlay()
        {
            // 滑鼠事件穿透，不干涉底層圖表的拖曳與縮放
            IsHitTestVisible = false;

            // 預先建立科幻綠 `#00ffcc` 畫筆與畫刷
            var brush = new SolidColorBrush(Color.FromRgb(0, 255, 204));
            brush.Freeze();
            _sciFiBrush = brush;

            var pen = new System.Windows.Media.Pen(brush, 1.2)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
            };
            pen.Freeze();
            _sciFiPen = pen;

            // 16ms 節流定時器 (≈60FPS 上限)，合併高頻滑鼠事件，消滅 Repaint Storm
            _throttleTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _throttleTimer.Tick += (s, e) =>
            {
                _throttleTimer.Stop();
                InvalidateVisual(); // 觸發 WPF 的 OnRender
            };
        }

        /// <summary>
        /// 設定目前滑鼠座標與時間/價格文字，透過 16ms 節流合併重繪。
        /// </summary>
        public void SetMousePos(Point? pos, string? timeStr = null, string? priceStr = null)
        {
            _mousePos = pos;
            _timeString = timeStr;
            _priceString = priceStr;
            
            if (!_throttleTimer.IsEnabled)
            {
                _throttleTimer.Start();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (!_mousePos.HasValue)
                return;

            double w = ActualWidth;
            double h = ActualHeight;
            double x = _mousePos.Value.X;
            double y = _mousePos.Value.Y;

            // 只在 K 線繪圖區畫十字線 (避開右側 Y 軸與底部 X 軸保留區)
            double rightMargin = 55.0; // 同步 KLinePainter.RightMargin
            double bottomMargin = 20.0; // 同步 KLinePainter.BottomMargin
            
            // 建立剪裁區域，禁止游標畫進刻度區
            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(1.0, w - rightMargin), Math.Max(1.0, h - bottomMargin))));
            try
            {
                // 畫十字科幻綠虛線
                drawingContext.DrawLine(_sciFiPen, new Point(x, 0), new Point(x, Math.Max(0, h - bottomMargin)));
                drawingContext.DrawLine(_sciFiPen, new Point(0, y), new Point(Math.Max(0, w - rightMargin), y));

                // 於交叉點繪製一個實心科技小圓點
                drawingContext.DrawEllipse(_sciFiBrush, null, new Point(x, y), 3, 3);
            }
            finally
            {
                drawingContext.Pop();
            }

            // 在 Clip 區之外繪製 X/Y 軸標籤 (灰底白字，14pt，微亮灰外框，帶圓角)
            double pixelsPerDip = 1.0;
            try
            {
                pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch { }

            var bgBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)); // 好看的深灰色背景
            var borderPen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromRgb(110, 110, 115)), 1); // 微亮灰邊框
            bgBrush.Freeze();
            borderPen.Freeze();

            var fontTypeface = new Typeface("Segoe UI");

            // 1. 繪製 X 軸時間方框 (置於底部刻度區)
            if (!string.IsNullOrEmpty(_timeString))
            {
                var text = new FormattedText(
                    _timeString,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    fontTypeface,
                    14,
                    System.Windows.Media.Brushes.White,
                    pixelsPerDip);

                double rectW = text.Width + 12;
                double rectH = text.Height + 6;
                double rectX = x - rectW / 2;
                double rectY = h - bottomMargin + 1; // 稍微向下偏移 1 像素避免貼齊繪圖區邊界

                // 限制 rectX 範圍，防止超出左右邊界
                rectX = Math.Max(0, Math.Min(w - rightMargin - rectW, rectX));

                drawingContext.DrawRoundedRectangle(bgBrush, borderPen, new Rect(rectX, rectY, rectW, rectH), 3, 3);
                drawingContext.DrawText(text, new Point(rectX + 6, rectY + 3));
            }

            // 2. 繪製 Y 軸價位方框 (置於右側刻度區)
            if (!string.IsNullOrEmpty(_priceString))
            {
                var text = new FormattedText(
                    _priceString,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    fontTypeface,
                    14,
                    System.Windows.Media.Brushes.White,
                    pixelsPerDip);

                double rectW = text.Width + 12;
                double rectH = text.Height + 6;
                double rectX = w - rightMargin + 1; // 稍微向右偏移 1 像素避免貼齊繪圖區邊界
                double rectY = y - rectH / 2;

                // 限制 rectY 範圍，防止超出上下邊界
                rectY = Math.Max(0, Math.Min(h - bottomMargin - rectH, rectY));

                drawingContext.DrawRoundedRectangle(bgBrush, borderPen, new Rect(rectX, rectY, rectW, rectH), 3, 3);
                drawingContext.DrawText(text, new Point(rectX + 6, rectY + 3));
            }
        }
    }
}
