#!/usr/bin/env bash
# Builds the drag-to-Applications dmg (dmgbuild) and the update zip from Quartermaster.app, plus .sha256 files.
# Then mounts the dmg read-only and checks its contents; the mount is always detached.
# macOS only (hdiutil, ditto, shasum); needs dmgbuild on PATH.
# Usage: build/macos/make-dmg.sh <Quartermaster.app> <version> <out-dir>
set -u
app="${1:?usage: make-dmg.sh <Quartermaster.app> <version> <out-dir>}"
version="${2:?usage: make-dmg.sh <Quartermaster.app> <version> <out-dir>}"
out="${3:?usage: make-dmg.sh <Quartermaster.app> <version> <out-dir>}"
here="$(cd "$(dirname "$0")" && pwd)"

die() { echo "make-dmg: $*" >&2; exit 1; }

command -v dmgbuild >/dev/null 2>&1 || die "dmgbuild not found on PATH (pip install dmgbuild)"
[ -d "$app" ] || die "missing app bundle $app"
[ -f "$here/dmg-background.png" ] || die "missing $here/dmg-background.png"
mkdir -p "$out" || die "cannot create $out"
app="$(cd "$app" && pwd)"
out="$(cd "$out" && pwd)"

base="Quartermaster-$version-osx-arm64"
dmg="$out/$base.dmg"
zip="$out/$base.zip"
rm -f "$dmg" "$zip" "$dmg.sha256" "$zip.sha256"

# Temp dirs are removed (and the dmg detached) on every exit, including a failed dmgbuild or ditto.
bgtmp=""
mnt=""
attached=0
cleanup() {
  [ -n "$bgtmp" ] && rm -rf "$bgtmp"
  if [ "$attached" = 1 ]; then hdiutil detach "$mnt" -force >/dev/null 2>&1; fi
  [ -n "$mnt" ] && rmdir "$mnt" 2>/dev/null
  return 0
}
trap cleanup EXIT

# Retina background: one multi-resolution tiff when tiffutil exists, else the 1x png.
tmpbase="${TMPDIR:-/tmp}"
tmpbase="${tmpbase%/}"
bg="$here/dmg-background.png"
if command -v tiffutil >/dev/null 2>&1 && [ -f "$here/dmg-background@2x.png" ]; then
  bgtmp="$(mktemp -d "$tmpbase/qm-dmgbg.XXXXXX")" || die "cannot create background dir"
  tiffutil -cathidpicheck "$here/dmg-background.png" "$here/dmg-background@2x.png" -out "$bgtmp/dmg-background.tiff" >/dev/null 2>&1 \
    && [ -f "$bgtmp/dmg-background.tiff" ] && bg="$bgtmp/dmg-background.tiff"
fi

dmgbuild -s "$here/dmg-settings.py" -D app="$app" -D background="$bg" "Quartermaster $version" "$dmg" || die "dmgbuild failed"
# --norsrc --noextattr: no ._ AppleDouble entries (the updater's extractor would write them as real files). The managed files'
# xattr signatures are therefore not in the update zip; the in-app updater re-signs the staged bundle with the make-app-bundle.sh
# file rule (src/Quartermaster.Render/Update/UpdateStaging.cs) so the bundle seal holds again after an update.
ditto -c -k --norsrc --noextattr --keepParent "$app" "$zip" || die "ditto zip failed"
[ -f "$dmg" ] || die "dmgbuild produced no $dmg"
[ -f "$zip" ] || die "ditto produced no $zip"

for f in "$dmg" "$zip"; do
  sum=$(shasum -a 256 "$f") || die "shasum failed for $f"
  digest=$(printf '%s\n' "$sum" | awk '{print $1}')
  [ -n "$digest" ] || die "empty sha256 for $f"
  echo "$digest  $(basename "$f")" > "$f.sha256"
done

# Mount check; always detach.
mnt="$(mktemp -d "$tmpbase/qm-dmg.XXXXXX")" || die "cannot create mount dir"
hdiutil attach -nobrowse -readonly -mountpoint "$mnt" "$dmg" >/dev/null || die "cannot mount $dmg"
attached=1
[ -d "$mnt/Quartermaster.app" ] || die "dmg lacks Quartermaster.app"
[ -L "$mnt/Applications" ] || die "dmg lacks the Applications symlink"
[ "$(readlink "$mnt/Applications")" = "/Applications" ] || die "Applications symlink does not point to /Applications"
codesign --verify --strict "$mnt/Quartermaster.app" || die "app inside the dmg fails codesign verification"
# dmgbuild writes the background as a hidden file .background.<ext> at the volume root (a .background/ folder is also accepted).
bgfound=0
for b in "$mnt"/.background.* "$mnt/.background"; do [ -e "$b" ] && bgfound=1; done
[ "$bgfound" = 1 ] || die "dmg lacks a .background file"
[ -f "$mnt/.DS_Store" ] || die "dmg lacks .DS_Store"
got="$("$mnt/Quartermaster.app/Contents/MacOS/Quartermaster" --version)" || die "Quartermaster --version failed inside the dmg"
[ "$got" = "Quartermaster $version" ] || die "--version printed '$got', expected 'Quartermaster $version'"

echo "built $dmg and $zip"
