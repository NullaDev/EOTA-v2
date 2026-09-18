param(
    [Parameter(Mandatory = $true)][string]$GodotPath,
    [string]$Version = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectSettings = Get-Content -LiteralPath (Join-Path $repositoryRoot 'project.godot') -Raw -Encoding UTF8
if (!$Version) {
    $match = [regex]::Match($projectSettings, '(?m)^config/version="([^"]+)"\r?$')
    if (!$match.Success) { throw 'Set config/version in project.godot or pass -Version.' }
    $Version = $match.Groups[1].Value
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must use major.minor.patch with an optional prerelease suffix, for example 1.1.0-pre-release.'
}
$GodotPath = (Resolve-Path -LiteralPath $GodotPath).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'exports' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "EOTA-v2-$Version-win-x64"
$packageDirectory = Join-Path $OutputDirectory $packageName
$archivePath = Join-Path $OutputDirectory "$packageName.zip"
$workDirectory = Join-Path $repositoryRoot ("artifacts/release-$Version-" + [Guid]::NewGuid().ToString('N'))
$stagingDirectory = Join-Path $workDirectory 'project'
foreach ($path in @($packageDirectory, $archivePath, $workDirectory)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path. Use another OutputDirectory to preserve the previous package of this version." }
}
$engineVersion = (& $GodotPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $engineVersion -notlike '4.6.2.stable.mono.*') {
    throw "Godot 4.6.2 .NET is required; found: $engineVersion"
}
New-Item -ItemType Directory -Path $packageDirectory, $stagingDirectory -Force | Out-Null
$savedNodeReuse = $env:MSBUILDDISABLENODEREUSE
$savedSharedCompilation = $env:UseSharedCompilation
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'

function Copy-ProjectTree([string]$RelativeDirectory) {
    $sourceDirectory = Join-Path $repositoryRoot $RelativeDirectory
    foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File -Force) {
        $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
        if ($relativePath -match '(^|[\\/])(bin|obj|\.godot)([\\/]|$)') { continue }
        $destination = Join-Path $stagingDirectory $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}

function Invoke-Godot([string[]]$EngineArguments, [string]$LogName) {
    $quotedArguments = $EngineArguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $GodotPath -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $workDirectory "$LogName-output.log") -RedirectStandardError (Join-Path $workDirectory "$LogName-error.log")
    try {
        # Keep the process handle open so Windows PowerShell retains its exit code.
        $null = $process.Handle
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(1000)) {
            if ($timer.Elapsed.TotalMinutes -gt 5) { throw "Godot $LogName timed out. See $workDirectory" }
        }
        if ($process.ExitCode -ne 0) { throw "Godot $LogName failed (exit $($process.ExitCode)). See $workDirectory" }
    } finally {
        if (!$process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}

Push-Location $repositoryRoot
try {
    # Import only an explicit copy of the current client. Local reference directories
    # are never scanned by Godot or written to during packaging.
    foreach ($directory in 'Client', 'src') { Copy-ProjectTree $directory }
    foreach ($file in 'project.godot', 'export_presets.cfg', 'Eota.Godot.csproj', 'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'packages.lock.json', 'icon.svg') {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $stagingDirectory
    }
    $stagedSettings = [regex]::Replace($projectSettings, '(?m)^config/version="[^"]*"\r?$', 'config/version="' + $Version + '"')
    if ($stagedSettings -notmatch '(?m)^config/version=') { $stagedSettings = $stagedSettings.Replace('[application]', "[application]`nconfig/version=`"$Version`"") }
    [IO.File]::WriteAllText((Join-Path $stagingDirectory 'project.godot'), $stagedSettings, [Text.UTF8Encoding]::new($false))
    dotnet new sln --name Eota.Godot --output $stagingDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Godot solution creation failed.' }
    dotnet sln (Join-Path $stagingDirectory 'Eota.Godot.sln') add (Join-Path $stagingDirectory 'Eota.Godot.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Godot project registration failed.' }
    $generatedDirectory = Join-Path $stagingDirectory 'Content/Generated'
    $compiledDirectory = Join-Path $workDirectory 'compiled-content'
    dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release -- build $repositoryRoot $compiledDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Content compilation failed.' }
    New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $compiledDirectory -File | Copy-Item -Destination $generatedDirectory
    foreach ($folder in 'Art', 'Icons') {
        $destination = Join-Path $generatedDirectory $folder
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "Content/Generated/$folder") -Filter '*.png' -File | Copy-Item -Destination $destination
    }
    $cards = Get-Content -LiteralPath (Join-Path $generatedDirectory 'cards.sources.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($card in $cards) {
        if (!(Test-Path -LiteralPath (Join-Path $stagingDirectory $card.texturePath))) { throw "Missing card art: $($card.id)" }
    }

    Invoke-Godot @('--headless', '--path', $stagingDirectory, '--editor', '--import', '--log-file', (Join-Path $workDirectory 'import.log')) 'import'
    Invoke-Godot @('--headless', '--path', $stagingDirectory, '--export-release', 'Windows Desktop', (Join-Path $packageDirectory 'EOTA.exe'), '--log-file', (Join-Path $workDirectory 'export.log')) 'export'
    dotnet publish (Join-Path $repositoryRoot 'src/Eota.Server.Host/Eota.Server.Host.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $packageDirectory 'Server/win-x64') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }

    # DesktopCatalog and the managed server use System.IO for these files.
    $contentDirectory = Join-Path $packageDirectory 'Content/Generated'
    New-Item -ItemType Directory -Path $contentDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $generatedDirectory -File | Where-Object { $_.Extension -in '.json', '.bin', '.md' } | Copy-Item -Destination $contentDirectory
    foreach ($folder in 'Art', 'Icons') {
        $destination = Join-Path $contentDirectory $folder
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $generatedDirectory $folder) -Filter '*.png' -File | Copy-Item -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Docs') -Destination (Join-Path $packageDirectory 'Docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $generatedDirectory 'CardTable.zh-CN.md') -Destination (Join-Path $packageDirectory 'Docs/CardTable.zh-CN.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Docs/Release-Readme.zh-CN.md') -Destination (Join-Path $packageDirectory 'README.zh-CN.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE')

    # Obtain the exact engine and bundled third-party notices from this engine.
    $noticeScript = Join-Path $workDirectory 'write-notices.gd'
    @'
extends SceneTree
func _initialize():
    var output = FileAccess.open(OS.get_cmdline_user_args()[0], FileAccess.WRITE)
    output.store_string(Engine.get_license_text() + "\n\n")
    output.store_string(JSON.stringify(Engine.get_copyright_info(), "  ") + "\n\n")
    output.store_string(JSON.stringify(Engine.get_license_info(), "  ") + "\n")
    output.close()
    quit()
'@ | Set-Content -LiteralPath $noticeScript -Encoding UTF8
    Invoke-Godot @('--headless', '--path', $stagingDirectory, '--script', $noticeScript, '--', (Join-Path $packageDirectory 'Docs/Godot-LICENSES.txt')) 'licenses'

    $contentManifest = Get-Content -LiteralPath (Join-Path $contentDirectory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    [ordered]@{
        version = $Version
        platform = 'windows-x86_64'
        configuration = 'Release'
        engine = $engineVersion
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        cards = $contentManifest.count
        ruleHash = $contentManifest.ruleHash
        selfContained = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-manifest.json') -Encoding UTF8
    foreach ($required in 'EOTA.exe', 'EOTA.pck', 'Server/win-x64/Eota.Server.Host.exe', 'Server/win-x64/coreclr.dll', 'Docs/Godot-LICENSES.txt', 'LICENSE') {
        if (!(Test-Path -LiteralPath (Join-Path $packageDirectory $required))) { throw "Missing release file: $required" }
    }
    if (!(Get-ChildItem -LiteralPath $packageDirectory -Directory -Filter 'data_*' | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'coreclr.dll') })) {
        throw 'The Godot client .NET runtime is missing.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
    Write-Output "Release archive: $archivePath"
    Write-Output "SHA256: $hash"
    Write-Output "Build logs: $workDirectory"
} finally {
    Pop-Location
    $env:MSBUILDDISABLENODEREUSE = $savedNodeReuse
    $env:UseSharedCompilation = $savedSharedCompilation
}
