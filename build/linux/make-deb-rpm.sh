#!/usr/bin/env bash
# Builds quartermaster_<v>_amd64.deb and quartermaster-<v>-1.x86_64.rpm plus their .sha256 with a pinned nfpm.
# The input publish folder is never modified: each package gets its own staged copy with its own package kind.
# Usage: build/linux/make-deb-rpm.sh <publish-folder> <version> <out-dir>
set -eu
publish="${1:?usage: make-deb-rpm.sh <publish-folder> <version> <out-dir>}"
version="${2:?usage: make-deb-rpm.sh <publish-folder> <version> <out-dir>}"
out="${3:?usage: make-deb-rpm.sh <publish-folder> <version> <out-dir>}"
here="$(cd "$(dirname "$0")" && pwd)"

NFPM_VERSION="2.48.0"
NFPM_URL="https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VERSION/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz"
NFPM_SHA256="6d7f9a28b2f397934403950fcbba83f598b08d02edbe79a0815b3b4897341750"

die() { echo "make-deb-rpm: $*" >&2; exit 1; }

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
fetch "$NFPM_URL" "$cache/nfpm-$NFPM_VERSION-linux-x86_64.tar.gz" "$NFPM_SHA256"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/tool"
tar -xzf "$cache/nfpm-$NFPM_VERSION-linux-x86_64.tar.gz" -C "$work/tool" nfpm
nfpm="$work/tool/nfpm"

icon="$(cd "$here/../icon" && pwd)/quartermaster.png"

# <packager> <kind> <file name>
build_one() {
  local packager="$1" kind="$2" name="$3"
  rm -f "$out/$name" "$out/$name.sha256"
  local stage="$work/stage-$packager" cfg="$work/nfpm-$packager.yaml"
  mkdir -p "$stage"
  cp -a "$publish/." "$stage/"
  bash "$here/../set-package-kind.sh" "$stage" "$kind"
  # nfpm v2.48.0 does not expand env vars in contents[].src, so render real paths into the config.
  python3 -I - "$here/nfpm.yaml" "$cfg" "$version" "$stage" "$here/quartermaster.desktop" "$icon" <<'PY'
import sys
src, dst, version, stage, desktop, icon = sys.argv[1:]
t = open(src).read()
for k, v in (("QM_VERSION", version), ("QM_STAGE", stage), ("QM_DESKTOP", desktop), ("QM_ICON", icon)):
    t = t.replace("${" + k + "}", v)
if "${" in t:
    sys.exit("unsubstituted placeholder left in nfpm.yaml")
open(dst, "w").write(t)
PY
  "$nfpm" package --config "$cfg" --packager "$packager" --target "$out/$name"
  [ -f "$out/$name" ] || die "nfpm did not produce $out/$name"
  ( cd "$out" && printf '%s  %s\n' "$(sha256sum "$name" | cut -d' ' -f1)" "$name" > "$name.sha256" )
  echo "built $out/$name"
}

build_one deb linux-deb "quartermaster_${version}_amd64.deb"
build_one rpm linux-rpm "quartermaster-${version}-1.x86_64.rpm"
