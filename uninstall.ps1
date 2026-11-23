# Removes Retro Todo, its start-on-boot entry and its shortcuts.
# Your todos (%APPDATA%\RetroTodo) are kept unless you pass -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'

$dest   = Join-Path $env:LOCALAPPDATA 'Programs\RetroTodo'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

function Get-Running {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='RetroTodo.exe'" |
        Where-Object { $_.Name -eq 'RetroTodo.exe' -or $_.CommandLine -match 'RetroTodo\.dll' }
}

if ((Get-Running) -and (Test-Path "$dest\RetroTodo.dll")) {
    & $dotnet "$dest\RetroTodo.dll" --quit
    Start-Sleep -Milliseconds 1500
}
Get-Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RetroTodo -ErrorAction SilentlyContinue
foreach ($folder in 'Programs', 'Desktop') {
    Remove-Item (Join-Path ([Environment]::GetFolderPath($folder)) 'Retro Todo.lnk') -ErrorAction SilentlyContinue
}
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:APPDATA 'RetroTodo') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host '> Retro Todo removed.' -ForegroundColor Green
