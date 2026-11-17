<#
  Ships a new Inventory Server version to GitHub Releases. Every running copy then offers "Install now".

  Usage:   .\release.ps1 V1.2.0 "Serial number tracking"
           (versions are Vmajor.minor.patch and must be higher than the current one)

  It: sets the version in InventoryServer.csproj, runs the smoke test, builds the single-file exe,
      zips + signs it, and creates the GitHub release tagged V1.2.0.
  Needs: .NET 8 SDK, the signing key (ReleaseTool init), and the GitHub CLI 'gh' signed in (gh auth login).
  Only the program goes to GitHub - never server settings, databases or keys.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
foreach ($dir in 'C:\Program Files\Git\cmd', 'C:\Program Files\GitHub CLI') { if ((Test-Path $dir) -and $env:Path -notlike "*$dir*") { $env:Path = "$dir;$env:Path" } }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git is not installed." }

if ($Version -notmatch '^[Vv]?(\d+\.\d+\.\d+)$') { throw "Version must look like V1.2.3" }
$num = $Matches[1]
$tag = "V$num"

$repo = [regex]::Match((Get-Content InventoryServer\Updater.cs -Raw), 'public const string Repo = "([^"]+)"').Groups[1].Value
if (-not $repo -or $repo -eq 'OWNER/REPO') { throw "Updates are not set up yet. Run: dotnet run --project ReleaseTool -- init <owner/repo>" }

# Version must go up, or installed copies would never see it as an update.
$csproj = 'InventoryServer\InventoryServer.csproj'
$xml = Get-Content $csproj -Raw
$current = [regex]::Match($xml, '<Version>([^<]+)</Version>').Groups[1].Value
if ([version]$num -lt [version]$current) { throw "$tag is lower than the current version V$current." }
$hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
if ([version]$num -eq [version]$current -and $hasGh) {
    $ErrorActionPreference = 'Continue'   # PowerShell 5.1 turns a native command's redirected stderr into a terminating error
    gh release view $tag --repo $repo *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($exists) { throw "$tag is already released. Use a higher version." }
}
Set-Content $csproj ([regex]::Replace($xml, '<Version>[^<]+</Version>', "<Version>$num</Version>")) -NoNewline -Encoding utf8
Write-Host "== Version set to $tag" -ForegroundColor Cyan

Write-Host "== Smoke test" -ForegroundColor Cyan
dotnet run --project SmokeTest
if ($LASTEXITCODE -ne 0) { throw "Smoke test failed - release cancelled." }

$out = Join-Path $PSScriptRoot "release\$tag"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& "$PSScriptRoot\build.ps1" -Out "$out\app"

# The release must contain only the program, never data.
$unexpected = Get-ChildItem "$out\app" -Recurse -File | Where-Object { $_.Name -ne 'InventoryServer.exe' -and $_.Extension -ne '.dll' }
if ($unexpected) { throw "Release folder contains files that should not be published: $($unexpected.Name -join ', ')" }

Write-Host "== Pack and sign" -ForegroundColor Cyan
dotnet run --project ReleaseTool -- pack "$out\app" $out $num $Notes
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

$zip = Join-Path $out 'InventoryServer-win-x64.zip'
$manifest = Join-Path $out 'update.json'

Write-Host "== Commit, tag and push source ($tag)" -ForegroundColor Cyan
git add -A
# Source code only: refuse anything that looks like data, keys or build output.
$staged = git diff --cached --name-only
$bad = $staged | Where-Object { $_ -match '\.(db|db-wal|db-shm|sqlite|pfx|pem|key|exe|dll|zip|lnk)$' -or $_ -match '(^|/)(servers|settings)\.json$' -or $_ -match '(^|/)(bin|obj|release|publish)/' }
if ($bad) { git reset -q; throw "Refusing to commit: $($bad -join ', ')" }
if ($staged) { git commit -q -m "Release $tag$(if ($Notes) { ": $Notes" })"; if ($LASTEXITCODE -ne 0) { throw "git commit failed." } }
git tag -a $tag -m "Inventory Server $tag"
if ($LASTEXITCODE -ne 0) { throw "git tag failed (does $tag already exist?)." }
git push -q origin HEAD
git push -q origin $tag
if ($LASTEXITCODE -ne 0) { throw "git push failed." }

if ($hasGh) {
    Write-Host "== GitHub release $tag on $repo" -ForegroundColor Cyan
    $releaseNotes = if ($Notes) { $Notes } else { "Inventory Server $tag" }
    gh release create $tag $zip $manifest --repo $repo --title $tag --notes $releaseNotes --latest --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
    Write-Host "Done. Running apps will offer $tag within 6 hours (or use 'Check for updates')." -ForegroundColor Green
} else {
    Write-Host "GitHub CLI (gh) not found. Create the release by hand:" -ForegroundColor Yellow
    Write-Host "  https://github.com/$repo/releases/new   tag: $tag   (mark as latest)"
    Write-Host "  attach: $zip"
    Write-Host "          $manifest"
}
