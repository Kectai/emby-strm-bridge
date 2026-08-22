#!/usr/bin/env sh
set -eu

project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$project_root"

if ! command -v rg >/dev/null 2>&1; then
  echo "Privacy check failed: ripgrep is required."
  exit 1
fi

symlinks=$(find . -path './.git' -prune -o -path './.local' -prune -o \
  -path './artifacts' -prune -o -type l -print)
if [ -n "$symlinks" ]; then
  echo "Privacy check failed: symbolic links are not allowed in source or release inputs."
  echo "$symlinks"
  exit 1
fi

finder_metadata=$(git ls-files | rg '(^|/)\.DS_Store$' || true)
if [ -n "$finder_metadata" ]; then
  echo "Privacy check failed: Finder metadata is not allowed in source or release inputs."
  echo "$finder_metadata"
  exit 1
fi

matches=$(rg -l --hidden --no-ignore \
  -g '!**/.git/**' -g '!.local/**' -g '!artifacts/**' \
  -g '!scripts/check-privacy.sh' \
  '(/Users/[^/[:space:]]+/|/Volumes/[^/]+/|[A-Za-z]:\\Users\\[^\\]+\\|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16})' \
  . || true)
if [ -n "$matches" ]; then
  echo "Privacy check failed: a file contains a machine path or credential-shaped value."
  echo "$matches"
  exit 1
fi

production_matches=$(rg -l --hidden --no-ignore \
  -g 'src/**' \
  '(Dropbox|OneDrive|Google Drive|Aliyun|AList|Alist|OpenList|115\.com|PikPak|WebDAV vendor)' \
  . || true)
if [ -n "$production_matches" ]; then
  echo "Privacy check failed: production code contains vendor-specific text."
  echo "$production_matches"
  exit 1
fi

unsafe_logging=$(rg -l --hidden --no-ignore \
  -g 'src/**' \
  'logger\.[A-Za-z]+\([^;]*(SourceUri|GetLocation\(|UserAgent|\.Ticket|\.Path)' \
  . || true)
if [ -n "$unsafe_logging" ]; then
  echo "Privacy check failed: production logging may include sensitive request or source material."
  echo "$unsafe_logging"
  exit 1
fi

hardcoded_endpoints=$(rg -l --hidden --no-ignore \
  -g 'src/**' \
  'https?://' \
  . || true)
if [ -n "$hardcoded_endpoints" ]; then
  echo "Hardcoding check failed: production code contains a fixed HTTP endpoint."
  echo "$hardcoded_endpoints"
  exit 1
fi

unsafe_extension_registration=$(rg -l --hidden --no-ignore \
  -g 'src/**' \
  '(\.AddParts\(|\.SaveMetadata\()' \
  . || true)
if [ -n "$unsafe_extension_registration" ]; then
  echo "Architecture check failed: production code bypasses automatic discovery or repository persistence."
  echo "$unsafe_extension_registration"
  exit 1
fi

unpinned_actions=$(rg -n --hidden --no-ignore -P \
  -g '.github/workflows/*.yml' \
  '^\s*-\s+uses:\s+[^@\s]+@(?![0-9a-f]{40}(?:\s|$))' \
  . || true)
if [ -n "$unpinned_actions" ]; then
  echo "Supply-chain check failed: workflow actions must be pinned to full commit hashes."
  echo "$unpinned_actions"
  exit 1
fi

release_dll="$project_root/.local/build/bin/Release/netstandard2.1/Emby.StrmBridge.dll"
if [ -f "$release_dll" ] && strings "$release_dll" | rg -q '(/Users/|/Volumes/|[A-Za-z]:\\Users\\)'; then
  echo "Privacy check failed: the Release DLL contains a machine-specific absolute path."
  exit 1
fi

if [ -f "$release_dll" ] && strings "$release_dll" | rg -q \
  '(BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16})'; then
  echo "Privacy check failed: the Release DLL contains a credential-shaped value."
  exit 1
fi

echo "Privacy check passed."
