#!/usr/bin/env bash
# Package the UltraBlur plugin for a Jellyfin plugin-repository feed.
#
#   ./package.sh [jf10|jf12]     (default: jf10 = Jellyfin 10.11 / net9.0)
#
# Produces out/UltraBlur_<version>_<target>.zip, prints its MD5, and patches
# manifest.json's checksum + sourceUrl filename for the built version.
# Host the zip + manifest.json anywhere HTTP-reachable by the Jellyfin server,
# then add the manifest URL under Dashboard -> Plugins -> Repositories.
set -euo pipefail
cd "$(dirname "$0")"

TARGET="${1:-jf10}"
case "$TARGET" in
  jf10) TFM=net9.0;  ABI=10.11.0.0 ;;
  jf12) TFM=net10.0; ABI=12.0.0.0  ;;
  *) echo "usage: $0 [jf10|jf12]" >&2; exit 2 ;;
esac

VERSION=$(sed -n 's/.*<AssemblyVersion>\([^<]*\)<\/AssemblyVersion>.*/\1/p' UltraBlur.Plugin/UltraBlur.Plugin.csproj)
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

dotnet publish UltraBlur.Plugin -c Release -p:JellyfinTarget="$TARGET" -o "$STAGE/publish"

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
  "owner": "local",
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
# Patch manifest for this build (checksum + zip filename).
sed -i -E "s/\"checksum\": \"[^\"]*\"/\"checksum\": \"${MD5^^}\"/" manifest.json
sed -i -E "s#(/plugins/)[^\"]+\.zip#\1UltraBlur_${VERSION}_${TARGET}.zip#" manifest.json

echo "built:    $ZIP"
echo "md5:      $MD5"
echo "manifest: manifest.json updated (set sourceUrl host, then serve both files)"
