---
name: trail
description: >-
  Use for ANY task in this repo. What past sessions worked out, decided and
  ruled out is in .trail/learnings/, so read the learnings on your task before
  exploring. Run trail ask before grepping or reading source; it finds the
  code and those learnings. When the session finds what the code can't tell
  the next person, like a bug's cause after failed attempts, an approach
  dropped, a choice and why, or the user correcting how something is done
  here, save a learning with trail note and commit it with the change. Run
  trail check before you commit, push or open a PR.
---

# Trail

This repo is indexed by Trail. For ANY task here, whether understanding how something works, finding where code lives, tracing what calls a symbol or what a change breaks, or scoping an edit, get your context from Trail before grepping or reading source files.

Every command below is $0, needs no API key, and returns in under a second.

## When to reach for trail
- **Before exploring code**, run `trail ask "<task>" --source`. Learnings from past sessions, yours and your teammates', on what was decided, tried and ruled out print above the code. Read them first.
- **When the session finds what the code can't tell the next person**, save a learning with `trail note`, and commit it with the change.
- **When the work is ready for review**, run `trail check` before you commit it, push the branch or open a PR.
- **When the user corrects how something is done here**, save that as a learning too, so nobody has to say it again.

## The tools

### 1 · `trail ask "<question>" --source`: locate + understand (the default)
Ranked retrieval over the graph, routed automatically between prose nodes and the wiring graph, returning the top hits with exact `file:line`.
- `--source` inlines the code at each hit, the ≤8-line **crux** of each definition, so the result IS the code you need, no follow-up file read. Add `--full` only when the crux is too small to act on.
- `--in <path>` narrows to a subtree before ranking; `-n N` caps results (default 8).
- **Use it when** the question is conceptual or locational: "how does auth work", "where is rate-limiting handled", "what assembles the request pipeline".

### 2 · `trail grep "<pattern>"`: exhaustive find
Regex (or `--fixed` for a literal) over every indexed file, hits **grouped by enclosing symbol** and ranked by coupling.
- **Use it when** you need every occurrence: all call sites, all uses of a constant, all providers.
- `-i` case-insensitive; `--in <path>` scopes to a subtree.

### 3 · `trail skeleton <file>`: a file's API at a glance
Signatures-only view of one file (every function / method / type with its span) in ~200 tokens, ~10x cheaper than reading the file.

### 4 · `trail callers <symbol>`: the exact edges
Precomputed call/reference edges, not a text search. Symbol can be bare (`Foo`), qualified (`Class.method`), or package-qualified (`pkg.Fn`).
- default `--direction in`: **who calls/references** this; run before you rename, delete, or change its signature.
- `--direction out`: **what this symbol itself calls/depends on**.
- `--depth N`: walk transitively N hops for the **full blast radius**; `--depth 2` is the usual "what breaks if I touch this".
- `--depth all`: the **entire connected closure** — every source reachable through the edges.

### 5 · `trail map`: orientation for an unfamiliar repo or area
A token-budgeted tour: directory clusters, per-directory hubs, and global hotspots, straight from the wiring graph.

### 6 · `trail note`: save a learning
Save a fact, decision, or user correction:
```bash
trail note --title "<the fact, not the task>"
```
Input on stdin under `## Decided`, `## Tried and ruled out`, and `## Watch out`.

### 7 · Lifecycle: `trail build` / `trail check`
Refreshes the graph. Run `trail build` after large modifications. Run `trail check` before committing.

## Scenarios
| When you're… | Reach for | Calls |
|---|---|---|
| Onboarding / "explain this codebase" | `trail map`, then read the named hub cards | 1 |
| Understanding a flow ("how does X work") | `trail ask "<flow>" --source` | 1 |
| Finding where a change belongs | `trail ask "where is <behavior>" --source` | 1 |
| Editing a symbol you can already name | `trail grep "<symbol>"`, edit at `file:line` | 1 |
| Renaming / deleting / changing signature | `trail callers <sym> --depth 2` | 1 |
| Refactor / multi-file change | `trail callers <sym> --depth all` | 1 |
| What does this depend on? | `trail callers <sym> --direction out` | 1 |
| Exhaustive pattern search | `trail grep "<literal>"` | 1 |
| API of a file | `trail skeleton <file>` | 1 |
| Pre-commit check against learnings | `trail check` | 1 |

## Report what trail saved
Each retrieval tool opens its output with `[trail] tokens saved ≈ N`.
Whenever you used any trail tool in a turn, close your reply with a one-line tally summing those numbers, e.g.:
`🌱 trail saved ~12,400 tokens this turn (3 calls)`
