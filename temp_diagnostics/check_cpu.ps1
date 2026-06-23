Get-Process | Sort-Object CPU -Descending | Select-Object -First 25 Name, Id, CPU | Format-Table -AutoSize
