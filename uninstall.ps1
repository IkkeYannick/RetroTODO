# Removes Retro Todo, its start-on-boot entry and its Start menu shortcut.
# Your todos (%APPDATA%\RetroTodo) are kept unless you pass -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Programs\RetroTodo'
$exe  = Join-Path $dest 'RetroTodo.exe'

if ((Get-Process RetroTodo -ErrorAction SilentlyContinue) -and (Test-Path $exe)) {
    Start-Process $exe -ArgumentList '--quit' -Wait
    Start-Sleep -Milliseconds 1000
}
Get-Process RetroTodo -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RetroTodo -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Retro Todo.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:APPDATA 'RetroTodo') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host '> Retro Todo removed.' -ForegroundColor Green
