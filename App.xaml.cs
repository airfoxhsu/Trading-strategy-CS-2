using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace ExtremeSignalAppCS;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 註冊 CodePages 編碼提供者以支援 big5 等繁體中文日誌編碼
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            // 殭屍程序防制機制：啟動時自動檢測並清理同名舊殘留程序
            var current = Process.GetCurrentProcess();
            string procName = current.ProcessName;

            // 確保只針對本專案的執行檔進行清理，避免誤殺 dotnet.exe 或除錯器
            if (procName.Contains("ExtremeSignalAppCS", StringComparison.OrdinalIgnoreCase))
            {
                string currentFilePath = current.MainModule?.FileName ?? string.Empty;

                var running = Process.GetProcessesByName(procName)
                                     .Where(p => p.Id != current.Id);
                
                foreach (var p in running)
                {
                    try
                    {
                        // 確保只有相同資料夾下的同名程式，才會被視為殭屍程序殺死
                        if (!string.IsNullOrEmpty(currentFilePath) && p.MainModule?.FileName == currentFilePath)
                        {
                            p.Kill();
                            p.WaitForExit(300); // 縮短等待時間，快速通過
                        }
                    }
                    catch
                    {
                        // 忽略權限存取（例如存取 MainModule 被拒）等異常
                    }
                }
            }
        }
        catch
        {
            // 確保整個防制機制即使出錯也絕不阻礙主程式啟動！
        }

        base.OnStartup(e);
    }
}

