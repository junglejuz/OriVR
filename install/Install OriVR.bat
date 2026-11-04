@echo off
rem OriVR installer. Double click it, or use: "Install OriVR.bat" install   or   "Install OriVR.bat" uninstall
rem It only touches the game folder. No game files are changed, and uninstall removes what it added.
setlocal
set "ORIVR_HERE=%~dp0"
set "ORIVR_ARG=%~1"
set "ORIVR_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = Get-Content -LiteralPath $env:ORIVR_SELF -Raw; $s = $t.Substring($t.IndexOf('#PS' + 'START') + 8); Invoke-Expression $s"
set "ORIVR_RESULT=%ERRORLEVEL%"
if "%ORIVR_ARG%"=="" pause
exit /b %ORIVR_RESULT%

#PSSTART
$ErrorActionPreference = 'Stop'
$here = $env:ORIVR_HERE
$files = Join-Path $here 'files'
$arg = $env:ORIVR_ARG

function Find-Game {
    $roots = New-Object System.Collections.ArrayList
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            [void]$roots.Add($steam)
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    [void]$roots.Add(($m.Groups[1].Value -replace '\\\\', '\'))
                }
            }
        }
    } catch { }
    foreach ($r in $roots) {
        $g = Join-Path $r 'steamapps\common\Ori DE'
        if (Test-Path -LiteralPath (Join-Path $g 'oriDE.exe')) { return $g }
    }
    return $null
}

function Fail($text) {
    Write-Host ''
    Write-Host "OriVR: $text" -ForegroundColor Red
    exit 1
}

function Install-Mod($game) {
    Copy-Item -LiteralPath (Join-Path $files 'winhttp.dll') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files 'doorstop_config.ini') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files '.doorstop_version') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files 'BepInEx') -Destination $game -Recurse -Force

    # BepInEx's default start point gives a black screen in this game, so it must start from Camera instead.
    $cfg = Join-Path $game 'BepInEx\config\BepInEx.cfg'
    if (Test-Path -LiteralPath $cfg) {
        $text = Get-Content -LiteralPath $cfg -Raw
        $text = $text -replace '(?m)^Type = Application\s*$', 'Type = Camera'
        [System.IO.File]::WriteAllText($cfg, $text)
    }

    $need = @('winhttp.dll', 'BepInEx\core\BepInEx.dll', 'BepInEx\config\BepInEx.cfg', 'BepInEx\plugins\OriVR\OriVR.dll', 'BepInEx\plugins\OriVR\OriVRNative.dll', 'BepInEx\plugins\OriVR\openxr_loader.dll')
    foreach ($n in $need) {
        if (-not (Test-Path -LiteralPath (Join-Path $game $n))) { Fail "$n did not copy." }
    }
    if ((Get-Content -LiteralPath $cfg -Raw) -notmatch '(?m)^Type = Camera') { Fail 'could not set the start point in BepInEx.cfg.' }

    # Files from a downloaded zip carry a "came from the internet" mark, which can stop the DLLs loading.
    Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx') -Recurse -File | Unblock-File
    Unblock-File -LiteralPath (Join-Path $game 'winhttp.dll')
    Write-Host ''
    Write-Host 'OriVR is installed.' -ForegroundColor Green
    Write-Host 'Start SteamVR, then start Ori from Steam. VR starts by itself.'
    Write-Host 'Click both sticks at once to open the settings menu.'
    $runtime = $null
    try { $runtime = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Khronos\OpenXR\1' -ErrorAction Stop).ActiveRuntime } catch { }
    if (-not $runtime) {
        Write-Host ''
        Write-Host 'Warning: no 32-bit OpenXR runtime is set on this PC, and this game is 32-bit.' -ForegroundColor Yellow
        Write-Host 'Update SteamVR to 2.17 or newer, then in SteamVR settings, under OpenXR, set SteamVR as the OpenXR runtime.'
        Write-Host 'Until then the game runs flat.'
    } else {
        Write-Host "32-bit OpenXR runtime: $runtime"
    }
    $sac = $null
    try { $sac = (Get-MpComputerStatus -ErrorAction Stop).SmartAppControlState } catch { }
    if ($sac -and $sac -ne 'Off') {
        Write-Host ''
        Write-Host "Warning: Windows Smart App Control is $sac on this PC." -ForegroundColor Yellow
        Write-Host 'It can block mod DLLs that are not signed, and this mod is not signed. If the game runs flat, check this first. See the README.'
    }
}

function Uninstall-Mod($game) {
    foreach ($n in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
        $p = Join-Path $game $n
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
    $plugin = Join-Path $game 'BepInEx\plugins\OriVR'
    if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force }
    $cfg = Join-Path $game 'BepInEx\config\com.scubasteve2365.orivr.cfg'
    if (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Force }
    $bep = Join-Path $game 'BepInEx'
    $plugins = Join-Path $bep 'plugins'
    $others = @()
    if (Test-Path -LiteralPath $plugins) { $others = @(Get-ChildItem -LiteralPath $plugins -Force) }
    if ($others.Count -eq 0) {
        if (Test-Path -LiteralPath $bep) { Remove-Item -LiteralPath $bep -Recurse -Force }
    } else {
        Write-Host 'Other BepInEx mods were found, so the BepInEx folder was left in place. They will not load without winhttp.dll.' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'OriVR is uninstalled. The game is back to how it was.' -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath (Join-Path $files 'BepInEx'))) { Fail 'the files folder is missing. Unzip everything before running this.' }
if (Get-Process -Name oriDE -ErrorAction SilentlyContinue) { Fail 'close Ori first, then run this again.' }

$game = Find-Game
if (-not $game) {
    Write-Host 'Could not find Ori and the Blind Forest: Definitive Edition automatically.'
    if ($arg) { Fail 'game folder not found.' }
    $game = Read-Host 'Paste the game folder (the one holding oriDE.exe)'
    $game = $game.Trim('"', ' ')
    if (-not (Test-Path -LiteralPath (Join-Path $game 'oriDE.exe'))) { Fail 'oriDE.exe is not in that folder.' }
}
Write-Host "Game folder: $game"

$choice = $arg
if (-not $choice) {
    Write-Host ''
    Write-Host '  1  Install or update OriVR'
    Write-Host '  2  Uninstall OriVR'
    $pick = Read-Host 'Type 1 or 2 and press Enter'
    if ($pick -eq '1') { $choice = 'install' }
    elseif ($pick -eq '2') { $choice = 'uninstall' }
    else { Fail 'nothing done.' }
}
if ($choice -eq 'install') { Install-Mod $game }
elseif ($choice -eq 'uninstall') { Uninstall-Mod $game }
else { Fail "unknown choice $choice" }
exit 0
