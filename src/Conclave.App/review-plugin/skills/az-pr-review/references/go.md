# Go review guide

Read this when a PR touches `.go` or `go.mod`. These are the recurring Go footguns; combine
them with whatever the repo's conventions mandate (which wins).

## Errors

- **Ignored errors** — `_ = f()` or an unchecked return where the error matters; every error path
  should be handled or deliberately (and visibly) dropped.
- **Lost context** — returning a bare `err` deep in a call stack; wrap with `fmt.Errorf("...: %w", err)`
  so it's diagnosable and still `errors.Is`/`As`-able. Don't `%v` an error you need to unwrap.
- **panic in library code** — panics for ordinary failures instead of returning an error;
  goroutines that can panic without `recover` crash the process.

## Concurrency

- **Data races** — shared state written from multiple goroutines without a mutex/channel;
  loop-variable captured by a goroutine (pre-1.22 semantics / closures). Suggest `go test -race`.
- **Goroutine leaks** — a goroutine blocked forever on a channel/`select` with no exit; goroutines
  spawned per request without bound (unbounded fan-out → memory/CPU blowup — a cost & perf risk).
- **Context** — `context.Context` should be the first param and be propagated, not `context.TODO()`
  on a real call path; long operations must honor cancellation/timeouts; don't store Context in structs.

## Resources & correctness

- **Missing `defer` cleanup** — `rows.Close()`, `resp.Body.Close()`, file/conn `Close()`, `Unlock()`
  not deferred → leaks. Check every `Open`/`Query`/`Do` has a matching close.
- **nil pointers / nil maps** — writing to a nil map; dereferencing a pointer that an error path
  left nil; type assertions without the `, ok` form.
- **Slices** — `append` aliasing a shared backing array; retaining a huge slice via a subslice;
  modifying a slice passed by value expecting the caller to see it.
- **defer in loops** — deferring `Close` inside a loop stacks them until the function returns
  (resource pileup); close per-iteration instead.

## Security

- **SQL/command injection** — string-built SQL instead of parameterized queries; `exec.Command`
  with a shell string from user input; path traversal from unvalidated file names.
- **Secrets & data** — credentials in code/logs; sensitive fields logged or returned; missing
  authz checks on handlers.
- **HTTP** — no timeouts on `http.Client`/servers (resource exhaustion); SSRF from user-supplied URLs.

## Performance & cost

- **N+1 queries** and per-item calls to a DB or paid API inside a loop — batch them.
- **Allocations in hot paths** — needless slice/map growth, `[]byte`↔`string` churn; preallocate
  with capacity where the size is known; reuse buffers where it clearly matters.
- **Unbounded work** — reading an entire large result/file into memory; missing pagination;
  fan-out without a worker-pool cap.
- **Repeated expensive calls** — recomputing/refetching what could be cached for the request/process.
