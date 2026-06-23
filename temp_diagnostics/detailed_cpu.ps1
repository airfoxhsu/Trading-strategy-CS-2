# 檢查 ExtremeSignalAppCS 的執行緒 CPU 使用狀況
Write-Host "===== ExtremeSignalAppCS 程序分析 =====" -ForegroundColor Cyan

# 用 Get-Process 檢查所有 ExtremeSignalAppCS 實例
$procs = Get-Process -Name "ExtremeSignalAppCS" -ErrorAction SilentlyContinue
foreach ($p in $procs) {
    Write-Host "`nPID: $($p.Id)" -ForegroundColor Yellow
    Write-Host "  CPU 時間(秒): $($p.CPU)"
    Write-Host "  記憶體(MB): $([math]::Round($p.WorkingSet64/1MB, 1))"
    Write-Host "  執行緒數: $($p.Threads.Count)"
    Write-Host "  啟動時間: $($p.StartTime)"
    
    # 列出每個執行緒的 CPU 使用
    $topThreads = $p.Threads | Sort-Object TotalProcessorTime -Descending | Select-Object -First 10
    Write-Host "  前10高 CPU 執行緒:" -ForegroundColor Green
    foreach ($t in $topThreads) {
        try {
            $cpuSec = $t.TotalProcessorTime.TotalSeconds
            $state = $t.ThreadState
            $waitReason = if ($state -eq 'Wait') { $t.WaitReason } else { 'N/A' }
            Write-Host "    Thread $($t.Id): CPU=$([math]::Round($cpuSec, 2))s  State=$state  WaitReason=$waitReason"
        } catch {
            Write-Host "    Thread $($t.Id): (無法讀取)"
        }
    }
}

# 同時檢查 TaskManager 本身是否佔用大量 CPU
Write-Host "`n===== TaskMgr 狀態 =====" -ForegroundColor Cyan
$tm = Get-Process -Name "Taskmgr" -ErrorAction SilentlyContinue
if ($tm) {
    Write-Host "TaskMgr PID: $($tm.Id), CPU: $($tm.CPU)s -- 注意：工作管理員本身高 CPU 可能因為即時監控導致"
}

# 檢查有多少個 dotnet 程序
Write-Host "`n===== dotnet 程序 =====" -ForegroundColor Cyan
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  dotnet PID: $($_.Id), CPU: $($_.CPU)s, 記憶體: $([math]::Round($_.WorkingSet64/1MB,1))MB"
}
