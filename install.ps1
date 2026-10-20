param(
    [switch]$Autostart
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'dist\MonitorBar.exe'
if (-not (Test-Path $src)) { throw "先运行 build.ps1 生成 $src" }

$targetDir = Join-Path $env:LOCALAPPDATA 'MonitorBar'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
$target = Join-Path $targetDir 'MonitorBar.exe'

Get-Process -Name MonitorBar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Copy-Item $src $target -Force
Write-Host "已复制到 $target"

# 开始菜单快捷方式
try {
    $programs = [Environment]::GetFolderPath('Programs')
    $lnkPath = Join-Path $programs '显示器控制.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $targetDir
    $lnk.Description = '调节显示器亮度/对比度/音量/输入源'
    $lnk.Save()
    Write-Host "已创建开始菜单快捷方式：$lnkPath"
} catch {
    Write-Warning "开始菜单快捷方式创建失败：$($_.Exception.Message)"
}

# 开机启动
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Autostart) {
    Set-ItemProperty -Path $runKey -Name 'MonitorBar' -Value "`"$target`""
    Write-Host '已设置开机自动启动'
} elseif (Get-ItemProperty -Path $runKey -Name 'MonitorBar' -ErrorAction SilentlyContinue) {
    Write-Host '检测到已有开机启动项（保留）'
}

# 启动（用 WMI 创建进程，避免继承当前终端的句柄而把终端挂住）
try {
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '"' + $target + '"' }
    if ($created.ReturnValue -ne 0) { throw "Win32_Process.Create 返回 $($created.ReturnValue)" }
    Write-Host '已启动，图标在任务栏右下角（可能在 ^ 溢出区里）'
} catch {
    try { Start-Process -FilePath $target | Out-Null; Write-Host '已启动' }
    catch { Write-Warning "自动启动失败，请手动双击 $target" }
}
