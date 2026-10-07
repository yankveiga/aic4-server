param([switch]$Test)
$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExecutable = if ($dotnetCommand) { $dotnetCommand.Source } else {
    Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
    throw 'Instale o SDK .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0'
}
$project = if ($Test) {
    Join-Path $PSScriptRoot '..\server-tests\AicIv.Server.Tests.csproj'
} else {
    Join-Path $PSScriptRoot 'AicIv.Server.csproj'
}
& $dotnetExecutable run --project $project
exit $LASTEXITCODE
