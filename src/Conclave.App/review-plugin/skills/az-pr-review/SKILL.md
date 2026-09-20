---
name: az-pr-review
description: >-
  Review an Azure DevOps pull request end-to-end from just its PR ID: fetch the diff,
  review it for correctness AND non-functional concerns (security, performance, cost,
  reliability) against the project's own conventions, post the findings as a comment thread,
  and cast a vote. Works across project types — React/TypeScript and Go are the primary
  lenses, with a general fallback for others. Use this whenever the user wants to review,
  critique, give feedback on, or "do a review of" an Azure DevOps / Azure Repos PR —
  phrasings like "review PR 1324", "review this pull request", "帮我 review 这个 PR",
  "对 PR 1324 做一次 code review", or when they hand you a PR number in an az context. Not for
  GitHub PRs (use gh/`/review` there) and not for reviewing your own uncommitted diff.
---

# Azure DevOps PR Review

Review a PR given only its ID, then record the review on the PR via the `az` CLI.

## Ground rule: you review as yourself

Every `az` call acts as the **authenticated user** (`az devops login`). This skill never posts
or votes as another person — a review is only meaningful if it's honestly attributed. If a
teammate wants a review *from them*, they run this skill under their own login. Never fabricate
another reviewer's name, and never cast a vote meant to look like someone else approved — that
subverts the review gate the team relies on.

## Inputs

- **PR ID** (required) — e.g. `1324`. Usually the only thing the user gives you.
- **Dry run?** — if the user says "just show me" / "don't post" / "dry run", do everything
  through the drafting step but **skip posting and voting**; show the drafted comment and
  proposed vote instead. Offer a dry run on a first use.
- **`REVIEW_MODE` env var** — when it is `collect`, you are one of several nodes independently
  reviewing this PR. Behave as a dry run **and** emit the machine-readable block in step 7.
  Check it once at the start: `echo "${REVIEW_MODE:-post}"`.

Helper: `scripts/az_pr.sh` (needs `az`, `jq`, `git`, and the PR's repo checked out locally).

## Workflow

### 1. Gather context

```bash
bash <skill-dir>/scripts/az_pr.sh context <pr-id>
```

Prints JSON (`id`, `title`, `author`, `source`, `target`, `repo`, `description`, `diffFile`,
`isDraft`, `status`) and lists changed files on stderr. Read `diffFile` — that diff is what you
review. If `isDraft` is true, note it but still review.

### 2. Establish the review lens (project-aware)

Don't apply generic advice blindly — calibrate to *this* repo:

1. **Read the repo's own rules** if present: `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`,
   `.cursor/rules`, linter configs. Project-stated conventions **override** anything below.
2. **Detect the language(s)** from the changed files' extensions and read the matching guide:
   - `.ts .tsx .js .jsx` (or `package.json`) → read `references/react-ts.md`
   - `.go` (or `go.mod`) → read `references/go.md`
   - Anything else → apply the general principles here + the repo's own rules; reason from
     first principles about that language's common footguns.
   - Mixed PR → read every matching guide.

### 3. Review — functional AND non-functional

Read the whole diff. For each concern, assign a severity (step 5) and be specific: cite
`file:line`, describe the failure, don't just name a rule.

**Functional**
- **Correctness** — logic errors, off-by-one, nil/undefined, wrong async/concurrency, state that
  won't update, effects/deps, edge cases and error paths. Language guides have the specifics.
- **Conventions** — whatever the repo's rules mandate. Report violations concretely.
- **Tests** — is new/changed logic covered? Flag risky changes with no tests.
- **Maintainability** — duplication, dead code, naming, needless complexity (non-blocking).

**Non-functional** — always consider these; they cause the expensive production incidents:
- **Security** 🔐 — untrusted input reaching a sink (SQL/command/path/template injection, XSS,
  SSRF); authn/authz gaps or missing ownership checks; secrets in code or logs; sensitive data
  in responses/logs; unsafe deserialization; missing input validation; new deps with known CVEs;
  missing rate limiting on new endpoints.
- **Performance** ⚡ — N+1 queries; missing indexes/pagination; O(n²) or worse on hot paths;
  blocking I/O in a request path or render; unbounded concurrency; large payloads;
  re-renders / re-computation that could be memoized; missing caching where it clearly helps.
- **Cost** 💰 — repeated calls to paid/cloud APIs (per-item in a loop), full-table scans or
  large egress, chatty network round-trips, polling where webhooks/streaming fit, retries
  without backoff amplifying load, LLM token bloat, storage/log growth from verbose writes.
- **Reliability & observability** 🛡️ — missing timeouts/retries/idempotency, swallowed errors,
  resource leaks (unclosed handles/connections), no logging/metrics on important new paths.

Judgement over volume: only raise a non-functional point when the diff plausibly triggers it —
say *why* (the input, the scale, the call site). Don't pad the review with hypotheticals.

**Run the project's own checks** when cheap and available — it makes the review credible. Detect
from the repo: `package.json` scripts (`npm run lint`/`test`, `tsc --noEmit`), a `Makefile`
(`make lint test`), Go (`go build ./... && go vet ./... && go test ./...`). Report what you ran
and the result; if you couldn't run them, say so rather than implying they passed.

### 4. Draft the comment

Write the review to a markdown file. Skimmable and honest — praise what's right, then findings
grouped by severity with `file:line` and (where useful) a security/perf/cost tag. Shape:

```markdown
**Code Review** — <one-line verdict>

Verified / 已核对:
- <what you checked and found OK, incl. any lint/build/test results>

Findings / 问题:
- 🔴 [blocking] <file:line> — <what's wrong and why>
- 🟡 [should-fix] <file:line> · [security|perf|cost] — <issue> · 建议 <fix>
- 🔵 [minor] <...>

<If nothing blocking:> 无阻塞项。<what's left for the author / reviewer>
```

Match the PR's language (mirror the PR/team; e.g. Chinese if the PR is in Chinese). Omit empty
severity groups.

### 5. Decide the vote (fully automatic)

| Worst finding | Vote | Auto-cast? |
|---|---|---|
| 🔴 blocking bug / security hole / broken build | `reject` | yes |
| 🟡 should-fix before merge (needs author action) | `wait-for-author` | yes |
| 🔵 only minor/optional | `approve-with-suggestions` | yes |
| clean — nothing to flag | `approve` | yes |

No 🔴/🟡 findings ⇒ approve automatically (`approve-with-suggestions` when minor notes exist,
plain `approve` when clean). Approve only on evidence actually reviewed: if the diff couldn't be
fully read or the project's checks couldn't run, say so in the comment and **skip the vote**
rather than casting a hollow approve. Dry run still skips posting and voting entirely.

### 6. Post it

**Skip this entire step when `REVIEW_MODE=collect`, or on a dry run.** Go to step 7 instead.

```bash
bash <skill-dir>/scripts/az_pr.sh comment <pr-id> <comment.md>   # always (unless dry run / collect)
bash <skill-dir>/scripts/az_pr.sh vote    <pr-id> <approve|approve-with-suggestions|wait-for-author|reject>  # per the table
```

Report back: the verdict, the thread ID, whether a vote was cast (recorded under *their*
identity), and any manual follow-up.

### 7. Collect mode — machine-readable output

Only when `REVIEW_MODE=collect`. You are one of N nodes reviewing this PR independently; a
coordinator merges the nodes' findings and **posts once**. If you posted or voted here, the PR
would get N duplicate comments and N votes — that's why step 6 is skipped.

Draft the comment exactly as you would in step 6 — you are not writing a summary for a machine,
you are writing the comment that will appear on the pull request. Then **end your final reply with
exactly one ```json fence** holding it verbatim in `comment`, plus the structured fields:

```json
{
  "decision": "reject",
  "comment": "## 评审结论：需要修改\n\n### 退款金额未校验上限\n`src/Api/ChargeController.cs:142`\n\n`amount` 直接取请求体……",
  "findings": [
    {
      "file": "src/Api/ChargeController.cs",
      "line": 142,
      "severity": "critical",
      "title": "退款金额未校验上限，可超额退款",
      "detail": "`amount` 直接取请求体，没有和原订单金额比对。构造 amount 大于订单金额的请求可以退出超过实付的钱。建议在 service 层加 `amount <= order.PaidAmount` 校验。"
    }
  ]
}
```

Field rules:

| Field | Values |
|---|---|
| `decision` | `approve` · `approve-with-suggestions` · `wait-for-author` · `reject` — same table as step 5 |
| `comment` | **the comment you drafted, verbatim** — the exact markdown you would have posted in step 6 |
| `severity` | `critical` (🔴 blocking) · `major` (🟡 should-fix) · `minor` (🔵) · `info` |
| `file` | repo-relative path, as it appears in the diff |
| `line` | line number in the **new** file; use the hunk's first changed line if a finding spans several |
| `title` | one line, what's wrong |
| `detail` | why it's wrong and the suggested fix; markdown is fine |

- `comment` is **posted to the pull request exactly as given** — the coordinator does not reformat
  it, add a header, or wrap it. Write it as the final comment, not as a summary of one. Include the
  reasoning, the suggested fixes and what you checked and found clean; that is the part `findings`
  cannot carry. Never omit the key — an empty or missing `comment` makes the coordinator fall back
  to a bare table rendered from `findings`, which loses everything except the titles.
- `findings` must be `[]` when there is nothing to report — never omit the key.
  It still matters when `comment` is present: findings feed the ledger, the projections and the UI,
  while `comment` is what a human reads on the PR. They are two outputs, not two copies.
- The fence must be the **last** fenced block in your reply, and valid JSON on its own
  (no comments, no trailing commas).
- Match the PR's language in `title`/`detail`, same as the comment.
- If the diff couldn't be fully read or the checks couldn't run, say so in `detail` and use
  `wait-for-author` rather than a hollow `approve`.

The coordinator groups findings by `(file, line/10)` across nodes and ranks them by how many
nodes reported the same one — so **be precise about `file` and `line`**: a wrong line number
splits one real finding into two low-confidence ones.

## Failure modes

- **`cannot read PR` / auth errors** → run `az devops login` (or set `AZURE_DEVOPS_EXT_PAT`), retry.
- **`git fetch failed`** → likely not in the PR's repo; confirm the working dir is a clone of `repo`.
- **On-prem Azure DevOps Server** → `az repos pr set-vote` prints a "does not support Server"
  warning but still works; the comment path uses `az devops invoke` and is unaffected.
- **`REVIEW_MODE=collect` but you can't produce the JSON block** (e.g. the diff was unreadable)
  → still emit the fence with `"decision": "wait-for-author"` and a `findings` entry explaining
  why. A missing fence makes the coordinator record an error ballot with no verdict at all.
