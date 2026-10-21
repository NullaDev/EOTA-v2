param([string]$OutputDirectory = '', [switch]$WithPng)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'Content/Packs/standard/Generated' }
if ($WithPng) {
    node (Join-Path $PSScriptRoot 'EmojiArt/export.mjs') (Join-Path $repositoryRoot 'Content/Packs/standard/Source/Art/emoji-recipes.json') (Join-Path $OutputDirectory 'Art')
    if ($LASTEXITCODE -ne 0) { throw 'PNG export failed.' }
    node (Join-Path $PSScriptRoot 'EmojiArt/export.mjs') (Join-Path $repositoryRoot 'Client/Assets/ui-icons.json') (Join-Path $repositoryRoot 'Client/Assets/Icons')
    if ($LASTEXITCODE -ne 0) { throw 'Icon export failed.' }
}
dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release -- build $repositoryRoot $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Content compilation failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release --no-build -- table $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw 'Card table generation failed.' }
Write-Output 'Content and card table updated.'
