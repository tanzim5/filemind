# Publishing File Mind on winget

winget installs from the community repository `microsoft/winget-pkgs`. The release workflow sends every new version there
automatically, but the **first** version has to be submitted by hand once.

## One-time setup (about 15 minutes)

1. Release a version first (the zip must exist at a public URL), for example `v0.12.0`.
2. Install the tool: `winget install Microsoft.WingetCreate`
3. Create the manifest from the release zip:

   ```
   wingetcreate new https://github.com/tanzim5/filemind/releases/download/v0.12.0/FileMind-Windows.zip
   ```

   Answer the questions with these values (the tool fills in the hash for you):

   | Question | Answer |
   |---|---|
   | PackageIdentifier | `Tanzim.FileMind` |
   | PackageName | `File Mind` |
   | Publisher | `Tanzim` |
   | License | the licence of this repository |
   | ShortDescription | `Find your photos, videos, sounds and documents by describing them. Runs on your PC.` |
   | InstallerType | `zip` |
   | NestedInstallerType | `portable` |
   | RelativeFilePath | `FileMind\FileMind.exe` |
   | PortableCommandAlias | `filemind` |
   | Architecture | `x64` |
   | MinimumOSVersion | `10.0.19041.0` |

   Let it open the pull request to `microsoft/winget-pkgs` (it asks for a GitHub token with `public_repo`).
4. Wait for the review (usually a day or two). Then test: `winget install Tanzim.FileMind`.

## Turn on automatic updates of the manifest

After the first submission is merged:

1. Create a classic personal access token with the `public_repo` scope.
2. In this repository: Settings > Secrets and variables > Actions
   - add the secret `WINGET_TOKEN` (the token),
   - add the variable `WINGET_ENABLED` with the value `true`.
3. Fork `microsoft/winget-pkgs` to the same account (the action opens its pull requests from the fork).

From then on, pushing a `v*` tag publishes the release and the `winget` job submits the new version.
Users get it with `winget upgrade Tanzim.FileMind`. The app itself notices the winget install and offers that command.

## How updates work for each way of installing

| Installed with | Who updates it |
|---|---|
| Unzipped folder | File Mind itself: it downloads the new zip, checks its SHA-256 against `FileMind-Windows.zip.sha256`, swaps the folder and restarts. Your settings and library are not in that folder. |
| winget | `winget upgrade Tanzim.FileMind` (the app opens it for you). |
| Microsoft Store | The Store. File Mind never replaces its own files there. |
