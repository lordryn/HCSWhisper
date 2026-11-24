$ErrorActionPreference = 'Continue'
Write-Host "Uninstalling HCSWhisper..." -ForegroundColor Yellow

Get-Process -Name "VoiceAttack" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$candidates = @(
    "C:\Program Files (x86)\Steam\steamapps\common\VoiceAttack 2\Apps\HCSWhisper",
    "C:\Program Files\VoiceAttack 2\Apps\HCSWhisper",
    "C:\Program Files (x86)\VoiceAttack 2\Apps\HCSWhisper"
)
foreach ($p in $candidates) {
    if (Test-Path $p) {
        Remove-Item -Recurse -Force $p
        Write-Host "Removed plugin folder: $p" -ForegroundColor Green
    }
}

Write-Host "Done. (Cached Whisper models in $env:LOCALAPPDATA\HCSWhisper were kept; delete that folder if you want to free ~141 MB.)"
