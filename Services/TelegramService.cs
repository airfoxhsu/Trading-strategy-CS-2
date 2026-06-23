using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// Telegram 背景非同步推播服務。
    /// 採用 Channel-Based 生產者-消費者模型，完全不卡住 UI 繪圖與即時行情。
    /// 配備安全的 Start/Stop 停機機制，防範程式退出時殘留殭屍執行緒。
    /// </summary>
    public class TelegramService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Channel<string> _msgChannel;
        private CancellationTokenSource? _cts;
        private Task? _processingTask;
        
        public string Token { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }

        public TelegramService()
        {
            _httpClient = new HttpClient();
            // 建立無限制大小的 Channel 通道
            _msgChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true, // 只有單一背景 Reader，減少 Contention
                SingleWriter = false
            });
        }

        /// <summary>
        /// 啟動背景非同步推播執行緒。
        /// </summary>
        public void Start()
        {
            if (_processingTask != null)
                return;

            _cts = new CancellationTokenSource();
            _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token), _cts.Token);
        }

        /// <summary>
        /// 安全終止背景推播執行緒，防止殭屍進程。
        /// </summary>
        public void Stop()
        {
            if (_processingTask == null)
                return;

            _cts?.Cancel();
            try
            {
                // 等待最多 1 秒讓正在發送的請求結束
                _processingTask.Wait(1000);
            }
            catch (AggregateException) { } // 忽略 Cancellation 被引發的預期例外
            
            _cts?.Dispose();
            _cts = null;
            _processingTask = null;
        }

        /// <summary>
        /// 執行緒安全地將訊息塞入 TG 推播通道中。
        /// </summary>
        public void PushMessage(string message)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ChatId))
                return;

            // 寫入通道 (非阻塞，O(1) 極速)
            _msgChannel.Writer.TryWrite(message);
        }

        /// <summary>
        /// 背景 Reader 迴圈。
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            var reader = _msgChannel.Reader;

            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var msg))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        await SendTelegramRequestAsync(msg, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[背景TG發送異常] {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 實際非同步發送 Telegram HTTP 請求。
        /// </summary>
        private async Task SendTelegramRequestAsync(string message, CancellationToken cancellationToken)
        {
            string url = $"https://api.telegram.org/bot{Token}/sendMessage";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", ChatId),
                new KeyValuePair<string, string>("text", message)
            });

            try
            {
                // 設定 5 秒超時，防止網路阻塞
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                var response = await _httpClient.PostAsync(url, content, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string errContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"Telegram 推播失敗，狀態碼: {response.StatusCode}, 錯誤: {errContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram 發送連線出錯: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            _httpClient.Dispose();
        }
    }
}
