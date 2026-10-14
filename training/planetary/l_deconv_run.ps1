# L-DECONV grid, started by the scheduled task TianWenDeconvGrid every 10 minutes; resumes from kept logs, exits once run.done exists.
Set-Location D:/temp/tianwen-scratch/i1056/deploy/deconv
if (Test-Path run.done) { exit 0 }
Get-ChildItem -Recurse -Filter *.part | Remove-Item -Force
"started $(Get-Date -Format o) pid $PID" | Out-File -FilePath status.txt -Append -Encoding utf8
python -u D:/temp/tianwen-scratch/i1056/deploy/deconv/deconv.py run 3 >> run.out 2>> run.err
"ended $(Get-Date -Format o) exit $LASTEXITCODE" | Out-File -FilePath status.txt -Append -Encoding utf8
