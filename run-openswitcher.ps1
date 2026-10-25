$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "src\OpenSwitcher\OpenSwitcher.csproj"

dotnet run --project $projectPath --configuration Release --no-launch-profile
exit $LASTEXITCODE
