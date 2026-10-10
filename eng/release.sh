#!/usr/bin/env bash
# Builds, tests, packs and author-signs a release from macOS or Linux, then publishes it as a
# GitHub release. The release workflow (.github/workflows/release.yml) pushes the signed
# packages to nuget.org with trusted publishing. Same steps as eng/release.ps1, but the
# signature is produced by eng/kms-nuget-sign, which talks to Google Cloud KMS directly
# instead of needing the certificate in a Windows store.
#
# Prerequisites: gcloud application default credentials with roles/cloudkms.signer on the
# key, GH_TOKEN with repo scope, a clean main in sync with origin.
#
#   ./eng/release.sh 1.0.0
set -euo pipefail

version="${1:?usage: eng/release.sh <version>}"
repository="${REPOSITORY:-balsoft-gmbh/Balsoft.Hive.Installer.Windows}"
kms_key="${HIVE_KMS_KEY:-projects/balsoft-codesigning/locations/europe-west3/keyRings/balsoft-codesigning/cryptoKeys/balsoft-rsa3072/cryptoKeyVersions/1}"
timestamper="${HIVE_TIMESTAMPER:-http://timestamp.digicert.com}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
tag="v$version"

step() { printf '\n== %s\n' "$1"; }

: "${GH_TOKEN:?GH_TOKEN is not set.}"

step "Repository state"
git fetch --quiet origin
[ "$(git rev-parse --abbrev-ref HEAD)" = "main" ] || { echo "Releases are made from main."; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "The working tree is not clean."; exit 1; }
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "main is not in sync with origin/main."; exit 1; }
[ -z "$(git tag --list "$tag")" ] || { echo "Tag $tag exists already."; exit 1; }

notes="$(awk -v v="$version" '
  $0 ~ "^## \\[" v "\\]" { on = 1; next }
  on && /^## \[/ { exit }
  on { print }
' CHANGELOG.md | sed -e '1{/^$/d;}')"
[ -n "$notes" ] || { echo "CHANGELOG.md has no section for $version."; exit 1; }

if [ "${SKIP_TESTS:-0}" != "1" ]; then
  step "Tests"
  dotnet test -c Release
fi

step "Pack"
out="$root/artifacts/packages"
rm -rf "$out"
dotnet pack -c Release -o "$out" "-p:Version=$version" -p:ContinuousIntegrationBuild=true

step "Sign and verify"
dotnet build eng/kms-nuget-sign -c Release --nologo -v quiet
for pkg in "$out"/*.nupkg; do
  dotnet run --project eng/kms-nuget-sign -c Release --no-build -- \
    --package "$pkg" --certificate eng/BalsoftGmbH.cer --key "$kms_key" --timestamper "$timestamper"
  dotnet nuget verify --all "$pkg"
done

step "Tag $tag"
git tag -a "$tag" -m "Balsoft.Hive.Installer.Windows $version"
git push origin "$tag"

step "GitHub release"
prerelease=false
case "$version" in *-*) prerelease=true ;; esac
gh release create "$tag" --repo "$repository" --title "Balsoft.Hive.Installer.Windows $version" --notes "$notes" --draft $( [ "$prerelease" = true ] && echo --prerelease ) "$out"/*.nupkg "$out"/*.snupkg
# Published only now, with every asset attached, so the release workflow finds them all.
gh release edit "$tag" --repo "$repository" --draft=false
echo
echo "Released $tag: https://github.com/$repository/releases/tag/$tag"
echo "nuget.org push runs in: https://github.com/$repository/actions/workflows/release.yml"
