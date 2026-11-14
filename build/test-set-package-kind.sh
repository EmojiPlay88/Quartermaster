#!/usr/bin/env bash
# Tests build/set-package-kind.sh against temp folders.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
tool="$here/set-package-kind.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fail=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2] got [$3]"; fail=1; fi; }

mkdir "$tmp/a"
printf '{"version":"0.2.0","package":"win-zip"}\n' > "$tmp/a/quartermaster-package.json"
bash "$tool" "$tmp/a" linux-deb >/dev/null; check "rewrite exit" 0 $?
check "rewrite json" '{"version":"0.2.0","package":"linux-deb"}' "$(tr -d '\r' < "$tmp/a/quartermaster-package.json")"

for k in win-zip win-setup mac-app linux-tar linux-appimage linux-deb linux-rpm; do
  bash "$tool" "$tmp/a" "$k" >/dev/null; check "kind $k exit" 0 $?
  check "kind $k json" "{\"version\":\"0.2.0\",\"package\":\"$k\"}" "$(tr -d '\r' < "$tmp/a/quartermaster-package.json")"
done

before="$(cat "$tmp/a/quartermaster-package.json")"
bash "$tool" "$tmp/a" bogus >/dev/null 2>&1; rc=$?
check "bad kind refused" 1 $((rc != 0))
check "bad kind leaves file" "$before" "$(cat "$tmp/a/quartermaster-package.json")"

mkdir "$tmp/b"
bash "$tool" "$tmp/b" linux-deb >/dev/null 2>&1; rc=$?
check "missing marker refused" 1 $((rc != 0))
check "missing marker not created" 0 $([ -e "$tmp/b/quartermaster-package.json" ] && echo 1 || echo 0)

mkdir "$tmp/c"; echo 'not json' > "$tmp/c/quartermaster-package.json"
bash "$tool" "$tmp/c" linux-deb >/dev/null 2>&1; rc=$?
check "unparseable marker refused" 1 $((rc != 0))

bash "$tool" "$tmp/a" >/dev/null 2>&1; rc=$?
check "missing args refused" 1 $((rc != 0))

[ $fail = 0 ] && echo "ALL PASSED" || { echo "FAILURES"; exit 1; }
