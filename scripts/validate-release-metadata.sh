#!/usr/bin/env bash
set -euo pipefail

yaml_version="$(awk -F'"' '/^version:/{print $2; exit}' build.yaml)"
assembly_version="$(sed -n 's:.*<AssemblyVersion>\([^<]*\)</AssemblyVersion>.*:\1:p' Jellyfin.Xtream/Jellyfin.Xtream.csproj)"
file_version="$(sed -n 's:.*<FileVersion>\([^<]*\)</FileVersion>.*:\1:p' Jellyfin.Xtream/Jellyfin.Xtream.csproj)"
script_version="$(sed -n 's/^version="${3:-\([^}]*\)}"$/\1/p' scripts/generate-repository-json.sh)"

if [[ -z "$yaml_version" || "$yaml_version" != "$assembly_version" || "$yaml_version" != "$file_version" || "$yaml_version" != "$script_version" ]]; then
  echo "Version mismatch: build=$yaml_version assembly=$assembly_version file=$file_version script=$script_version" >&2
  exit 1
fi

if [[ $# -gt 0 ]]; then
  expected_tag="v${yaml_version%.0}"
  if [[ "$1" != "$expected_tag" ]]; then
    echo "Tag mismatch: expected $expected_tag, got $1" >&2
    exit 1
  fi
fi

echo "Release metadata is consistent for $yaml_version"
