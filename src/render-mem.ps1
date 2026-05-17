param([string]$CsvPath = "C:\temp\mem-samples.csv", [int]$BarWidth = 50)

if (-not (Test-Path $CsvPath)) { Write-Output "no csv at $CsvPath"; exit }

$rows = Import-Csv $CsvPath
if ($rows.Count -eq 0) { Write-Output "no samples yet"; exit }

$maxRss = ($rows | Measure-Object -Property rss_mb -Maximum).Maximum
$maxT = ($rows | Measure-Object -Property t_s -Maximum).Maximum

Write-Output ("samples: {0}, span: {1}s, peak RSS: {2} MB" -f $rows.Count, $maxT, $maxRss)
Write-Output ""
Write-Output ("t(s)     RSS_MB  bar [{0}={1} MB]" -f ('#' * $BarWidth), $maxRss)
Write-Output ("----     ------  " + ('-' * $BarWidth))

foreach ($r in $rows) {
    $rss = [int]$r.rss_mb
    $barLen = if ($maxRss -gt 0) { [int](($rss / $maxRss) * $BarWidth) } else { 0 }
    $bar = '#' * $barLen
    Write-Output ("{0,4}     {1,6}  {2}" -f $r.t_s, $rss, $bar)
}
