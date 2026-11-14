#!/usr/bin/env bash
# Builds Quartermaster-<v>-linux-x64.tar.gz (files at the top, no wrapping folder) plus its .sha256.
# The input publish folder is never modified: a staged copy gets package kind linux-tar.
# Usage: build/linux/make-tar.sh <publish-folder> <version> <out-dir>
set -eu
publish="${1:?usage: make-tar.sh <publish-folder> <version> <out-dir>}"
version="${2:?usage: make-tar.sh <publish-folder> <version> <out-dir>}"
out="${3:?usage: make-tar.sh <publish-folder> <version> <out-dir>}"
here="$(cd "$(dirname "$0")" && pwd)"

[ -d "$publish" ] || { echo "make-tar: missing publish folder $publish" >&2; exit 1; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"
name="Quartermaster-$version-linux-x64.tar.gz"
rm -f "$out/$name" "$out/$name.sha256"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp -a "$publish/." "$stage/"
bash "$here/../set-package-kind.sh" "$stage" linux-tar

# Fixed owner keeps the build machine's user names out of the archive; modes (exec bits) are kept.
tar --owner=0 --group=0 --numeric-owner -czf "$out/$name" -C "$stage" .
( cd "$out" && printf '%s  %s\n' "$(sha256sum "$name" | cut -d' ' -f1)" "$name" > "$name.sha256" )
echo "built $out/$name"
