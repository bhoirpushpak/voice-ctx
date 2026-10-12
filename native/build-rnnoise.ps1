<#
.SYNOPSIS
  Builds rnnoise.dll (x64) from the pinned upstream RNNoise v0.2 release with MSVC.
.DESCRIPTION
  Downloads the official release tarball, verifies its SHA-256, compiles it with the
  Visual Studio C++ build tools and copies the DLL to src/VoiceCtx.Audio/Native/.
  Requires: Visual Studio 2022+ (or Build Tools) with the "Desktop development with C++" workload.
#>
[CmdletBinding()]
param([string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "voicectx-rnnoise-build"))

$ErrorActionPreference = 'Stop'
$Version = '0.2'
$Url = "https://github.com/xiph/rnnoise/releases/download/v$Version/rnnoise-$Version.tar.gz"
$Sha256 = '90FCE4B00B9FF24C08DBFE31B82FFD43BAE383D85C5535676D28B0A2B11C0D37'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$OutDir = Join-Path $RepoRoot 'src\VoiceCtx.Audio\Native'

Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$tarball = Join-Path $WorkDir "rnnoise-$Version.tar.gz"
Invoke-WebRequest $Url -OutFile $tarball -UseBasicParsing
$actual = (Get-FileHash $tarball -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw "SHA-256 mismatch for $Url`nexpected $Sha256`nactual   $actual" }
tar -xzf $tarball -C $WorkDir
$src = Join-Path $WorkDir "rnnoise-$Version"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 build tools not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$sources = 'denoise rnn pitch kiss_fft celt_lpc nnet nnet_default parse_lpcnet_weights rnnoise_data rnnoise_tables' -split ' ' |
    ForEach-Object { "src\$_.c" }
$cmd = @(
    "set `"PATH=%PATH%;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer`"",
    "`"$vcvars`" >nul",
    "cd /d `"$src`"",
    # MSVC never defines __SSE2__ on x64 (where SSE2 is guaranteed); without it RNNoise picks a
    # generic path that needs a header missing from the release tarball.
    "cl /nologo /LD /O2 /fp:fast /DNDEBUG /DWIN32 /D__SSE2__ /DRNNOISE_BUILD /DDLL_EXPORT /Iinclude /Isrc $($sources -join ' ') /Fe:rnnoise.dll /link /NOLOGO"
) -join ' && '
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "MSVC build failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Copy-Item (Join-Path $src 'rnnoise.dll') $OutDir -Force
Copy-Item (Join-Path $src 'COPYING') (Join-Path $OutDir 'RNNOISE-LICENSE.txt') -Force
Write-Host "Built $(Join-Path $OutDir 'rnnoise.dll')"
