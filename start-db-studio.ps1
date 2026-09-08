$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot 'DbStudio/DbStudio.csproj') --no-launch-profile --urls 'http://127.0.0.1:5188'
