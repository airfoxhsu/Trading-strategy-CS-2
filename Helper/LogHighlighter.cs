using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 即時日誌著色高亮器。
    /// 採用增量式 O(1) 插入著色設計，在文字寫入瞬間直接將對應的前景色套用在 Run 元件上，
    /// 徹底拋棄昂貴的全篇幅 Regex 正則後處理著色，完美保證 UI 流暢度。
    /// </summary>
    public static class LogHighlighter
    {
        // 預建的 SolidColorBrush 畫刷物件池，消滅臨時分配的 GC 負擔
        private static readonly SolidColorBrush UpBrush = new(Color.FromRgb(235, 75, 75));     // 亮紅
        private static readonly SolidColorBrush DownBrush = new(Color.FromRgb(40, 167, 69));   // 亮綠
        private static readonly SolidColorBrush SystemBrush = new(Color.FromRgb(0, 162, 237));  // 亮青
        private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(220, 220, 220)); // 灰白

        private static ScrollViewer? GetScrollViewer(System.Windows.DependencyObject depObj)
        {
            if (depObj is ScrollViewer sv) return sv;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private static Paragraph CreateLineParagraph(string line)
        {
            var p = new Paragraph { Margin = new System.Windows.Thickness(0, 2, 0, 2) };
            
            if (string.IsNullOrEmpty(line))
            {
                p.Inlines.Add(new Run(""));
                return p;
            }

            bool hasRedTag = line.Contains("[C:RED]");
            bool hasGreenTag = line.Contains("[C:GREEN]");

            if (hasRedTag) line = line.Replace("[C:RED]", "");
            if (hasGreenTag) line = line.Replace("[C:GREEN]", "");

            var run = new Run(line);

            if (hasRedTag)
            {
                run.Foreground = UpBrush; // 紅色
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (hasGreenTag)
            {
                run.Foreground = DownBrush; // 綠色
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (line.Contains("未達標"))
            {
                run.Foreground = DefaultBrush;
            }
            else if (line.Contains("最高") || line.Contains("K低"))
            {
                run.Foreground = DownBrush; // Python 做空/綠色高亮
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (line.Contains("最低") || line.Contains("K高"))
            {
                run.Foreground = UpBrush;   // Python 做多/紅色高亮
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (line.Contains("共識推播") || line.Contains("觸發推播") || 
                     line.Contains("行情狀態") || line.Contains("預載") ||
                     line.Contains("系統"))
            {
                run.Foreground = SystemBrush; // 青色高亮
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else
            {
                run.Foreground = DefaultBrush;
            }

            p.Inlines.Add(run);
            return p;
        }

        /// <summary>
        /// 執行緒安全地向 RichTextBox 增量添加一行已被著色的日誌。
        /// 支援差量更新 (clear=true 時會啟用快取比對)，大幅降低 WPF Measure/Arrange 負擔。
        /// </summary>
        public static void AppendLog(System.Windows.Controls.RichTextBox rtb, string text, bool clear = false, bool forceScrollToEnd = false)
        {
            if (rtb == null) return;

            var scrollViewer = GetScrollViewer(rtb);
            bool isAtBottom = true;
            double currentOffset = 0;

            if (scrollViewer != null)
            {
                // 判斷目前是否在最底部 (容許 5px 誤差)
                isAtBottom = scrollViewer.VerticalOffset >= (scrollViewer.ScrollableHeight - 5.0);
                currentOffset = scrollViewer.VerticalOffset;
            }

            var document = rtb.Document;

            string cleanText = text.TrimEnd('\r', '\n');
            string[] newLines = string.IsNullOrEmpty(cleanText) ? Array.Empty<string>() : cleanText.Replace("\r", "").Split('\n');

            if (clear)
            {
                // ========== 差量更新邏輯 (取代暴力 Blocks.Clear()) ==========
                int existingCount = document.Blocks.Count;
                int newCount = newLines.Length;
                
                // 若差距太大或現有行數超過 500 (異常狀態)，直接暴力清空重建比較快
                if (existingCount > 500 || existingCount == 0 || newCount == 0)
                {
                    document.Blocks.Clear();
                    foreach (var line in newLines)
                    {
                        document.Blocks.Add(CreateLineParagraph(line));
                    }
                }
                else
                {
                    // 就地修改/新增/刪除 Paragraph
                    var currentBlocks = document.Blocks.ToList();
                    
                    for (int i = 0; i < Math.Max(existingCount, newCount); i++)
                    {
                        if (i < newCount && i < existingCount)
                        {
                            // 修改既有段落 (避免重建 Paragraph 樹節點)
                            var p = (Paragraph)currentBlocks[i];
                            var currentText = new TextRange(p.ContentStart, p.ContentEnd).Text;
                            // 只有當文字真的改變時才重新賦值 (WPF TextRange 讀出來會有 \r\n，這裡簡單做 StartsWith 判斷)
                            if (string.IsNullOrEmpty(currentText) || !currentText.StartsWith(newLines[i]))
                            {
                                var newP = CreateLineParagraph(newLines[i]);
                                document.Blocks.InsertBefore(p, newP);
                                document.Blocks.Remove(p);
                            }
                        }
                        else if (i < newCount && i >= existingCount)
                        {
                            // 追加新段落
                            document.Blocks.Add(CreateLineParagraph(newLines[i]));
                        }
                        else if (i >= newCount && i < existingCount)
                        {
                            // 刪除多餘段落
                            document.Blocks.Remove(currentBlocks[i]);
                        }
                    }
                }
            }
            else
            {
                // 內存防護：限制日誌總行數 (不超過 500 行)，防止 WPF 佈局引擎隨長度線性退化
                if (document.Blocks.Count > 500)
                {
                    // 刪除頭部的 200 行
                    for (int i = 0; i < 200; i++)
                    {
                        if (document.Blocks.FirstBlock != null)
                        {
                            document.Blocks.Remove(document.Blocks.FirstBlock);
                        }
                    }
                }

                // Append 模式直接追加
                foreach (var line in newLines)
                {
                    document.Blocks.Add(CreateLineParagraph(line));
                }
            }

            // 智慧捲動：只有在原本已經在最底部時，或強制指定時，才跟著往下捲動
            // 💡 必須使用 DispatcherPriority.Loaded 確保 WPF 已經重新計算好 ScrollableHeight
            if (isAtBottom || forceScrollToEnd)
            {
                rtb.Dispatcher.InvokeAsync(() =>
                {
                    rtb.ScrollToEnd();
                    rtb.CaretPosition = rtb.Document.ContentEnd;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (scrollViewer != null)
            {
                // 若使用者正在往上捲動查看歷史，則保持原本的捲動位置
                rtb.Dispatcher.InvokeAsync(() =>
                {
                    scrollViewer.ScrollToVerticalOffset(currentOffset);
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }
}
