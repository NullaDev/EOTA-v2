[CmdletBinding()]
param(
    [string]$GodotPath = $env:GODOT_EXE
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($GodotPath)) {
    $GodotPath = Join-Path (Split-Path -Parent $projectDirectory) 'Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) {
    throw "Godot .NET executable not found: $GodotPath. Set GODOT_EXE or pass -GodotPath."
}
$GodotPath = (Resolve-Path -LiteralPath $GodotPath).Path

Push-Location -LiteralPath $projectDirectory
try {
    & dotnet build (Join-Path $projectDirectory 'Eota.Godot.csproj') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $GodotPath --path $projectDirectory
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
