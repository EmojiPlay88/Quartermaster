#!/usr/bin/env bash
# Usage: set-package-kind.sh <folder> <kind>
# Rewrites <folder>/quartermaster-package.json to {"version":"<same>","package":"<kind>"},
# the same shape the Desktop csproj (QmWritePackageMarker) writes. Needs python3 (or python).
set -eu

kinds="win-zip win-setup mac-app linux-tar linux-appimage linux-deb linux-rpm"
if [ "$#" -ne 2 ]; then
  echo "usage: set-package-kind.sh <folder> <kind>" >&2; exit 2
fi
folder="$1"; kind="$2"
case " $kinds " in
  *" $kind "*) ;;
  *) echo "set-package-kind: unknown kind '$kind' (valid: $kinds)" >&2; exit 1 ;;
esac
marker="$folder/quartermaster-package.json"
if [ ! -f "$marker" ]; then
  echo "set-package-kind: no quartermaster-package.json in '$folder'" >&2; exit 1
fi
py=python3
command -v python3 >/dev/null 2>&1 || py=python
"$py" -I - "$marker" "$kind" <<'PY'
import json, sys
path, kind = sys.argv[1], sys.argv[2]
try:
    with open(path, encoding="utf-8-sig") as f:
        data = json.load(f)
    version = data["version"]
    if not isinstance(version, str) or not version:
        raise ValueError("version")
except Exception:
    sys.exit("set-package-kind: '%s' is not a valid package marker" % path)
with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write(json.dumps({"version": version, "package": kind}, separators=(",", ":")) + "\n")
PY
echo "package kind set to $kind"
