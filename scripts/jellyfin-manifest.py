#!/usr/bin/env python3
"""Writes the Jellyfin plugin repository manifest from the repo's published GitHub releases.

Usage: scripts/jellyfin-manifest.py <owner/repo> <image url> <out.json>
Reads plugin details from build.yaml. Drafts and prereleases are left out: their assets are
not public, so Jellyfin could not download them.
"""
import hashlib
import json
import os
import re
import sys
import urllib.request

repo, image_url, out = sys.argv[1:4]
api = f"https://api.github.com/repos/{repo}/releases?per_page=100"
headers = {"Accept": "application/vnd.github+json"}
if os.environ.get("GH_TOKEN"):
    headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]


def get(url, accept=None):
    request = urllib.request.Request(url, headers=dict(headers, **({"Accept": accept} if accept else {})))
    with urllib.request.urlopen(request) as response:
        return response.read()


# build.yaml is flat apart from the folded description and the artifacts list; only the plain
# "key: value" lines are needed here, so no YAML library is pulled in.
meta = {}
with open("build.yaml", encoding="utf-8") as f:
    text = f.read()
for key in ("name", "guid", "targetAbi", "owner", "overview", "category"):
    match = re.search(rf'^{key}:\s*"?(.*?)"?\s*$', text, re.M)
    meta[key] = match.group(1) if match else ""
description = re.search(r"^description:\s*>\s*\n((?:[ \t]+.*\n?)+)", text, re.M)
meta["description"] = " ".join(line.strip() for line in description.group(1).splitlines()).strip() if description else meta["overview"]

versions = []
for release in json.loads(get(api)):
    if release["draft"] or release["prerelease"]:
        continue
    asset = next((a for a in release["assets"] if re.fullmatch(r"AniMetaPlus-Jellyfin-v.*\.zip", a["name"])), None)
    if asset is None:
        continue
    checksum = hashlib.md5(get(asset["url"], "application/octet-stream")).hexdigest()
    version = release["tag_name"].lstrip("v")
    version += ".0" * (3 - version.count("."))
    # The release body ends with a "Full changelog" link below a rule; Jellyfin shows the rest.
    changelog = (release["body"] or "").replace("\r\n", "\n").split("\n---\n")[0].strip()
    versions.append({
        "version": version,
        "changelog": changelog,
        "targetAbi": meta["targetAbi"],
        "sourceUrl": asset["browser_download_url"],
        "checksum": checksum,
        "timestamp": release["published_at"],
    })

versions.sort(key=lambda v: [int(p) for p in v["version"].split(".")], reverse=True)

manifest = [{
    "guid": meta["guid"],
    "name": meta["name"],
    "overview": meta["overview"],
    "description": meta["description"],
    "owner": meta["owner"],
    "category": meta["category"],
    "imageUrl": image_url,
    "versions": versions,
}]

with open(out, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"{len(versions)} version(s): " + ", ".join(v["version"] for v in versions))
