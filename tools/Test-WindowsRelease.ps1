param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [ValidateSet('startup', 'local', 'scenes', 'ai', 'ui', 'decks', 'hosting', 'packages', 'isolation')]
    [string[]]$Checks = @('startup', 'local', 'scenes', 'ai', 'ui', 'decks', 'hosting', 'packages', 'isolation')
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$expectedHash = (Get-Content -LiteralPath "$ArchivePath.sha256" -Raw).Split(' ')[0].Trim()
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Archive checksum mismatch.' }
$workDirectory = Join-Path $repositoryRoot ('artifacts/release-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $workDirectory)
$packageDirectory = Join-Path $workDirectory ([IO.Path]::GetFileNameWithoutExtension($ArchivePath))
$executable = Join-Path $packageDirectory 'EOTA.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'The archive does not contain the expected game executable.' }
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'artifacts'), (Join-Path $workDirectory 'user-data') | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

function Start-ReleaseCheck([string]$Name, [string]$Arguments, [string]$WorkingDirectory = $packageDirectory) {
    $logPath = Join-Path $workDirectory "$Name.log"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.Arguments = '--log-file "' + $logPath + '" ' + $Arguments
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['APPDATA'] = Join-Path $workDirectory 'user-data'
    $start.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $workDirectory 'no-global-dotnet'
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $workDirectory 'no-global-dotnet'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    $ownedProcesses.Add($process)
    return $process
}

function Confirm-ReleaseCheck([Diagnostics.Process]$Process, [string]$Name, [string]$Marker, [int]$TimeoutSeconds = 120) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!$Process.HasExited) {
        if ($timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) { throw "$Name timed out. See $workDirectory" }
        Start-Sleep -Milliseconds 200
    }
    $logPath = Join-Path $workDirectory "$Name.log"
    if (!(Test-Path -LiteralPath $logPath)) { throw "$Name produced no log (exit $($Process.ExitCode))." }
    $log = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
    if ($Process.ExitCode -ne 0 -or $log -match '(?m)^(ERROR|SCRIPT ERROR):' -or ($Marker -and !$log.Contains($Marker))) {
        Write-Output $log
        throw "$Name failed (exit $($Process.ExitCode)). See $workDirectory"
    }
    Write-Output "$Name passed."
}

try {
    Write-Output "Validation directory: $workDirectory"
    $packRoot = Join-Path $packageDirectory 'Content/Packs'
    $ids = @(Get-ChildItem -LiteralPath $packRoot -Directory | Select-Object -ExpandProperty Name | Sort-Object)
    if (($ids -join ',') -ne 'standard,tutorial') { throw 'Release must contain exactly standard and tutorial.' }
    $defaultIds = Get-Content -LiteralPath (Join-Path $packageDirectory 'Content/default-selection.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($defaultIds -join ',') -ne 'standard') { throw 'Only standard may be enabled by default.' }
    foreach ($id in $ids) {
        $generated = Join-Path $packRoot "$id/Generated"
        $pack = Get-Content -LiteralPath (Join-Path $generated "$id.eotapack.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($asset in $pack.assets.PSObject.Properties) {
            if ($asset.Name -notmatch '^Art/[A-Za-z0-9._/-]+\.png$' -or $asset.Name.Contains('..')) { throw 'Unexpected package resource.' }
            if ((Get-FileHash -LiteralPath (Join-Path $generated $asset.Name) -Algorithm SHA256).Hash -ine $asset.Value) { throw "Bad resource hash: $id/$($asset.Name)" }
        }
        if ($id -eq 'standard' -and ($pack.cards.Count -ne 282 -or $pack.professions.Count -ne 6 -or $pack.assets.PSObject.Properties.Name.Count -ne 282)) { throw 'Standard inventory is incomplete.' }
        if ($id -eq 'tutorial' -and ($pack.cards.Count -ne 15 -or $pack.professions.Count -ne 1 -or $pack.dependencies.Count -ne 0)) { throw 'Tutorial must be standalone.' }
    }
    if (Get-ChildItem -LiteralPath (Join-Path $packageDirectory 'Docs') -Recurse -Filter '*.import') { throw 'Documentation contains Godot imports.' }
    if (Get-ChildItem -LiteralPath $packageDirectory -Recurse -Filter '*UIGuideline*') { throw 'Private UI notes were shipped.' }
    $packedText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $packageDirectory 'EOTA.pck')))
    if ($packedText.Contains('Content/Packs/')) { throw 'The PCK contains a package path; package content must remain external.' }
    # Launch outside the install directory to catch accidental working-directory dependencies.
    if ($Checks -contains 'startup') {
        $startup = Start-ReleaseCheck 'startup' '--headless --quit-after 60' $workDirectory
        Confirm-ReleaseCheck $startup 'startup' '' 30
    }
    if ($Checks -contains 'local') {
        $local = Start-ReleaseCheck 'local' '--headless -- --smoke'
        Confirm-ReleaseCheck $local 'local' 'P9_GODOT_SMOKE_OK' 45
    }
    if ($Checks -contains 'scenes') {
        $scenes = Start-ReleaseCheck 'scenes' '--headless -- --smoke-scene-lifetime'
        Confirm-ReleaseCheck $scenes 'scenes' 'SCENE_LIFETIME_SMOKE_OK' 120
    }
    if ($Checks -contains 'ai') {
        $ai = Start-ReleaseCheck 'ai' '--headless -- --smoke-ai'
        Confirm-ReleaseCheck $ai 'ai' 'P95_GODOT_AI_SMOKE_OK' 210
    }
    if ($Checks -contains 'ui') {
        $ui = Start-ReleaseCheck 'ui' '--resolution 1280x800 -- --smoke-ui-review'
        Confirm-ReleaseCheck $ui 'ui' 'P9_UI_REVIEW_SMOKE_OK' 90
    }
    if ($Checks -contains 'decks') {
        $decks = Start-ReleaseCheck 'decks' '--resolution 1280x800 -- --smoke-p10-decks'
        Confirm-ReleaseCheck $decks 'decks' 'P10_DECK_PROTOCOL_SMOKE_OK'
    }
    if ($Checks -contains 'hosting') {
        $invitation = Join-Path $workDirectory 'invitation.tmp'
        $hostingProcess = Start-ReleaseCheck 'host' ('--headless -- --smoke-p10-host --invitation-file "' + $invitation + '"')
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $invitation)) {
            if ($hostingProcess.HasExited -or $timer.Elapsed.TotalSeconds -gt 30) { throw "Host did not open a room. See $workDirectory" }
            Start-Sleep -Milliseconds 100
        }
        $joiningProcess = Start-ReleaseCheck 'join' ('--headless -- --smoke-p10-join --invitation-file "' + $invitation + '"')
        Confirm-ReleaseCheck $joiningProcess 'join' 'P10_HOSTING_SMOKE_OK' 105
        Confirm-ReleaseCheck $hostingProcess 'host' 'P10_HOSTING_SMOKE_OK' 45
    }
    if ($Checks -contains 'packages') {
        $packages = Start-ReleaseCheck 'packages' '--resolution 1600x1000 -- --smoke-content-packs'
        Confirm-ReleaseCheck $packages 'packages' 'CONTENT_PACK_SMOKE_OK'
    }
    if ($Checks -contains 'isolation') {
        # Move only the extracted verification copy, never the source project or release archive.
        $standard = [IO.Path]::GetFullPath((Join-Path $packRoot 'standard'))
        $removed = [IO.Path]::GetFullPath((Join-Path $workDirectory 'removed-standard'))
        if (!$standard.StartsWith($workDirectory + '\') -or !$removed.StartsWith($workDirectory + '\')) { throw 'Isolation move leaves verification directory.' }
        Move-Item -LiteralPath $standard -Destination $removed
        try {
            $isolated = Start-ReleaseCheck 'without-standard' '--resolution 1600x1000 -- --smoke-tutorial'
            Confirm-ReleaseCheck $isolated 'without-standard' 'TUTORIAL_SMOKE_OK'
        } finally { Move-Item -LiteralPath $removed -Destination $standard }
    }
    Write-Output "Selected packaged release checks passed: $($Checks -join ', '). Logs and screenshots: $workDirectory"
} finally {
    foreach ($process in $ownedProcesses) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}
