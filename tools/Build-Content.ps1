param([string]$OutputDirectory = '', [switch]$WithPng)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'Content/Generated' }
dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release -- build $repositoryRoot $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Content compilation failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Eota.ContentCli') -c Release --no-build -- table $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw 'Card table generation failed.' }
if ($WithPng) {
    node (Join-Path $PSScriptRoot 'EmojiArt/export.mjs') (Join-Path $repositoryRoot 'Content/Source/Art/emoji-recipes.json') (Join-Path $OutputDirectory 'Art')
    if ($LASTEXITCODE -ne 0) { throw 'PNG export failed.' }
    node (Join-Path $PSScriptRoot 'EmojiArt/export.mjs') (Join-Path $repositoryRoot 'Content/Source/Art/ui-icons.json') (Join-Path $OutputDirectory 'Icons')
    if ($LASTEXITCODE -ne 0) { throw 'Icon export failed.' }
}
Write-Output 'Content and card table updated.'
