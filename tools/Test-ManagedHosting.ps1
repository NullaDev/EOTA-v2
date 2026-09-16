param([Parameter(Mandatory = $true)][string]$GodotPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$invitePath = Join-Path $repositoryRoot ('artifacts/p10-invite-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$hostProcess = $null
$joinProcess = $null
try {
    $hostProcess = Start-Process -FilePath $GodotPath -WorkingDirectory $repositoryRoot -ArgumentList @('--path','.', '--resolution','1600x1000','--log-file','artifacts/p10-host.log','--','--smoke-p10-host','--invitation-file',('"' + $invitePath + '"')) -WindowStyle Hidden -PassThru
    $readinessTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $invitePath)) {
        if ($hostProcess.HasExited -or $readinessTimer.Elapsed.TotalSeconds -gt 30) { throw 'Host did not produce invitation' }
        Start-Sleep -Milliseconds 100
    }
    $joinProcess = Start-Process -FilePath $GodotPath -WorkingDirectory $repositoryRoot -ArgumentList @('--path','.', '--resolution','1280x800','--log-file','artifacts/p10-join.log','--','--smoke-p10-join','--invitation-file',('"' + $invitePath + '"')) -WindowStyle Hidden -PassThru
    if (-not $joinProcess.WaitForExit(55000)) { throw 'Join timed out' }
    if (-not $hostProcess.WaitForExit(30000)) { throw 'Host recovery timed out' }
    Get-Content (Join-Path $repositoryRoot 'artifacts/p10-host.log') -Encoding UTF8 -Tail 12
    Get-Content (Join-Path $repositoryRoot 'artifacts/p10-join.log') -Encoding UTF8 -Tail 12
    if ($hostProcess.ExitCode -ne 0 -or $joinProcess.ExitCode -ne 0) { throw 'Hosted match smoke failed' }
} finally {
    foreach ($ownedProcess in @($hostProcess, $joinProcess)) {
        if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited) { Stop-Process -Id $ownedProcess.Id }
    }
    if (Test-Path -LiteralPath $invitePath) { Remove-Item -LiteralPath $invitePath }
    if (Test-Path -LiteralPath ($invitePath + '.done')) { Remove-Item -LiteralPath ($invitePath + '.done') }
}
