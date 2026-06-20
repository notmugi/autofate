# Publishing AutoFates as a Dalamud repo

This repo is set up so that pushing a version tag builds the plugin on GitHub Actions,
publishes `latest.zip` + `repo.json` to a GitHub Release, and gives you a custom Dalamud
repository URL you can add in-game.

## One-time setup

1. **Create a GitHub repo** (e.g. `AutoFates`) and push this project to it.

2. **Set your GitHub username** everywhere the placeholder appears. From the project root:

   ```bash
   ./set-owner.sh YOUR_GITHUB_USERNAME
   ```

   (Or manually replace `YOUR_GITHUB_USERNAME` in `repo.json`.)

3. **Push with submodules** (ECommons is a submodule):

   ```bash
   git remote add origin https://github.com/YOUR_GITHUB_USERNAME/AutoFates.git
   git push -u origin main
   ```

   > The CI checkout uses `submodules: recursive`, so ECommons is pulled automatically on the runner.

## Cutting a release

Bump the version in `AutoFates/AutoFates.csproj` (`<Version>`), commit, then tag and push:

```bash
git commit -am "Release v1.0.0.1"
git tag v1.0.0.1
git push origin v1.0.0.1
```

GitHub Actions will:
- build the plugin against the latest Dalamud,
- attach `latest.zip` and a generated `repo.json` to a Release,
- mark it as the `latest` release.

You can also trigger a build manually from the **Actions** tab (no release is created then).

## Adding the repo in-game

In FFXIV with Dalamud:

1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add this URL (replace the username):

   ```
   https://github.com/YOUR_GITHUB_USERNAME/AutoFates/releases/latest/download/repo.json
   ```

3. **Save and Close**, then open the Plugin Installer and search **AutoFates**.

> The `releases/latest/download/...` URLs always point to your newest release, so updates
> are delivered automatically — no need to change the repo URL when you publish new versions.

## Alternative: dev plugin (no repo)

You can still side-load the built DLL directly:
`Settings → Experimental → Dev Plugin Locations` → add the path to `AutoFates.dll`.
