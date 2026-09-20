#!/usr/bin/env bash
# Helper for the az-pr-review skill: wraps the fiddly Azure DevOps CLI plumbing so
# the review workflow stays readable. Every command acts as the *authenticated user*
# (whoever ran `az devops login`) — it never impersonates anyone else.
#
# Subcommands:
#   context <pr-id>            Fetch PR metadata + branches, write the diff to a temp file.
#   comment <pr-id> <md-file>  Post <md-file> as a review comment thread on the PR.
#   vote    <pr-id> <vote>     Cast a vote (approve|approve-with-suggestions|wait-for-author|reject|reset).
#
# Requires: az (with azure-devops extension), jq, git. Org/project default to the
# `az devops configure` settings; repo + branches are read from the PR itself.
set -euo pipefail

API_VERSION="${AZ_PR_API_VERSION:-7.1}"

die() { echo "az_pr: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "missing dependency: $1"; }
need az; need jq; need git

pr_show() { az repos pr show --id "$1" -o json 2>/dev/null || die "cannot read PR $1 — run 'az devops login' and check the id"; }

cmd="${1:-}"; shift || true
case "$cmd" in
  context)
    pr_id="${1:?usage: az_pr.sh context <pr-id>}"
    pr_json="$(pr_show "$pr_id")"
    src="$(jq -r '.sourceRefName' <<<"$pr_json" | sed 's#^refs/heads/##')"
    tgt="$(jq -r '.targetRefName' <<<"$pr_json" | sed 's#^refs/heads/##')"
    git fetch --quiet origin "$src" "$tgt" || die "git fetch failed for $src / $tgt (wrong repo checked out?)"
    diff_file="$(mktemp -t "az-pr-${pr_id}-diff.XXXXXX")"
    git diff "origin/${tgt}...origin/${src}" >"$diff_file"
    # Machine-readable summary the skill parses.
    jq -n --argjson pr "$pr_json" --arg diff "$diff_file" '{
      id: $pr.pullRequestId, title: $pr.title, isDraft: $pr.isDraft, status: $pr.status,
      repo: $pr.repository.name, project: $pr.repository.project.name,
      source: ($pr.sourceRefName|sub("^refs/heads/";"")), target: ($pr.targetRefName|sub("^refs/heads/";"")),
      author: $pr.createdBy.displayName, description: $pr.description, diffFile: $diff
    }'
    echo "--- changed files ---" >&2
    git diff --name-only "origin/${tgt}...origin/${src}" >&2
    ;;
  comment)
    pr_id="${1:?usage: az_pr.sh comment <pr-id> <markdown-file>}"
    md_file="${2:?usage: az_pr.sh comment <pr-id> <markdown-file>}"
    [ -f "$md_file" ] || die "comment file not found: $md_file"
    pr_json="$(pr_show "$pr_id")"
    repo="$(jq -r '.repository.name' <<<"$pr_json")"
    proj="$(jq -r '.repository.project.name' <<<"$pr_json")"
    thread_file="$(mktemp -t "az-pr-${pr_id}-thread.XXXXXX")"
    jq -Rs '{comments:[{parentCommentId:0,commentType:"text",content:.}],status:"active"}' "$md_file" >"$thread_file"
    az devops invoke --area git --resource pullRequestThreads \
      --route-parameters project="$proj" repositoryId="$repo" pullRequestId="$pr_id" \
      --http-method POST --in-file "$thread_file" --api-version "$API_VERSION" -o json \
      | jq -r '"posted comment thread id=\(.id)"'
    ;;
  vote)
    pr_id="${1:?usage: az_pr.sh vote <pr-id> <vote>}"
    vote="${2:?usage: az_pr.sh vote <pr-id> <approve|approve-with-suggestions|wait-for-author|reject|reset>}"
    az repos pr set-vote --id "$pr_id" --vote "$vote" -o json \
      | jq -r '"vote \"" + (.vote|tostring) + "\" recorded as " + (.displayName // "you")'
    ;;
  *)
    die "unknown command: '$cmd' — use context | comment | vote"
    ;;
esac
