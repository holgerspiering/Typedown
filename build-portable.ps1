<#
.SYNOPSIS
    Builds a self-contained portable Typedown and packs it as a ZIP.

.DESCRIPTION
    1. Builds the web editor (Dev\Typedown.Editor) with yarn
    2. Publishes Typedown self-contained via MSBuild (Visual Studio Build Tools)
    3. Adds the "portable" marker file, so settings and data are stored in the program folder (Data\)
    4. Creates artifacts\Typedown-<version>-portable-<platform>.zip

.PARAMETER Platform
    Target platform, x64 (default) or x86.

.PARAMETER SkipEditor
    Skips the yarn build of the web editor and uses the existing Dev\Typedown\Resources\Statics.

.PARAMETER Force
    Cleans the output folder even if it contains a Data folder from a test run.

.EXAMPLE
    .\build-portable.ps1
    .\build-portable.ps1 -SkipEditor
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64',
    [switch]$SkipEditor,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE)"
    }
}

# Locate MSBuild from Visual Studio / Build Tools 2022
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw 'vswhere.exe not found. Install "Build Tools for Visual Studio 2022".'
}
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install "Build Tools for Visual Studio 2022".'
}

$version = ([xml](Get-Content (Join-Path $root 'Dev\Typedown\Typedown.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "Typedown-$version-portable-$Platform"
$artifacts = Join-Path $root 'artifacts'
$outDir = Join-Path $artifacts $name
$zipFile = "$outDir.zip"

# Clean the output folder, but never silently delete data from a test run
if (Test-Path $outDir) {
    if ((Test-Path (Join-Path $outDir 'Data')) -and -not $Force) {
        throw "$outDir contains a Data folder (settings from a test run). Back it up or run with -Force."
    }
    Remove-Item $outDir -Recurse -Force
}

if (-not $SkipEditor) {
    Push-Location (Join-Path $root 'Dev\Typedown.Editor')
    try {
        Invoke-Step 'Install editor dependencies' { yarn install --frozen-lockfile }
        Invoke-Step 'Build editor' { yarn build }
    }
    finally {
        Pop-Location
    }
}

Invoke-Step "Publish Typedown $version ($Platform, self-contained)" {
    & $msbuild (Join-Path $root 'Typedown.sln') '/t:Typedown:Publish' /restore /m /v:minimal /nologo `
        /p:Configuration=Release /p:Platform=$Platform /p:SelfContained=true "/p:PublishDir=$outDir\"
}

New-Item -ItemType File (Join-Path $outDir 'portable') -Force | Out-Null

Invoke-Step 'Create ZIP' {
    if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipFile -CompressionLevel Optimal
}

$sizeMb = [math]::Round((Get-Item $zipFile).Length / 1MB, 1)
Write-Host "Done: $zipFile ($sizeMb MB)" -ForegroundColor Green
