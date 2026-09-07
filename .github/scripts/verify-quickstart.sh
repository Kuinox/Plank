#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
quickstart_temp="$(mktemp -d)"
trap 'rm -rf "$quickstart_temp"' EXIT
version=0.0.0-quickstart
feed="$quickstart_temp/packages"

cd "$repo_root"
dotnet pack Plank.Native.Zlib/Plank.Native.Zlib.csproj -c Release -o "$feed"
dotnet pack Plank.SourceGen/Plank.SourceGen.csproj -c Release -o "$feed" -p:PackageVersion="$version"
dotnet pack Plank/Plank.csproj -c Release -o "$feed" -p:PackageVersion="$version"

# Consume only the public package, outside the repo and its global imports.
# A private package cache also prevents a stale quickstart package from passing.
export NUGET_PACKAGES="$quickstart_temp/cache"
dotnet new console --framework net10.0 --name PlankDemo --output "$quickstart_temp/app" --no-restore
cp Samples/Plank.Quickstart/Program.cs "$quickstart_temp/app/Program.cs"
cd "$quickstart_temp/app"
dotnet add package Plank --version "$version" --no-restore
dotnet restore --source "$feed" --source https://api.nuget.org/v3/index.json
dotnet run --configuration Release --no-restore
