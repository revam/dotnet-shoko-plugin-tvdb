#!/usr/bin/env bash
# Build the plugin and lay it out the way Shoko expects to find it.
#
#   pack.sh [--configuration Release] [--output dist] [--zip]
#
# The output is a directory of DLLs plus `manifest.json`, which is what
# goes under the server's `plugins/` directory. `--zip` also produces
# `<name>-<version>.zip` of the same, for hand-installing.
#
# There is deliberately no publishing step and no host in here: which
# remote a build is published to is a property of the runner, not of the
# source tree, so it is passed in rather than written down.
set -euo pipefail

configuration="Release"
output="dist"
make_zip=0

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/source/Shoko.Plugin.Tvdb.csproj"

die() { echo "pack: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        -c|--configuration) configuration="${2:-}"; shift 2 ;;
        -o|--output) output="${2:-}"; shift 2 ;;
        -z|--zip) make_zip=1; shift ;;
        -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

[ -f "$project" ] || die "cannot find $project"

case "$output" in
    /*) target="$output" ;;
    *) target="$root/$output" ;;
esac

rm -rf "$target"
mkdir -p "$target"

dotnet publish "$project" -c "$configuration" -o "$target" --nologo

# The abstractions are supplied by the server at runtime. A plugin that
# ships its own copy resolves IPlugin against that copy, and the server
# then skips it without an error, so any copy that made it into the
# output is removed rather than left to fail quietly at load time.
rm -f "$target/Shoko.Abstractions.dll" "$target/Shoko.Abstractions.xml"

cp "$root/manifest.json" "$target/manifest.json"

echo "pack: wrote $target"

if [ "$make_zip" -eq 1 ]; then
    version="$(grep -oPm1 '(?<=<Version>)[^<]+' "$project" || echo "0.0.0")"
    archive="$root/Shoko.Plugin.Tvdb-$version.zip"
    rm -f "$archive"
    (cd "$target" && zip -qr "$archive" .)
    echo "pack: wrote $archive"
fi
