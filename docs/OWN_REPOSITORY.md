# Own Jellyfin Plugin Repository

This fork can be published as a custom Jellyfin plugin repository through GitHub Releases and GitHub Pages.

## Publish

1. Create a new GitHub repository, for example `Jellyfin.Xtream`.
2. Push this fork to your repository.
3. In GitHub, create a release with tag `v0.8.2`.
4. Wait for the `Publish Plugin` workflow to finish.
5. In the repository settings, enable GitHub Pages for the branch created by the workflow.

The Jellyfin repository URL will be:

```text
https://<github-user>.github.io/<repository-name>/repository.json
```

## Install In Jellyfin

1. Open the Jellyfin admin dashboard.
2. Go to `Plugins`.
3. Open the `Repositories` tab.
4. Add a repository with the URL above.
5. Open the `Catalog` tab.
6. Install `Jellyfin Xtream`.
7. Restart Jellyfin.

## Notes

This custom build targets Jellyfin `10.11.11`.
If your server runs another Jellyfin version, update `targetAbi` in `build.yaml` and the Jellyfin package versions in `Jellyfin.Xtream/Jellyfin.Xtream.csproj` before publishing.
