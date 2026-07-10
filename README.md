# Jellyfin.Xtream
![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/pietermn/Jellyfin.Xtream/total)
![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/pietermn/Jellyfin.Xtream/latest/total)
![GitHub commits since latest release](https://img.shields.io/github/commits-since/pietermn/Jellyfin.Xtream/latest)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fpietermn%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=targetAbi&label=Jellyfin%20ABI)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2Fpietermn%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=framework&label=.NET%20framework)

The Jellyfin.Xtream plugin can be used to integrate the content provided by an [Xtream-compatible API](https://xtream-ui.org/api-xtreamui-xtreamcode/) in your [Jellyfin](https://jellyfin.org/) instance.

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

Name cleanup rules rename items without changing their provider IDs. Enter one regular expression per line in the Credentials tab. Use `pattern => replacement`; omitting `=> replacement` removes each match. Rules without a scope remain compatible with older configurations and apply everywhere.

Optional scopes make a rule apply only where intended:

```text
[LiveChannel] ^(?:NL|BE)[|:]\s* =>
[LiveProgram] \s+\(Replay\)$ =>
[Movie,Series,Episode,Filesystem] \s+\[(?:4K|FHD|HD)\]$ =>
```

Supported scopes are `LiveChannel`, `LiveProgram`, `Category`, `Movie`, `Series`, `Episode`, and `Filesystem`. Invalid expressions are skipped and logged. Rules use a timeout so a pathological expression cannot stall a guide refresh or STRM export. A manual Live TV name override is always the final display name.

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

Optionally enable STRM export and choose a server-local Movies folder. v0.9 writes stable ID-based paths and an ownership manifest. Cleanup only removes files owned by that manifest; manually created and legacy STRM files are preserved.

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

v0.9 returns signed Jellyfin proxy URLs for playback and exported STRM files. Xtream usernames and passwords are no longer embedded in newly generated client-visible media paths. Treat a signed URL as a stream-specific access token; changing the provider credentials invalidates it.

Before the first v0.9 export, remove or move STRM files generated by v0.8, or configure a new empty export root. v0.9 deliberately preserves files that are not listed in its ownership manifest because it cannot safely distinguish old generated files from manual files. Old v0.8 STRMs can contain provider credentials and are not rewritten in place.

## Troubleshooting

Make sure you have correctly configured your [Jellyfin networking](https://jellyfin.org/docs/general/networking/):

1. Open your admin dashboard and navigate to `Networking`.
2. Correctly configure your `Published server URIs`.
   For example: `all=https://jellyfin.example.com`
