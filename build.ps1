<#
  Builds Inventory Server as ONE self-contained file: InventoryServer.exe (no .NET install needed on the PC).

  .\build.ps1                 -> writes InventoryServer.exe into this folder (the version in InventoryServer.csproj)
  .\build.ps1 -Out somewhere  -> writes it there instead (release.ps1 uses this)

  Needs the .NET 8 SDK. Close the app first if it is running from the output folder.
#>
param([string]$Out = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$target = Join-Path $Out 'InventoryServer.exe'
$running = Get-Process InventoryServer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }
if ($running) { throw "InventoryServer.exe is running from $Out. Exit it (tray icon -> Exit) and try again." }

$version = [regex]::Match((Get-Content InventoryServer\InventoryServer.csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
Write-Host "== Building Inventory Server V$version -> $target" -ForegroundColor Cyan

dotnet publish InventoryServer -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -p:IsTransformWebConfigDisabled=true `
    -o $Out
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Built $target (V$version)." -ForegroundColor Green
