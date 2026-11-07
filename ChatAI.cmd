@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"

:: Default port (change here, set CHATAI_PORT, or pass as argument: ChatAI.cmd 8080 / ChatAI.cmd --port 8080)
set "DEFAULT_PORT=5090"
set "REPO=AkiEvansDev/NE.ChatAI"
set "ASSET_NAME=NE.ChatAI-win-x64.zip"

if defined CHATAI_PORT (
    set "PORT=%CHATAI_PORT%"
) else (
    set "PORT=%DEFAULT_PORT%"
)
if defined CHATAI_REPO set "REPO=%CHATAI_REPO%"

set "OPEN_BROWSER=1"
set "CHECK_UPDATE=1"

:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="--port" (
    if not "%~2"=="" (
        set "PORT=%~2"
        shift /1
        shift /1
        goto :parse_args
    )
)
if /I "%~1"=="-p" (
    if not "%~2"=="" (
        set "PORT=%~2"
        shift /1
        shift /1
        goto :parse_args
    )
)
if /I "%~1"=="--no-browser" (
    set "OPEN_BROWSER=0"
    shift /1
    goto :parse_args
)
if /I "%~1"=="--no-update" (
    set "CHECK_UPDATE=0"
    shift /1
    goto :parse_args
)
set "PORT=%~1"
shift /1
goto :parse_args

:args_done
set "APP_DIR=%ROOT%app"
set "DATA_DIR=%ROOT%data"
set "VERSION_FILE=%ROOT%.version"
set "EXE_PATH=%APP_DIR%\NE.ChatAI.Web.exe"

if "%CHECK_UPDATE%"=="1" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
        "$root = $env:ROOT.TrimEnd('\');" ^
        "$appDir = $env:APP_DIR;" ^
        "$exePath = $env:EXE_PATH;" ^
        "$versionFile = $env:VERSION_FILE;" ^
        "$assetName = $env:ASSET_NAME;" ^
        "$apiUrl = if ($env:CHATAI_RELEASE_API) { $env:CHATAI_RELEASE_API } else { 'https://api.github.com/repos/' + $env:REPO + '/releases/latest' };" ^
        "$installed = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '' };" ^
        "$hasApp = Test-Path $exePath;" ^
        "try {" ^
        "    $headers = @{ 'User-Agent' = 'NE.ChatAI-Launcher'; 'Accept' = 'application/vnd.github+json' };" ^
        "    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 6;" ^
        "    $latest = [string]$release.tag_name;" ^
        "    $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1;" ^
        "    if ($latest -and $asset -and ((-not $hasApp) -or ($installed -ne $latest))) {" ^
        "        $action = if ($hasApp) { 'Updating NE.ChatAI (' + $(if ($installed) { $installed } else { 'local' }) + ' -> ' + $latest + ')...' } else { 'Installing NE.ChatAI ' + $latest + '...' };" ^
        "        Write-Host $action -ForegroundColor Cyan;" ^
        "        $zipPath = Join-Path ([IO.Path]::GetTempPath()) ('chatai-' + [Guid]::NewGuid().ToString('N') + '.zip');" ^
        "        $staging = Join-Path $root '.update-staging';" ^
        "        try {" ^
        "            Invoke-WebRequest -Uri $asset.browser_download_url -Headers @{ 'User-Agent' = 'NE.ChatAI-Launcher' } -OutFile $zipPath;" ^
        "            if (Test-Path $staging) { Remove-Item -Recurse -Force $staging };" ^
        "            [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $staging);" ^
        "            $stagedApp = if (Test-Path (Join-Path $staging 'app\NE.ChatAI.Web.exe')) { Join-Path $staging 'app' } else { $staging };" ^
        "            if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir };" ^
        "            Move-Item -Path $stagedApp -Destination $appDir -Force;" ^
        "            [IO.File]::WriteAllText($versionFile, $latest + [Environment]::NewLine);" ^
        "            Write-Host ('Ready: ' + $latest) -ForegroundColor Green;" ^
        "        } finally {" ^
        "            if (Test-Path $zipPath) { Remove-Item -Force $zipPath -ErrorAction SilentlyContinue };" ^
        "            if (Test-Path $staging) { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue };" ^
        "        }" ^
        "    }" ^
        "} catch {" ^
        "    if ($hasApp) {" ^
        "        Write-Host ('Update check skipped: ' + $_.Exception.Message) -ForegroundColor DarkGray;" ^
        "    } else {" ^
        "        Write-Host ('Failed to download release from ' + $apiUrl + ': ' + $_.Exception.Message) -ForegroundColor Red;" ^
        "        exit 1;" ^
        "    }" ^
        "}"
    if errorlevel 1 exit /b 1
)

if not exist "%EXE_PATH%" (
    echo NE.ChatAI executable not found at "%EXE_PATH%".
    exit /b 1
)

if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"

set "ASPNETCORE_ENVIRONMENT=Production"
set "ASPNETCORE_URLS=http://0.0.0.0:%PORT%"
set "ChatAI__DataPath=%DATA_DIR%"

echo.
echo NE.ChatAI is starting on port %PORT%
echo   Local: http://localhost:%PORT%
for /f "tokens=2 delims=:" %%I in ('ipconfig ^| findstr /C:"IPv4"') do (
    for /f "tokens=* delims= " %%A in ("%%I") do echo   LAN:   http://%%A:%PORT%
)
echo   Data:  %DATA_DIR%
echo.

if "%OPEN_BROWSER%"=="1" (
    start "" "http://localhost:%PORT%"
)

pushd "%APP_DIR%"
"%EXE_PATH%"
set "EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %EXIT_CODE%
