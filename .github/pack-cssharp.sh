#!/usr/bin/env bash
# Pack CounterStrikeSharp.API from PR 1433 so plugin builds match CS2 1.41.8.2.
set -euo pipefail

SHA="${CSSHARP_SHA:-2d1b3d7c96e7aeb618e4346f69229ee2fbfbc907}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/.cssharp"

rm -rf "$DEST"
git clone --depth 1 --branch update/1.41.8.2 --filter=blob:none --sparse \
  https://github.com/roflmuffin/CounterStrikeSharp.git "$DEST"
git -C "$DEST" sparse-checkout set managed
git -C "$DEST" fetch --depth 1 origin "$SHA"
git -C "$DEST" checkout --detach FETCH_HEAD
printf '<Project></Project>\n' > "$DEST/Directory.Build.props"
# Parent nuget.config points at this folder. It must exist before restore.
mkdir -p "$DEST/nupkg"

dotnet pack "$DEST/managed/CounterStrikeSharp.API/CounterStrikeSharp.API.csproj" \
  -c Release \
  -o "$DEST/nupkg" \
  -p:Version=1.0.375-pr1433 \
  -p:ApiCompatValidateAssemblies=false \
  -p:EnablePackageValidation=false \
  -p:GenerateDocumentationFile=false \
  -p:IncludeSymbols=false \
  -p:ContinuousIntegrationBuild=true
