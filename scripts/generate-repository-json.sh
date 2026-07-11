#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "Usage: $0 <github-owner> <github-repo> [version]" >&2
  exit 1
fi

owner="$1"
repo="$2"
version="${3:-0.9.2.0}"
tag="v${version%".0"}"
zip_name="jelly-xtream_${version}.zip"
dist_zip="dist/${zip_name}"
output="dist/repository.json"

if [ ! -f "$dist_zip" ]; then
  echo "Missing $dist_zip. Build the plugin and create the distribution zip first." >&2
  exit 1
fi

if command -v md5sum >/dev/null 2>&1; then
  checksum="$(md5sum "$dist_zip" | awk '{ print $1 }')"
else
  checksum="$(md5 -q "$dist_zip")"
fi

timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
source_url="https://github.com/${owner}/${repo}/releases/download/${tag}/${zip_name}"

cat > "$output" <<JSON
[
  {
    "category": "LiveTV",
    "description": "Stream Live IPTV, Video On-Demand, and Series from an Xtream-compatible server using this plugin.\\n",
    "guid": "5d774c35-8567-46d3-a950-9bb8227a0c5d",
    "name": "Jelly Xtream",
    "overview": "Stream content from an Xtream-compatible server.",
    "owner": "${owner}",
    "versions": [
      {
        "changelog": "Rework Live TV name normalization, make STRM reconciliation manifest-owned and atomic, and improve streaming performance and reliability.",
        "checksum": "${checksum}",
        "sourceUrl": "${source_url}",
        "targetAbi": "10.11.0.0",
        "timestamp": "${timestamp}",
        "version": "${version}"
      }
    ]
  }
]
JSON

if [ "$dist_zip" != "dist/${zip_name}" ]; then
  cp "$dist_zip" "dist/${zip_name}"
fi

echo "Generated $output"
echo "Release asset: dist/${zip_name}"
echo "Repository URL after publishing GitHub Pages: https://${owner}.github.io/${repo}/repository.json"
