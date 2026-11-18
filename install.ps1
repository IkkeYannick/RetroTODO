# Builds Retro Todo, installs it to %LOCALAPPDATA%\Programs\RetroTodo,
# makes it start with Windows (as the overlay), adds a Start menu shortcut and launches it.
#   .\install.ps1                 normal install
#   .\install.ps1 -NoAutostart    install without start-on-boot
#   .\install.ps1 -NoLaunch       don't start it right now
param([switch]$NoAutostart, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$dest   = Join-Path $env:LOCALAPPDATA 'Programs\RetroTodo'
$exe    = Join-Path $dest 'RetroTodo.exe'

Write-Host '> building...' -ForegroundColor Green
& $dotnet publish "$root\src\RetroTodo.csproj" -c Release -o "$root\publish" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (Get-Process RetroTodo -ErrorAction SilentlyContinue) {
    Write-Host '> closing the running copy...' -ForegroundColor Green
    Start-Process "$root\publish\RetroTodo.exe" -ArgumentList '--quit' -Wait
    Start-Sleep -Milliseconds 1000
    Get-Process RetroTodo -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host "> installing to $dest" -ForegroundColor Green
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\publish\*" $dest -Recurse -Force

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutostart) {
    Remove-ItemProperty $run -Name RetroTodo -ErrorAction SilentlyContinue
    Write-Host '> start with Windows: OFF' -ForegroundColor Green
} else {
    Set-ItemProperty $run -Name RetroTodo -Value "`"$exe`" --startup"
    Write-Host '> start with Windows: ON (opens as the overlay)' -ForegroundColor Green
}

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Retro Todo.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.Save()
Write-Host '> Start menu shortcut: Retro Todo' -ForegroundColor Green

if (-not $NoLaunch) { Start-Process $exe -ArgumentList "--startup" }   # opens as the overlay; nothing steals focus
Write-Host '> done. Your todos live in %APPDATA%\RetroTodo\todos.json' -ForegroundColor Green
