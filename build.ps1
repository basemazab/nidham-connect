# Build Nidham Connect 2 with the C# compiler that ships inside every Windows
# (.NET Framework 4.x) - no SDK, no Node, no Electron.
# Output: dist\Nidham-Connect.exe
#
#   .\build.ps1            build
#   .\build.ps1 -Test      build + run test\e2e.mjs (needs node)
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads a BOM-less .ps1 as
# ANSI, so a non-ASCII character here becomes a parser error, not a typo.
param([switch]$Test)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe not found - .NET Framework 4.x is required" }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist "Nidham-Connect.exe"
if (Test-Path $out) { Remove-Item $out -Force }

$srcs = Get-ChildItem (Join-Path $root "src\*.cs") | ForEach-Object { $_.FullName }

& $csc -nologo -codepage:65001 -optimize+ -target:winexe -platform:anycpu `
  "-win32icon:$root\build\icon.ico" "-win32manifest:$root\build\app.manifest" `
  "-out:$out" `
  -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll `
  -r:System.Net.Http.dll -r:System.Web.Extensions.dll -r:Microsoft.CSharp.dll `
  $srcs | Where-Object { $_ -and ($_ -notmatch "language versions") }

if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
$size = (Get-Item $out).Length
Write-Host ("built " + $out + " (" + $size.ToString("N0") + " bytes)")

if ($Test) {
  Push-Location $root
  try {
    node (Join-Path $root "test\e2e.mjs")
    if ($LASTEXITCODE -ne 0) { throw ("e2e failed (" + $LASTEXITCODE + ")") }
  } finally { Pop-Location }
}
