#!/usr/bin/env bash
# Upload files as assets on a Gitea release. The replacement for
# `gh release upload`, which does not exist on a Gitea runner.
#
#   gitea-release-asset.sh --tag v1.0.4 [--dry-run] FILE...
#
#   --tag TAG          the release tag. No default; guessing one is how
#                      the wrong release gets an asset
#   --release-id N     skip the tag lookup
#   --repo OWNER/NAME  default $GITHUB_REPOSITORY
#   --server URL       default $GITHUB_SERVER_URL
#   --token TOKEN      default $GITEA_TOKEN
#   --replace          delete an existing asset of the same name first
#   --dry-run          print what would be sent, touch no network
#
# A same-name asset is refused rather than left to the server: the
# manifest promises the download URL, so an asset silently stored under
# a uniquified name would break an install rather than a job.
set -euo pipefail

tag=""
release_id=""
repo="${GITHUB_REPOSITORY:-}"
server="${GITHUB_SERVER_URL:-}"
token="${GITEA_TOKEN:-}"
replace=0
dry_run=0
files=()

die() { echo "gitea-release-asset: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --tag) tag="${2:-}"; shift 2 ;;
        --release-id) release_id="${2:-}"; shift 2 ;;
        --repo) repo="${2:-}"; shift 2 ;;
        --server) server="${2:-}"; shift 2 ;;
        --token) token="${2:-}"; shift 2 ;;
        --replace) replace=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        -h|--help) awk 'NR>1 { if (!/^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit 0 ;;
        --) shift; files+=("$@"); break ;;
        -*) die "unknown option: $1" ;;
        *) files+=("$1"); shift ;;
    esac
done

[ "${#files[@]}" -gt 0 ] || die "no files given"
[ -n "$repo" ] || die "no repository: pass --repo OWNER/NAME or set GITHUB_REPOSITORY"
[ -n "$server" ] || die "no server: pass --server URL or set GITHUB_SERVER_URL"
[ -n "$tag" ] || [ -n "$release_id" ] || die "no release: pass --tag TAG or --release-id N"

# The tag is a path segment in `GET /releases/tags/{tag}` and in every
# asset URL, so a '/' in it does not mean what it looks like.
case "$tag" in
    */*) die "tag ${tag} contains a '/', which is a path separator in the release API and in every asset URL" ;;
esac

for f in "${files[@]}"; do
    [ -f "$f" ] || die "not a file: $f"
done

[ "$dry_run" -eq 1 ] || [ -n "$token" ] || die "no token: pass --token or set GITEA_TOKEN"

api="${server%/}/api/v1/repos/${repo}"

gitea() {
    local method="$1" url="$2"; shift 2
    # --fail-with-body keeps the server's error text, which is the
    # difference between "422" and "an asset with this name exists".
    curl --silent --show-error --fail-with-body \
        --request "$method" \
        --header "Authorization: token ${token}" \
        --header "Accept: application/json" \
        "$@" \
        "$url"
}

if [ "$dry_run" -eq 1 ]; then
    echo "DRY RUN - no request is made"
    echo "  server:   ${server%/}"
    echo "  repo:     ${repo}"
    if [ -n "$release_id" ]; then
        echo "  release:  id ${release_id} (given)"
    else
        echo "  release:  GET ${api}/releases/tags/${tag} -> .id"
    fi
    # Never the value: a dry run is something a human pastes.
    echo "  token:    $([ -n "$token" ] && echo "set" || echo "MISSING (would refuse)")"
    echo "  on clash: $([ "$replace" -eq 1 ] && echo "delete then re-upload" || echo "refuse")"
    for f in "${files[@]}"; do
        name="$(basename "$f")"
        echo "  upload:   POST ${api}/releases/{id}/assets?name=${name}"
        echo "            attachment=@${f} ($(stat -c %s "$f") bytes)"
        # Where the asset lands on the origin, which is not the URL the
        # manifest promises: that one names the CDN in front of it.
        echo "            -> ${server%/}/${repo}/releases/download/${tag}/${name} (on the origin)"
    done
    exit 0
fi

if [ -z "$release_id" ]; then
    # No fallback to creating the release. This fires from a published
    # release, so a tag with no release means the trigger and the tag
    # disagree.
    release_id="$(gitea GET "${api}/releases/tags/${tag}" | jq -er '.id')" \
        || die "no release for tag ${tag} (or the token cannot read it)"
    echo "release ${tag} -> id ${release_id}"
fi

existing="$(gitea GET "${api}/releases/${release_id}/assets")" \
    || die "cannot list assets of release ${release_id}"

for f in "${files[@]}"; do
    name="$(basename "$f")"
    clash="$(printf '%s' "$existing" | jq -r --arg name "$name" 'map(select(.name == $name)) | .[0].id // empty')"

    if [ -n "$clash" ]; then
        [ "$replace" -eq 1 ] || die "release ${tag:-$release_id} already has an asset named ${name}; pass --replace to overwrite"
        echo "replacing ${name} (asset ${clash})"
        gitea DELETE "${api}/releases/${release_id}/assets/${clash}" >/dev/null \
            || die "cannot delete existing asset ${name}"
    fi

    echo "uploading ${name} ($(stat -c %s "$f") bytes)"
    gitea POST "${api}/releases/${release_id}/assets?name=${name}" --form "attachment=@${f}" \
        | jq -r '"  -> " + (.browser_download_url // "(no browser_download_url in the response)")' \
        || die "upload of ${name} failed"
done
