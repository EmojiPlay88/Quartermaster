#!/usr/bin/env bash
# Builds Quartermaster-<v>-x86_64.AppImage plus its .sha256 with a pinned appimagetool and a pinned type2 runtime.
# The input publish folder is never modified: the AppDir holds a staged copy with package kind linux-appimage.
# Usage: build/linux/make-appimage.sh <publish-folder> <version> <out-dir>
set -eu
publish="${1:?usage: make-appimage.sh <publish-folder> <version> <out-dir>}"
version="${2:?usage: make-appimage.sh <publish-folder> <version> <out-dir>}"
out="${3:?usage: make-appimage.sh <publish-folder> <version> <out-dir>}"
here="$(cd "$(dirname "$0")" && pwd)"

TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
TOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64"
RUNTIME_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

die() { echo "make-appimage: $*" >&2; exit 1; }

[ -d "$publish" ] || die "missing publish folder $publish"
[ -f "$here/../icon/quartermaster.png" ] || die "missing build/icon/quartermaster.png"

# Downloads $1 to $2 unless the cached file already matches $3; always verifies before use.
fetch() {
  local url="$1" dest="$2" want="$3" got
  if [ -f "$dest" ] && [ "$(sha256sum "$dest" | cut -d' ' -f1)" = "$want" ]; then return 0; fi
  curl -fsSL --retry 3 -o "$dest.tmp" "$url" || die "download failed: $url"
  got="$(sha256sum "$dest.tmp" | cut -d' ' -f1)"
  if [ "$got" != "$want" ]; then rm -f "$dest.tmp"; die "SHA-256 mismatch for $url (got $got, expected $want)"; fi
  mv "$dest.tmp" "$dest"
}

cache="${XDG_CACHE_HOME:-$HOME/.cache}/quartermaster-build"
mkdir -p "$cache" "$out"
out="$(cd "$out" && pwd)"
fetch "$TOOL_URL" "$cache/appimagetool-1.9.1-x86_64.AppImage" "$TOOL_SHA256"
fetch "$RUNTIME_URL" "$cache/type2-runtime-20251108-x86_64" "$RUNTIME_SHA256"
chmod +x "$cache/appimagetool-1.9.1-x86_64.AppImage"

name="Quartermaster-$version-x86_64.AppImage"
rm -f "$out/$name" "$out/$name.sha256"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
appdir="$work/Quartermaster.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$appdir/usr/bin/"
bash "$here/../set-package-kind.sh" "$appdir/usr/bin" linux-appimage
cp "$here/AppRun" "$appdir/AppRun"
cp "$here/quartermaster.desktop" "$appdir/quartermaster.desktop"
cp "$here/../icon/quartermaster.png" "$appdir/quartermaster.png"
cp "$here/../icon/quartermaster.png" "$appdir/usr/share/icons/hicolor/256x256/apps/quartermaster.png"
chmod 755 "$appdir/AppRun"

# --appimage-extract-and-run: no FUSE needed. No -u, so no update information is embedded.
ARCH=x86_64 "$cache/appimagetool-1.9.1-x86_64.AppImage" --appimage-extract-and-run \
  --no-appstream --runtime-file "$cache/type2-runtime-20251108-x86_64" \
  "$appdir" "$out/$name"
chmod +x "$out/$name"
( cd "$out" && printf '%s  %s\n' "$(sha256sum "$name" | cut -d' ' -f1)" "$name" > "$name.sha256" )
echo "built $out/$name"
