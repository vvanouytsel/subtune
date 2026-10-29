# Builds Subtune into a single .exe, installs it for the current user, and adds a desktop icon
# (plus a Start menu entry, so it shows up in Windows search). Run it again to update.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Subtune'
$exe = Join-Path $target 'Subtune.exe'

# A running copy would lock the .exe.
Get-Process Subtune -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish (Join-Path $PSScriptRoot 'src\Subtune') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Subtune.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Subtune.lnk')
)
foreach ($path in $shortcuts) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$exe,0"
    $link.Description = 'Synced lyrics overlay for Spotify'
    $link.Save()
}

Start-Process $exe
Write-Host "Installed to $exe and started it. Desktop icon: Subtune."
