# Builds Retro Todo, installs it to %LOCALAPPDATA%\Programs\RetroTodo,
# makes it start with Windows (as the overlay), adds Start menu + Desktop shortcuts and launches it.
#   .\install.ps1                 normal install
#   .\install.ps1 -NoAutostart    install without start-on-boot
#   .\install.ps1 -NoLaunch       don't start it right now
#
# Why dotnet.exe + RetroTodo.dll instead of RetroTodo.exe: Windows Smart App Control blocks unsigned
# .exe files. Microsoft's signed dotnet.exe runs the app instead, started through
# "conhost.exe --headless" so no console window appears.
param([switch]$NoAutostart, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$dotnet  = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$conhost = Join-Path $env:WINDIR 'System32\conhost.exe'
$dest    = Join-Path $env:LOCALAPPDATA 'Programs\RetroTodo'
$dll     = Join-Path $dest 'RetroTodo.dll'
$icon    = Join-Path $dest 'RetroTodo.ico'
$launch  = "--headless `"$dotnet`" `"$dll`""

function Get-Running {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='RetroTodo.exe'" |
        Where-Object { $_.Name -eq 'RetroTodo.exe' -or $_.CommandLine -match 'RetroTodo\.dll' }
}

Write-Host '> building...' -ForegroundColor Green
& $dotnet publish "$root\src\RetroTodo.csproj" -c Release -o "$root\publish" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (Get-Running) {
    Write-Host '> closing the running copy...' -ForegroundColor Green
    & $dotnet "$root\publish\RetroTodo.dll" --quit   # asks the running copy to save and exit
    Start-Sleep -Milliseconds 1500
    Get-Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Write-Host "> installing to $dest" -ForegroundColor Green
New-Item -ItemType Directory -Force $dest | Out-Null
Remove-Item (Join-Path $dest 'RetroTodo.exe') -ErrorAction SilentlyContinue   # old blocked single-file build
Copy-Item "$root\publish\*" $dest -Recurse -Force
Copy-Item "$root\src\RetroTodo.ico" $icon -Force

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutostart) {
    Remove-ItemProperty $run -Name RetroTodo -ErrorAction SilentlyContinue
    Write-Host '> start with Windows: OFF' -ForegroundColor Green
} else {
    Set-ItemProperty $run -Name RetroTodo -Value "`"$conhost`" $launch --startup"
    Write-Host '> start with Windows: ON (opens as the overlay)' -ForegroundColor Green
}

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in 'Programs', 'Desktop') {
    $sc = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath($folder)) 'Retro Todo.lnk'))
    $sc.TargetPath = $conhost
    $sc.Arguments = $launch
    $sc.WorkingDirectory = $dest
    $sc.IconLocation = "$icon,0"
    $sc.WindowStyle = 7   # minimized: nothing flashes even on systems without --headless support
    $sc.Description = 'Retro Todo: open your todos'
    $sc.Save()
}
Write-Host '> shortcuts: Start menu + Desktop ("Retro Todo")' -ForegroundColor Green

if (-not $NoLaunch) { Start-Process $conhost -ArgumentList "$launch --startup" }   # overlay; nothing steals focus
Write-Host '> done. Your todos live in %APPDATA%\RetroTodo\todos.json' -ForegroundColor Green
