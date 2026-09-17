# P2 E1b (H11): the ESTIMATED-kernel oracle ceiling, paired row by row against E1's exact kernel.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED in docs/plans/deconvolver-training.md, "The next four", 2026-09-06. Restated here so
# the launch carries its own verdict criteria.
#
# METHOD. E1's 180 rows (6 masters, 3 channels, 5 injections, 2 noise arms), 60 RL iterations. Per row
#   PsfProfileFit on the observed crop (FWHM_obs, beta_obs) and on the clean crop (FWHM_clean), from
#   the frame's own detections; difference width sqrt(obs^2 - clean^2). Arm est-w: that width, beta
#   exact. Arm est-wb: width and beta both from the frame. Crop unfittable -> whole degraded frame,
#   flagged 'frame'. The exact arm runs in the same process on the same noise so d(r/t) is paired.
# PREDICTION. est-w within 0.03 of exact rec/truth through 1.6x and within 0.05 at 1.6-2.0x, ring
#   excess up by at most ten points; est-wb loses more, through ringing where beta is misread. Noisy
#   arm's stars column is the fabrication tell. Confidence MODERATE.
# KILL. est-wb rec/truth above 1.15 at 1.3-1.6x, or ring excess above 50 percent at 1.1-1.3x. Then
#   the unrolled-RL route is blocked on the ESTIMATOR, which becomes its own step, not a torch job.
# ADDED BEFORE LAUNCH (smoke, 1 master, 5 iterations): the 512 crop never supported a fit and several
#   NOISY whole-frame fits were refused, so the summary carries a no-fit count per bin. A refusal rate
#   that is high on the noisy arm is itself a kill-line finding: an estimator that cannot read the
#   frame at deployment depth blocks the route as surely as a wrong width does.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log while it runs:
#   $s = "<this file>"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'oracle-e1b.status'
$log = Join-Path $LogDir 'oracle-e1b.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8

Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '60'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated,estimated-shape'
try {
    # --no-build: the Release build is the one just made from the working tree; a rebuild here would
    # race nothing but costs a minute, and the head of the log records the commit it ran against.
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/TianWen.Lib.Tests/DeconvolutionOracleCeilingProbe.cs) -ne $null))" | Out-File $log -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" `
        --logger "console;verbosity=detailed" *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
