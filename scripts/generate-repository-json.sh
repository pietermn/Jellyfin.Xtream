#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "Usage: $0 <github-owner> <github-repo> [version]" >&2
  exit 1
fi

owner="$1"
repo="$2"
version="${3:-0.8.4.0}"
tag="v${version%".0"}"
zip_name="jellyfin-xtream_${version}.zip"
dist_zip="dist/Jellyfin.Xtream_${version}.zip"
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
        "changelog": "Custom build: increase live restream buffer, improve live buffer synchronization, preserve configured User-Agent for stream requests, update Jellyfin 10.11 dependencies, add configurable regex cleanup rules, and add optional STRM export for selected movies and series.",
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

cp "$dist_zip" "dist/${zip_name}"

echo "Generated $output"
echo "Release asset: dist/${zip_name}"
echo "Repository URL after publishing GitHub Pages: https://${owner}.github.io/${repo}/repository.json"
