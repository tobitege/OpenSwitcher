param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_UI_LANGUAGE = "en-US"
$runtimeIdentifier = "win-x64"
$appProjectPath = Join-Path $PSScriptRoot "src\OpenSwitcher\OpenSwitcher.csproj"
$logDirectory = Join-Path $PSScriptRoot "logs"
$logPath = Join-Path $logDirectory "build.log"

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Set-Content -LiteralPath $logPath -Value "OPENSWITCHER_BUILD_START $(Get-Date -Format o)" -Encoding utf8

Write-Host "Restore Windows desktop app ($runtimeIdentifier)"
dotnet restore $appProjectPath --runtime $runtimeIdentifier --verbosity minimal 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) {
  throw "App restore failed with exit code $LASTEXITCODE. See $logPath"
}

Write-Host "Clean Windows desktop output"
dotnet clean $appProjectPath --configuration $Configuration --runtime $runtimeIdentifier --verbosity quiet 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) {
  throw "Clean failed with exit code $LASTEXITCODE. See $logPath"
}

Write-Host "Windows desktop x64 build (English resources only)"
dotnet build $appProjectPath --configuration $Configuration --runtime $runtimeIdentifier --no-restore --verbosity minimal 2>&1 | Tee-Object -FilePath $logPath -Append
$buildExitCode = $LASTEXITCODE

Add-Content -LiteralPath $logPath -Value "OPENSWITCHER_BUILD_END EXIT_CODE=$buildExitCode $(Get-Date -Format o)" -Encoding utf8
Write-Host "OPENSWITCHER_BUILD_EXIT_CODE=$buildExitCode"
Write-Host "Log: $logPath"
exit $buildExitCode
