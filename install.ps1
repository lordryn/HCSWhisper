# HCSWhisper One-Click Installer for VoiceAttack 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoOwner = "lordryn"
$RepoName  = "HCSWhisper"
$AssetName = "HCSWhisper-v1.0.0-win-x64.zip"
$DownloadUrl = "https://github.com/$RepoOwner/$RepoName/releases/latest/download/$AssetName"
$ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin"

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   HCSWhisper — One-Click Installer for VoiceAttack 2     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""

function Find-VoiceAttack2 {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\VoiceAttack 2",
        "C:\Program Files\Steam\steamapps\common\VoiceAttack 2",
        "C:\Program Files\VoiceAttack 2",
        "C:\Program Files (x86)\VoiceAttack 2"
    )

    # Check all Steam library folders
    $steamVdfs = @(
        "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf",
        "C:\Program Files\Steam\steamapps\libraryfolders.vdf"
    )
    foreach ($vdf in $steamVdfs) {
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
                $lib = $_.Matches[0].Groups[1].Value -replace '\\\\', '\'
                $candidates += (Join-Path $lib "steamapps\common\VoiceAttack 2")
            }
        }
    }

    # Check Windows Uninstall Registry
    $regPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($rp in $regPaths) {
        Get-ItemProperty $rp -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "VoiceAttack 2*" -and $_.InstallLocation } |
            ForEach-Object { $candidates += $_.InstallLocation }
    }

    foreach ($dir in ($candidates | Select-Object -Unique)) {
        if ($dir -and (Test-Path (Join-Path $dir "VoiceAttack.exe")) -and (Test-Path (Join-Path $dir "VoiceAttack.dll"))) {
            return $dir
        }
    }
    return $null
}

$va2Dir = Find-VoiceAttack2
if (-not $va2Dir) {
    Write-Host "Could not auto-detect VoiceAttack 2 in default Steam/Program Files folders." -ForegroundColor Yellow
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select your 'VoiceAttack 2' installation folder (containing VoiceAttack.exe)"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $va2Dir = $dlg.SelectedPath
    }
}

if (-not $va2Dir -or -not (Test-Path (Join-Path $va2Dir "VoiceAttack.exe"))) {
    throw "VoiceAttack 2 installation was not found. Please install VoiceAttack 2 first."
}

Write-Host "[1/4] Found VoiceAttack 2 at: $va2Dir" -ForegroundColor Green

# Ensure VoiceAttack isn't locking old plugin DLLs during install/upgrade
$vaProc = Get-Process -Name "VoiceAttack" -ErrorAction SilentlyContinue
if ($vaProc) {
    Write-Host "      Closing running VoiceAttack 2 instance so plugin files can update..." -ForegroundColor Yellow
    $vaProc | Stop-Process -Force
    Start-Sleep -Seconds 1
}

$appsDir = Join-Path $va2Dir "Apps"
$targetDir = Join-Path $appsDir "HCSWhisper"
New-Item -ItemType Directory -Force -Path $appsDir | Out-Null

# Download & extract release zip
$tmpZip = Join-Path $env:TEMP $AssetName
Write-Host "[2/4] Downloading HCSWhisper plugin package (~18 MB)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $DownloadUrl -OutFile $tmpZip

Write-Host "[3/4] Installing plugin into $targetDir..." -ForegroundColor Cyan
if (Test-Path $targetDir) {
    Remove-Item -Recurse -Force $targetDir
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($tmpZip, $appsDir)
Remove-Item -Force $tmpZip -ErrorAction SilentlyContinue

# Pre-cache Whisper base.en GGML model so first startup in VoiceAttack is instant
$modelDir = Join-Path $env:LOCALAPPDATA "HCSWhisper\models"
$modelFile = Join-Path $modelDir "ggml-base.en.bin"
New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

if (-not (Test-Path $modelFile) -or (Get-Item $modelFile).Length -lt 100000000) {
    Write-Host "[4/4] Pre-downloading Whisper AI model 'base.en' (141 MB, one-time cache)..." -ForegroundColor Cyan
    $tmpModel = "$modelFile.downloading"
    Invoke-WebRequest -Uri $ModelUrl -OutFile $tmpModel
    Move-Item -Force $tmpModel $modelFile
} else {
    Write-Host "[4/4] Whisper AI model 'base.en' already cached." -ForegroundColor Green
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   HCSWhisper Installed Successfully!                     " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Open VoiceAttack 2 -> Options (wrench) -> General -> check 'Enable Plugin Support'"
Write-Host "     (If enabling plugins for the first time, restart VoiceAttack 2 once.)"
Write-Host "  2. Select your HCS Voice Pack profile (Event Horizon, Singularity, etc.)."
Write-Host "     No VoiceAttack profile edits or keybind workarounds are needed!"
Write-Host "  3. Hold RIGHT ALT to speak, or double-click the cyan HCSWhisper tray icon"
Write-Host "     to bind any HOTAS button, Mouse 4/5, Keyboard key, or switch to Open-Mic."
Write-Host ""
