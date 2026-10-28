"""Build the VPM listing (index.json) and a small install page from the GitHub releases.

usage: python build_listing.py <owner/repo> <outdir>
Each release must contain package.json and <package-id>-<version>.zip (see release.yml).
"""

import hashlib
import json
import os
import sys
import urllib.request

LISTING_ID = "com.chikumatateshina.vpm"
LISTING_NAME = "PDF2UnityTransformer"
AUTHOR = "ChikumaTateshina"


def get(url, raw=False):
    req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json",
                                               "User-Agent": "vpm-listing-builder"})
    token = os.environ.get("GH_TOKEN")
    if token and url.startswith("https://api.github.com/"):
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=120) as r:
        data = r.read()
    return data if raw else json.loads(data)


def main():
    repo, out = sys.argv[1], sys.argv[2]
    owner, name = repo.split("/")
    base = f"https://{owner.lower()}.github.io/{name}"
    packages = {}
    page = 1
    while True:
        releases = get(f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}")
        if not releases:
            break
        for rel in releases:
            if rel.get("draft"):
                continue
            assets = {a["name"]: a["browser_download_url"] for a in rel.get("assets", [])}
            if "package.json" not in assets:
                continue
            manifest = get(assets["package.json"])
            zip_name = f"{manifest['name']}-{manifest['version']}.zip"
            if zip_name not in assets:
                print(f"skip {rel['tag_name']}: {zip_name} missing")
                continue
            manifest["url"] = assets[zip_name]
            manifest["zipSHA256"] = hashlib.sha256(get(assets[zip_name], raw=True)).hexdigest()
            packages.setdefault(manifest["name"], {"versions": {}})["versions"][manifest["version"]] = manifest
            print(f"listed {manifest['name']} {manifest['version']}")
        page += 1

    listing = {"name": LISTING_NAME, "id": LISTING_ID, "url": f"{base}/index.json", "author": AUTHOR,
               "packages": packages}
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(listing, f, ensure_ascii=False, indent=2)
    open(os.path.join(out, ".nojekyll"), "w").close()
    add = f"vcc://vpm/addRepo?url={base}/index.json"
    html = f"""<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{LISTING_NAME} VPM</title>
<style>body{{font-family:system-ui,sans-serif;max-width:640px;margin:48px auto;padding:0 16px;line-height:1.6}}
code{{background:#eee;padding:2px 6px;border-radius:4px;word-break:break-all}}
a.button{{display:inline-block;padding:10px 18px;background:#2563eb;color:#fff;border-radius:6px;text-decoration:none}}</style>
</head><body>
<h1>{LISTING_NAME}</h1>
<p><a class="button" href="{add}">Add to VCC</a></p>
<p>VRChat Creator Companion &gt; Settings &gt; Packages &gt; Add Repository:<br><code>{base}/index.json</code></p>
<p><a href="https://github.com/{repo}">GitHub</a></p>
</body></html>
"""
    with open(os.path.join(out, "index.html"), "w", encoding="utf-8") as f:
        f.write(html)


if __name__ == "__main__":
    main()
