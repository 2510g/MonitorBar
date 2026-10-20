$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here 'src\MonitorBar.cs'
$dist = Join-Path $here 'dist'
$out = Join-Path $dist 'MonitorBar.exe'

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw 'csc.exe'

$refs = @(
    (Join-Path $fw 'WPF\WindowsBase.dll')
    (Join-Path $fw 'WPF\PresentationCore.dll')
    (Join-Path $fw 'WPF\PresentationFramework.dll')
    (Join-Path $fw 'WPF\UIAutomationClient.dll')
    (Join-Path $fw 'WPF\UIAutomationTypes.dll')
    (Join-Path $fw 'System.Xaml.dll')
    (Join-Path $fw 'System.dll')
    (Join-Path $fw 'System.Core.dll')
    (Join-Path $fw 'System.Drawing.dll')
    (Join-Path $fw 'System.Windows.Forms.dll')
)

foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "missing reference: $r" } }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', "/out:$out")
foreach ($r in $refs) { $cscArgs += "/r:$r" }
$cscArgs += $src

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit $LASTEXITCODE" }

$len = (Get-Item $out).Length
Write-Host ("built {0} ({1:N0} bytes)" -f $out, $len)
