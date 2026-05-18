param([int]$MaxSeconds = 1800, [int]$IntervalSeconds = 5, [string]$CsvPath = "C:\temp\mem-samples.csv")

# Streams samples to $CsvPath as they happen (one line per sample) so the
# graph can be rendered at any time without waiting for the loop to finish.
# Format: t_s,rss_mb,priv_mb,pid,name

if (Test-Path $CsvPath) { Remove-Item $CsvPath }
Add-Content -Path $CsvPath -Value "t_s,rss_mb,priv_mb,pid,name"

$start = Get-Date
$end = $start.AddSeconds($MaxSeconds)

while ((Get-Date) -lt $end) {
    # Score all candidate processes; "testhost" / "TianWen.Lib.Tests" / largest "dotnet" win.
    $p = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @('dotnet','testhost','TianWen.Lib.Tests') } |
        Sort-Object WorkingSet64 -Descending |
        Select-Object -First 1
    if ($null -ne $p) {
        $t = [int]((Get-Date) - $start).TotalSeconds
        $rss = [int]([math]::Round($p.WorkingSet64 / 1MB))
        $priv = [int]([math]::Round($p.PrivateMemorySize64 / 1MB))
        Add-Content -Path $CsvPath -Value ("{0},{1},{2},{3},{4}" -f $t, $rss, $priv, $p.Id, $p.ProcessName)
    }
    Start-Sleep -Seconds $IntervalSeconds
}
