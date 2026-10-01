#!/usr/bin/env bash
# Packs ConnectNet and ConnectNet.Validation and runs the smoke application against the
# packages, exactly as a consumer would use them.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERSION="${SMOKE_PACKAGE_VERSION:-0.0.0-smoke}"
FEED="$ROOT/artifacts/packages"

rm -rf "$FEED"
dotnet pack "$ROOT/src/ConnectNet/ConnectNet.csproj" -c Release -o "$FEED" -p:Version="$VERSION" -nologo -v q
dotnet pack "$ROOT/src/ConnectNet.Validation/ConnectNet.Validation.csproj" -c Release -o "$FEED" -p:Version="$VERSION" -nologo -v q

# A previous run leaves the same version in the package cache; the fresh pack must win.
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
rm -rf "$NUGET_ROOT/connectnet/$VERSION" "$NUGET_ROOT/connectnet.validation/$VERSION"

cd "$HERE"
rm -rf bin obj
dotnet run --project ConnectNet.Validation.Smoke.csproj -c Release -p:SmokePackageVersion="$VERSION"
