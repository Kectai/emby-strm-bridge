#!/usr/bin/env sh
set -eu

project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"

"$project_root/scripts/verify.sh"

version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' src/Emby.StrmBridge/Emby.StrmBridge.csproj)
if ! printf '%s\n' "$version" | rg -q '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'; then
  echo "Package validation failed: the project version is missing or unsafe."
  exit 1
fi
stage="$project_root/.local/package/Emby.StrmBridge"
archive="$project_root/artifacts/Emby.StrmBridge-$version.zip"
mkdir -p "$stage" "$project_root/artifacts"
find "$stage" -mindepth 1 -delete
cp "$project_root/.local/build/bin/Release/netstandard2.1/Emby.StrmBridge.dll" "$stage/"
cp "$project_root/README.md" "$project_root/LICENSE" "$project_root/CHANGELOG.md" \
  "$project_root/THIRD_PARTY_NOTICES.md" "$stage/"
mkdir -p "$stage/LICENSES"
cp "$project_root/LICENSES/Lib.Harmony-LICENSE.txt" "$stage/LICENSES/"
mkdir -p "$stage/docs"
cp "$project_root/docs/ARCHITECTURE.md" \
  "$project_root/docs/COMPATIBILITY.md" \
  "$project_root/docs/INSTALL.md" \
  "$project_root/docs/PLAYBACK_GATEWAY_DESIGN.md" \
  "$project_root/docs/SECURITY.md" \
  "$project_root/docs/TESTING.md" \
  "$stage/docs/"
find "$stage" -name '.DS_Store' -type f -delete
# Normalize every staged entry so identical inputs produce an identical archive.
find "$stage" -exec touch -t 200001010000 {} +
if rg -l '(Dropbox|OneDrive|Google Drive|Aliyun|AList|Alist|OpenList|115\.com|PikPak|WebDAV vendor)' \
  "$stage" --glob '*.md' >/dev/null; then
  echo "Package validation failed: release documentation contains vendor-specific text."
  exit 1
fi
cmp "$project_root/.local/build/bin/Release/netstandard2.1/Emby.StrmBridge.dll" "$stage/Emby.StrmBridge.dll"

if [ -f "$archive" ]; then
  rm "$archive"
fi

(cd "$project_root/.local/package" && zip -X -q -r "$archive" Emby.StrmBridge)

unzip -tqq "$archive"

unexpected=$(unzip -Z1 "$archive" | rg -v \
  '^Emby\.StrmBridge/$|^Emby\.StrmBridge/(Emby\.StrmBridge\.dll|README\.md|LICENSE|CHANGELOG\.md|THIRD_PARTY_NOTICES\.md)$|^Emby\.StrmBridge/LICENSES/$|^Emby\.StrmBridge/LICENSES/Lib\.Harmony-LICENSE\.txt$|^Emby\.StrmBridge/docs/$|^Emby\.StrmBridge/docs/(ARCHITECTURE|COMPATIBILITY|INSTALL|PLAYBACK_GATEWAY_DESIGN|SECURITY|TESTING)\.md$' \
  || true)
if [ -n "$unexpected" ]; then
  echo "Package validation failed: the archive contains an unexpected entry."
  echo "$unexpected"
  exit 1
fi

entry_count=$(unzip -Z1 "$archive" | wc -l | tr -d ' ')
if [ "$entry_count" -ne 15 ]; then
  echo "Package validation failed: the archive allowlist is incomplete."
  exit 1
fi

if zipinfo -v "$archive" | rg -q 'length of extra field:[[:space:]]+[1-9]'; then
  echo "Package validation failed: ZIP extra fields may expose host metadata."
  exit 1
fi

verified_dll="$project_root/.local/package/verified-Emby.StrmBridge.dll"
unzip -p "$archive" Emby.StrmBridge/Emby.StrmBridge.dll > "$verified_dll"
cmp "$project_root/.local/build/bin/Release/netstandard2.1/Emby.StrmBridge.dll" "$verified_dll"
rm "$verified_dll"

echo "$archive"
