$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
dotnet restore .\src\SultansGameGuideMod\SultansGameGuideMod.csproj --configfile .\NuGet.config
dotnet build .\src\SultansGameGuideMod\SultansGameGuideMod.csproj -c Release --no-restore
Pop-Location
Write-Host "完成。DLL 位于 src\SultansGameGuideMod\bin\Release\net6.0\SultansGameGuide.dll"
