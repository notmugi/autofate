# Publishing Autofate as a Dalamud repo (no tags, no releases)

Autofate is served **directly from the `main` branch**. There are no GitHub Releases and no
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

**Save and Close** → open `/xlplugins` → click the **refresh** icon → search **Autofate**.

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
`Autofate/bin/x64/Release/Autofate.dll`, then **Dev Tools → Installed Dev Plugins → Load**.

## Updating the icon

The plugin icon is a **512×512 PNG** at `Autofate/images/icon.png`. A plain-white placeholder
ships in the repo (`icon.svg` is the editable source).

1. Replace `Autofate/images/icon.png` with your 512×512 PNG (keep the same path/filename).
2. Commit + push. `repo.json` already points Dalamud at:
   `https://raw.githubusercontent.com/notmugi/autofate/main/Autofate/images/icon.png`

No other changes needed — the in-game installer picks it up (after the ~5 min CDN cache).

## Updating the name / description / tags

These live in **two** places and should be kept in sync:

- `Autofate/Autofate.json` — `Name`, `Punchline`, `Description`, `Tags`, `CategoryTags`
  (this is what gets packed into `latest.zip`).
- `repo.json` — same fields, used by the repo listing before install.

Edit both, then run `./update-build.sh push`.
