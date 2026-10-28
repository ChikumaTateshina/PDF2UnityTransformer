"""Build the VPM listing (index.json) and the install/intro page (.github/pages/index.html) from the releases.

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
    latest = "-"
    unity = "2022.3"
    versions = [v for p in packages.values() for v in p["versions"].values()]
    if versions:
        newest = max(versions, key=lambda m: tuple(int(x) if x.isdigit() else 0
                                                   for x in m["version"].split("-")[0].split(".")))
        latest, unity = newest["version"], newest.get("unity", unity)
    template = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "pages", "index.html")
    with open(template, encoding="utf-8") as f:
        html = f.read()
    for key, value in {
        "VCC_URL": f"vcc://vpm/addRepo?url={base}/index.json",
        "LISTING_URL": f"{base}/index.json",
        "REPO_URL": f"https://github.com/{repo}",
        "LATEST_VERSION": latest,
        "UNITY": unity,
    }.items():
        html = html.replace("{{" + key + "}}", value)
    with open(os.path.join(out, "index.html"), "w", encoding="utf-8") as f:
        f.write(html)


if __name__ == "__main__":
    main()
