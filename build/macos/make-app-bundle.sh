#!/usr/bin/env bash
# Turns the osx-arm64 `dotnet publish` folder of Quartermaster.Desktop into an ad-hoc signed Quartermaster.app.
# The whole publish folder goes into Contents/MacOS unchanged; Info.plist comes from Info.plist.in.
# Signing (macOS only): ad-hoc `codesign --force --sign -` on every file in Contents/MacOS except the main executable, then the bundle (no --deep).
# --no-sign skips codesign and plutil so the script can be exercised on Linux/Windows.
# Usage: build/macos/make-app-bundle.sh <publish-dir> <version> <out-dir> [--no-sign]
set -u
pub="${1:?usage: make-app-bundle.sh <publish-dir> <version> <out-dir> [--no-sign]}"
version="${2:?usage: make-app-bundle.sh <publish-dir> <version> <out-dir> [--no-sign]}"
out="${3:?usage: make-app-bundle.sh <publish-dir> <version> <out-dir> [--no-sign]}"
nosign=0; [ "${4:-}" = "--no-sign" ] && nosign=1
# Minimum macOS for .NET 10, per dotnet/core release-notes/10.0/supported-os.md; verify
minos="14.0"
here="$(cd "$(dirname "$0")" && pwd)"

die() { echo "make-app-bundle: $*" >&2; exit 1; }

[ -f "$pub/Quartermaster" ] || die "missing $pub/Quartermaster (is this an osx-arm64 publish folder?)"
marker="$pub/quartermaster-package.json"
[ -f "$marker" ] || die "missing $marker"
kind=$(sed -n 's/.*"package"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$marker" | head -n 1)
mver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$marker" | head -n 1)
[ "$kind" = "mac-app" ] || die "package marker says '$kind', expected 'mac-app'"
[ "$mver" = "$version" ] || die "package marker version is '$mver', expected '$version'"

app="$out/Quartermaster.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" || die "cannot create $app"
cp -Rp "$pub"/. "$app/Contents/MacOS/" || die "copy of $pub failed"
sed -e "s/@VERSION@/$version/g" -e "s/@MINOS@/$minos/g" "$here/Info.plist.in" > "$app/Contents/Info.plist" || die "cannot write Info.plist"
chmod +x "$app/Contents/MacOS/Quartermaster"
[ -f "$app/Contents/MacOS/Quartermaster.Updater" ] && chmod +x "$app/Contents/MacOS/Quartermaster.Updater"

if [ "$nosign" = 0 ]; then
  plutil -lint "$app/Contents/Info.plist" >/dev/null || die "Info.plist failed plutil -lint"
  # codesign treats every file in Contents/MacOS as nested code, so sign them all (non-Mach-O files get xattr signatures).
  # The main executable is left out: signing the bundle below signs it.
  while IFS= read -r f; do
    codesign --force --sign - "$f" || die "codesign failed for $f"
  done < <(find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Quartermaster")
  codesign --force --sign - "$app" || die "codesign failed for $app"
  codesign --verify --strict --verbose=2 "$app" || die "codesign verify failed for $app"
fi
echo "built $app"
