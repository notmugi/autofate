# Publishing AutoFates as a Dalamud repo (no tags, no releases)

AutoFates is served **directly from the `main` branch**. There are no GitHub Releases and no
tags involved. Dalamud reads `repo.json` and the plugin zip straight from the raw files in the
repo.

## How it works

- Dalamud reads **`repo.json`** from:
  `https://raw.githubusercontent.com/notmugi/autofate/main/repo.json`
- That points the download at **`latest.zip`** (committed at the repo root):
  `https://raw.githubusercontent.com/notmugi/autofate/main/latest.zip`

Both files live on `main`. To publish or update, you just commit a new `latest.zip` + bump the
version in `repo.json` and push.

## One-time: push the repo

```bash
git remote add origin https://github.com/notmugi/autofate.git   # already set
git push -u origin main
```

If GitHub rejects the push because the remote already has commits (e.g. an auto-created README):

```bash
git pull origin main --rebase
git push -u origin main
```

## Add the repo in-game

`/xlsettings` → **Experimental** → **Custom Plugin Repositories** → paste the **raw** URL:

```
https://raw.githubusercontent.com/notmugi/autofate/main/repo.json
```

**Save and Close** → open `/xlplugins` → click the **refresh** icon → search **AutoFates**.

> Use the `raw.githubusercontent.com` URL, NOT the `github.com/.../blob/...` page URL.

## Publishing an update

Run the helper script, then push:

```bash
./update-build.sh        # rebuilds, copies latest.zip, bumps repo.json version to match
git commit -am "Update build"
git push
```

> `raw.githubusercontent.com` is CDN-cached for a few minutes, so a new version may take ~5
> minutes to appear in Dalamud after pushing.

## Alternative: dev plugin (local, no repo)

You can always side-load the built DLL directly:
`/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the path to
`AutoFates/bin/x64/Release/AutoFates.dll`, then **Dev Tools → Installed Dev Plugins → Load**.
