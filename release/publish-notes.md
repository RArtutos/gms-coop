# Publishing a new version

The launcher (`GMS Co-op.exe`) checks the latest GitHub release of `RArtutos/gms-coop` when it starts.
When the release is newer than the launcher, it shows "Hay versión nueva X — Actualizar". Clicking it
downloads the new exe, checks it against the `.sha256` file and restarts. Friends only need the exe once.

## Steps

1. Raise the version in `GMSCoop\Plugin.cs` (`const string Version = "0.14.1"`). Releases are compared by
   this number, so every release needs a higher one.
2. Build the mod: `dotnet build GMSCoop -c Release`.
3. Build the launcher: `powershell -ExecutionPolicy Bypass -File release\make_release.ps1`.
4. Publish: `powershell -ExecutionPolicy Bypass -File release\publish.ps1 -Notes "what changed"`.
   It writes `GMS Co-op.exe.sha256` and creates the release `vX.Y.Z` with both files through the `gh` CLI
   (it must be logged in: `gh auth login`).

## Rules

- The release must contain exactly these two assets: `GMS Co-op.exe` and `GMS Co-op.exe.sha256`.
  Without the hash file the launcher does not offer the update.
- The tag format is `vX.Y.Z`.
- The repository must stay public, because the launcher reads it without logging in.
- Everybody in a game needs the same mod version. After publishing, ask friends to open the launcher
  once, so it updates.
