# Jellyfin.Xtream
![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/pietermn/Jellyfin.Xtream/total)
![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/pietermn/Jellyfin.Xtream/latest/total)
![GitHub commits since latest release](https://img.shields.io/github/commits-since/pietermn/Jellyfin.Xtream/latest)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fpietermn%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=targetAbi&label=Jellyfin%20ABI)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fpietermn%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=framework&label=.NET%20framework)

The Jellyfin.Xtream plugin can be used to integrate the content provided by an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/) in your [Jellyfin](https://jellyfin.org/) instance.

## Compatibility

Plugin v0.10.0 targets Jellyfin 12.1 and .NET 10. For Jellyfin 10.11, use v0.9.12.

## Installation

The plugin can be installed using a custom plugin repository.
For this custom fork, publish your own repository first using [docs/OWN_REPOSITORY.md](docs/OWN_REPOSITORY.md).
To add the repository, follow these steps:

1. Open your admin dashboard and navigate to `Plugins`.
1. Select the `Repositories` tab on the top of the page.
1. Click the `+` symbol to add a repository.
1. Enter `Jellyfin.Xtream Custom` as the repository name.
1. Enter `https://<github-user>.github.io/<repository-name>/repository.json` as the repository url.
1. Click save.

To install or update the plugin, follow these steps:

1. Open your admin dashboard and navigate to `Plugins`.
1. Select the `Catalog` tab on the top of the page.
1. Under `Live TV`, select `Jellyfin Xtream`.
1. (Optional) Select the desired plugin version.
1. Click `Install`.
1. Restart your Jellyfin server to complete the installation.

## Configuration

The plugin requires connection information for an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/).
The following credentials should be set correctly in the `Credentials` plugin configuration tab on the admin dashboard.

| Property | Description                                                                               |
| -------- | ----------------------------------------------------------------------------------------- |
| Base URL | The URL of the API endpoint excluding the trailing slash, including protocol (http/https) |
| Username | The username used to authenticate to the API                                              |
| Password | The password used to authenticate to the API                                              |

### Name cleanup rules

Name cleanup rules rename items without changing their provider IDs. The Credentials tab has separate regex editors for categories, Live TV, movies, and series; each automatically applies to its matching item type. Enter one `pattern => replacement` per line; omitting `=> replacement` removes each match. The global/advanced editor remains compatible with older configurations and applies everywhere unless a scope is specified.

Optional scopes make a rule apply only where intended:

```text
[LiveChannel] ^(?:NL|BE)[|:]\s* =>
[LiveProgram] \s+\(Replay\)$ =>
[Vod,Series,Episode] \s+\[(?:4K|FHD|HD)\]$ =>
```

Supported scopes are `LiveChannel`, `LiveProgram`, `Category`, `Vod`, `Series`, `Season`, `Episode`, and `Filesystem`. A VOD, series, or episode rule also applies to that content's STRM filename; use `Filesystem` for a rule that applies to every exported path. Invalid expressions are skipped and logged. Rules use a timeout so a pathological expression cannot stall a guide refresh or STRM export. A manual Live TV name override is always the final display name.

### Live TV

1. Open the `Live TV` configuration tab.
1. Select the categories, or individual channels within categories, you want to be available.
1. Click `Save` on the bottom of the page.

Live TV is exposed through Jellyfin's native Live TV interface. It does not create STRM files; cleanup rules and TV Overrides rename the channel display names directly.
1. Open the `TV Overrides` configuration tab.
1. Modify the channel numbers, names, and icons if desired.
1. Click `Save` on the bottom of the page.

### Video On-Demand

1. Open the `Video On-Demand` configuration tab.
1. Enable `Show this channel to users`.
1. Select the categories, or individual videos within categories, you want to be available.
1. Click `Save` on the bottom of the page.

Optionally enable STRM export and choose a server-local Movies folder. The plugin writes stable ID-based paths and an ownership manifest. Cleanup only removes files owned by that manifest; manually created files are preserved.

### Series

1. Open the `Series` configuration tab.
1. Enable `Show this channel to users`.
1. Select the categories, or individual series within categories, you want to be available.
1. Click `Save` on the bottom of the page.

Optionally enable STRM export and choose a server-local Shows folder. Episodes use stable provider IDs, so equal cleaned titles are kept rather than silently deduplicated.

### TV Catchup
1. Open the `Live TV` configuration tab.
1. Enable `Show the catch-up channel to users`.
1. Click `Save` on the bottom of the page.

## Streaming security

v0.9 returns signed Jellyfin proxy URLs, so Xtream usernames and passwords are no longer embedded in newly generated client-visible media paths. Normal playback grants expire after 15 minutes. Exported STRM files carry a separate durable resolver grant backed by a random, server-side key; the resolver issues a fresh short-lived playback grant. Both keys can be rotated independently, and bearer responses are marked `no-store`.

If the Jellyfin server is behind a reverse proxy, set Jellyfin's Published Server URL or the plugin's optional `Public Jellyfin URL`. The plugin override takes precedence and is embedded in playback and STRM links; it must be an absolute HTTP(S) URL without credentials, query, or fragment.

## Troubleshooting

After upgrading, Jellyfin must load one Jellyfin.Xtream version only. Do not leave manually copied `Jelly Xtream_0.8.*` plugin directories beside the installed v0.9 plugin: side-by-side assemblies create duplicate API routes and can cause cross-assembly `PluginConfiguration` cast failures. Preserve the configuration file, remove or disable obsolete plugin copies, then restart Jellyfin before saving the v0.9 configuration.

For every update, install it through the Jellyfin plugin catalog and restart Jellyfin before using the plugin. Do not manually extract a release ZIP beside the catalog-managed installation. The plugin configuration is stored separately under its stable plugin GUID and is preserved across catalog updates; removing old plugin **binaries** does not remove credentials or selections. If the scheduled-task page ever shows duplicate Xtream entries after restarting, stop Jellyfin and remove only obsolete `Jelly Xtream_*`/`Jellyfin.Xtream_*` plugin directories, keeping the configuration file intact.

Make sure you have correctly configured your [Jellyfin networking](https://jellyfin.org/docs/general/networking/):

1. Open your admin dashboard and navigate to `Networking`.
2. Correctly configure your `Published server URIs`.
   For example: `all=https://jellyfin.example.com`
