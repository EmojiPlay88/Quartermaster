#!/usr/bin/env bash
# Structure checks for a Quartermaster.app bundle made by make-app-bundle.sh.
# Checks Info.plist values, the executables, the package marker, the file list and (unless --no-sign) ad-hoc signatures.
# plutil and codesign only exist on macOS; --no-sign skips them so the check also runs on Linux/Windows CI.
# Usage: build/macos/test-app-bundle.sh <Quartermaster.app> <version> [--no-sign]
set -u
app="${1:?usage: test-app-bundle.sh <Quartermaster.app> <version> [--no-sign]}"
version="${2:?usage: test-app-bundle.sh <Quartermaster.app> <version> [--no-sign]}"
nosign=0; [ "${3:-}" = "--no-sign" ] && nosign=1
bad=0
ok()   { echo "ok       $1"; }
fail() { echo "FAIL     $1" >&2; bad=1; }

plist="$app/Contents/Info.plist"
macos="$app/Contents/MacOS"

if [ -s "$plist" ]; then
  ok "Info.plist"
  if [ "$nosign" = 0 ]; then
    if plutil -lint "$plist" >/dev/null; then ok "plutil -lint"; else fail "plutil -lint Info.plist"; fi
  fi
  # Each entry sits on one line: <key>K</key><string>V</string> (or <true/>).
  expect() {
    if grep -Fq "<key>$1</key><$2" "$plist" && { [ "$2" = "true/>" ] || grep -Fq "<key>$1</key><string>$3</string>" "$plist"; }; then
      ok "plist $1"
    else fail "plist $1 should be '$3'"; fi
  }
  expect CFBundleIdentifier string io.github.emojiplay88.quartermaster
  expect CFBundleName string Quartermaster
  expect CFBundleDisplayName string Quartermaster
  expect CFBundleExecutable string Quartermaster
  expect CFBundlePackageType string APPL
  expect CFBundleShortVersionString string "$version"
  expect CFBundleVersion string "$version"
  expect LSApplicationCategoryType string public.app-category.strategy-games
  expect NSPrincipalClass string NSApplication
  expect NSHighResolutionCapable "true/>" ""
  if grep -Eq '<key>LSMinimumSystemVersion</key><string>[0-9]+(\.[0-9]+)*</string>' "$plist"; then ok "plist LSMinimumSystemVersion"; else fail "plist LSMinimumSystemVersion missing"; fi
  if grep -Fq "CFBundleIconFile" "$plist"; then fail "plist must not have an icon key"; fi
  if grep -Fq "@" "$plist"; then fail "plist still has an unfilled @PLACEHOLDER@"; fi
else
  fail "Contents/Info.plist (in $app)"
fi

for f in Quartermaster Quartermaster.Updater; do
  if [ -f "$macos/$f" ] && [ -x "$macos/$f" ]; then ok "executable $f"; else fail "Contents/MacOS/$f missing or not executable"; fi
done

marker="$macos/quartermaster-package.json"
if [ -s "$marker" ]; then
  kind=$(sed -n 's/.*"package"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$marker" | head -n 1)
  ver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$marker" | head -n 1)
  if [ "$kind" = "mac-app" ]; then ok "marker package mac-app"; else fail "marker package is '$kind', want mac-app"; fi
  if [ "$ver" = "$version" ]; then ok "marker version $version"; else fail "marker version is '$ver', want $version"; fi
else
  fail "Contents/MacOS/quartermaster-package.json"
fi
if [ -s "$macos/quartermaster-files.txt" ]; then ok "quartermaster-files.txt"; else fail "Contents/MacOS/quartermaster-files.txt"; fi

if [ "$nosign" = 0 ]; then
  if codesign --verify --strict "$app" 2>/dev/null; then ok "codesign bundle"; else fail "codesign --verify --strict $app"; fi
  if codesign --verify --strict "$macos/Quartermaster.Updater" 2>/dev/null; then ok "codesign Quartermaster.Updater"; else fail "codesign Quartermaster.Updater"; fi
  while IFS= read -r d; do
    if codesign --verify --strict "$d" 2>/dev/null; then ok "codesign ${d#"$macos"/}"; else fail "codesign ${d#"$macos"/}"; fi
  done < <(find "$macos" -name '*.dylib' -type f)
fi
exit $bad
