# React / TypeScript review guide

Read this when a PR touches `.ts/.tsx/.js/.jsx` or `package.json`. These are the recurring
footguns; combine them with whatever the repo's `CLAUDE.md`/lint config mandates (which wins).

## TypeScript

- **Escape hatches** — `as any`, `@ts-ignore`, `@ts-expect-error`, non-null `!` on values that
  can genuinely be null. Each hides a real type gap; ask for a proper type or a guard.
- **Unsound casts** — `as SomeType` on data from the network/`JSON.parse`; validate (zod/io-ts)
  at the boundary instead of asserting.
- **Loose contracts** — `any`/`object`/overly-wide unions where a precise type exists; optional
  fields that should be required (or vice versa).

## React

- **Hook dependencies** — `useEffect`/`useMemo`/`useCallback` dep arrays that omit referenced
  values (stale closures) or include unstable ones (effect fires every render). Objects/arrays/
  functions recreated each render passed as deps or props defeat memoization.
- **Keys** — list `key` must be a stable id, not the array index, when items can reorder/insert.
- **Effect hygiene** — subscriptions/timers/listeners without cleanup; `fetch` without an
  `AbortController`; effects that setState in a loop; data fetching that races on fast prop changes.
- **Render cost** — expensive work in render without `useMemo`; big lists without virtualization;
  new inline objects/handlers forcing child re-renders in hot paths.
- **State correctness** — derived state duplicated into `useState` (should be computed);
  setState based on previous state not using the updater form; missing loading/error states.
- **Security** — `dangerouslySetInnerHTML` with unsanitized input (XSS); building URLs/HTML from
  user input; secrets or tokens in client-side code or `NEXT_PUBLIC_*` env vars.
- **Accessibility** — interactive `<div>`/`<span>` without role/keyboard handlers; missing
  labels/alt; native `<select>`/inputs where the repo mandates a shared component.

## Project-shape conventions (verify against the repo's own docs)

Many React codebases standardize these — check the repo's rules and flag deviations:
- A **service/API layer** instead of raw `fetch` in components; a shared fetch wrapper for
  auth/refresh; typed request/response.
- **No server actions / no specific data libs** if the repo says so; forms via the sanctioned
  stack (e.g. RHF + zod).
- **i18n key alignment** — when translation keys are added, every locale file must stay aligned;
  a PR that adds a key to one file but not the others is a bug.
- Shared UI primitives (design-system components) over ad-hoc markup.

## Performance / cost signals specific to frontend

- Large dependencies added for a small need (bundle bloat) — check `package.json` diffs.
- Waterfalls of dependent requests that could be parallelized or batched.
- Re-fetching on every render/keystroke without debounce; polling where a subscription fits.
