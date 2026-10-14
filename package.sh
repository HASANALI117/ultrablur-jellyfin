#!/usr/bin/env bash
# Package the UltraBlur plugin for a Jellyfin plugin-repository feed.
#
#   ./package.sh [jf10|jf12]     (default: jf10 = Jellyfin 10.11 / net9.0)
#
# Produces out/UltraBlur_<version>_<target>.zip and upserts its manifest.json
# entry (version + targetAbi): checksum, timestamp, sourceUrl = the GitHub
# release asset for tag v<version>. Publish the release (command printed at
# the end) BEFORE pushing manifest.json, so the feed never points at a missing
# asset. BASE_URL=<dir-url> overrides the download location.
set -euo pipefail
cd "$(dirname "$0")"

TARGET="${1:-jf10}"
case "$TARGET" in
  jf10) TFM=net9.0;  ABI=10.11.0.0 ;;
  jf12) TFM=net10.0; ABI=12.0.0.0  ;;
  *) echo "usage: $0 [jf10|jf12]" >&2; exit 2 ;;
esac

VERSION=$(sed -n 's/.*<AssemblyVersion>\([^<]*\)<\/AssemblyVersion>.*/\1/p' UltraBlur.Plugin/UltraBlur.Plugin.csproj)
BASE_URL="${BASE_URL:-https://github.com/HASANALI117/ultrablur-jellyfin/releases/download/v$VERSION}"
STAGE=out/.stage
rm -rf "$STAGE"
trap 'rm -rf "$STAGE"' EXIT

# ContinuousIntegrationBuild maps source paths to /_/ (no local paths in the PDB record).
dotnet publish UltraBlur.Plugin -c Release -p:JellyfinTarget="$TARGET" -p:ContinuousIntegrationBuild=true -o "$STAGE/publish"

mkdir -p out "$STAGE/zip"
# The Jellyfin host supplies its own assemblies; ship only ours + SQLite.
cp "$STAGE/publish/UltraBlur.Plugin.dll" \
   "$STAGE/publish/Microsoft.Data.Sqlite.dll" \
   "$STAGE/publish"/SQLitePCLRaw.*.dll \
   "$STAGE/zip/"

cat > "$STAGE/zip/meta.json" <<EOF
{
  "guid": "b6f0f0d4-51c3-4c9a-8f42-3f2a9be0c6d1",
  "name": "UltraBlur",
  "overview": "Plex-style ambient gradients for Jellyfin detail pages.",
  "owner": "HASANALI117",
  "category": "General",
  "version": "$VERSION",
  "targetAbi": "$ABI",
  "framework": "$TFM",
  "assemblies": ["UltraBlur.Plugin.dll"]
}
EOF

ZIP="out/UltraBlur_${VERSION}_${TARGET}.zip"
rm -f "$ZIP"
# python zipfile: portable (git-bash on Windows has no `zip` binary).
python - "$STAGE/zip" "$ZIP" <<'PYEOF'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
PYEOF

MD5=$(md5sum "$ZIP" | cut -d' ' -f1)
python - manifest.json "$VERSION" "$ABI" "${MD5^^}" "$BASE_URL/$(basename "$ZIP")" <<'PYEOF'
import datetime, json, sys
path, version, abi, md5, url = sys.argv[1:]
doc = json.load(open(path, encoding="utf-8"))
versions = doc[0]["versions"]
entry = next((v for v in versions if v["version"] == version and v["targetAbi"] == abi), None)
if entry is None:
    entry = {"changelog": "TODO", "targetAbi": abi, "version": version}
    versions.insert(0, entry)
entry.update(sourceUrl=url, checksum=md5,
             timestamp=datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write(json.dumps(doc, indent=2) + "\n")
PYEOF

echo "built:    $ZIP"
echo "md5:      $MD5"
echo "manifest: entry $VERSION / $ABI updated -> $BASE_URL"
echo "release:  gh release create v$VERSION $ZIP --title \"UltraBlur $VERSION\" --notes \"<changelog>\"  (gh release upload v$VERSION $ZIP for a second target)"
