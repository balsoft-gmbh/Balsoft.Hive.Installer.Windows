<#
.SYNOPSIS
  Builds, tests, packs and author-signs a release, then publishes it as a GitHub release.
  The release workflow (.github/workflows/release.yml) pushes the signed packages to nuget.org
  with trusted publishing.

.DESCRIPTION
  Run on the signing host (the code signing key is not available to CI):
    1. checks that main is clean and in sync with origin,
    2. runs the full test suite including the external validators,
    3. packs every packable project with the given version,
    4. author-signs each .nupkg with the certificate identified by its SHA-256 fingerprint
       (see eng/Register-SigningCertificate.ps1) and a RFC 3161 timestamp, and verifies it,
    5. tags v<version>, pushes the tag, creates the GitHub release with the notes of that
       version from CHANGELOG.md, and attaches the .nupkg and .snupkg files.
  GH_TOKEN must hold a token with repo scope.

.EXAMPLE
  $env:GH_TOKEN = '...'; ./eng/release.ps1 -Version 1.0.0 -CertificateFingerprint B761F9...
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $CertificateFingerprint = $env:HIVE_SIGN_FINGERPRINT,
    [string] $Timestamper = 'http://timestamp.sectigo.com',
    [string] $Repository = 'balsoft-gmbh/Balsoft.Hive.Installer.Windows',
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run($exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set.' }
if (-not $CertificateFingerprint) { throw 'No certificate fingerprint (parameter or HIVE_SIGN_FINGERPRINT).' }
$tag = "v$Version"

Step 'Repository state'
Run git @('fetch', '--quiet', 'origin')
if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Releases are made from main.' }
if (git status --porcelain) { throw 'The working tree is not clean.' }
if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'main is not in sync with origin/main.' }
if (git tag --list $tag) { throw "Tag $tag exists already." }

$notes = [regex]::Match((Get-Content CHANGELOG.md -Raw), "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|\z)").Groups[1].Value.Trim()
if (-not $notes) { throw "CHANGELOG.md has no section for $Version." }

if (-not $SkipTests) {
    Step 'Tests'
    Run dotnet @('test', '-c', 'Release')
}

Step 'Pack'
$out = Join-Path $root 'artifacts/packages'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Run dotnet @('pack', '-c', 'Release', '-o', $out, "-p:Version=$Version", '-p:ContinuousIntegrationBuild=true')

Step 'Sign and verify'
foreach ($pkg in Get-ChildItem $out -Filter *.nupkg) {
    Run dotnet @('nuget', 'sign', $pkg.FullName, '--certificate-fingerprint', $CertificateFingerprint,
        '--certificate-store-location', 'CurrentUser', '--certificate-store-name', 'My',
        '--timestamper', $Timestamper, '--overwrite')
    Run dotnet @('nuget', 'verify', '--all', $pkg.FullName)
}

Step "Tag $tag"
Run git @('tag', '-a', $tag, '-m', "Balsoft.Hive.Installer.Windows $Version")
Run git @('push', 'origin', $tag)

Step 'GitHub release'
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$body = @{ tag_name = $tag; name = "Balsoft.Hive.Installer.Windows $Version"; body = $notes; draft = $true; prerelease = ($Version -match '-') } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repository/releases" -Headers $headers -Body $body -ContentType 'application/json'
$uploadBase = $release.upload_url -replace '\{.*\}', ''
foreach ($file in Get-ChildItem $out -File | Where-Object Extension -in '.nupkg', '.snupkg') {
    Invoke-RestMethod -Method Post -Uri "${uploadBase}?name=$($file.Name)" -Headers $headers -InFile $file.FullName -ContentType 'application/octet-stream' | Out-Null
    Write-Host "  attached $($file.Name)"
}
# Published only now, with every asset attached, so the release workflow finds them all.
Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" -Headers $headers -Body (@{ draft = $false } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
Write-Host "`nReleased ${tag}: $($release.html_url)"
Write-Host "nuget.org push runs in: https://github.com/$Repository/actions/workflows/release.yml"
