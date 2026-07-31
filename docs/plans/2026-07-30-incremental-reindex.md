# vela: incremental reindex

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Reindex only what changed, so the common case after an edit costs seconds rather than the 2m12s a full rebuild of a 375,608-line solution takes.

**The reason this is dangerous, and the reason it is opt-in to begin with.** A full rebuild cannot be stale, because it reads everything. An incremental rebuild is a claim that what it skipped has not changed, and if that claim is wrong the index holds rows describing code that no longer exists, at line numbers that have moved, while reporting itself complete. That is Constraint 3's exact failure, and it is worse than the slowness it replaces. So the first version is `vela index --incremental`, off by default, and it degrades rather than guesses whenever it cannot prove a project is untouched.

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework `net10.0`.** Do not change it or any package version, in particular the three `Microsoft.Build*` 17.11.48 entries with `ExcludeAssets="runtime" PrivateAssets="all"`.
- **Deterministic only.** The same tree and the same prior index must produce the same decision about what to rebuild, every time. No sampling, no heuristics, no timestamps of convenience.
- **Never write to the indexed repository.**
- **An incomplete index must never look like a complete one.** Anything the incremental path cannot prove, it rebuilds. Anything it cannot rebuild, it degrades and says so.
- **The load-bearing property is Razor and Blazor coverage.** The webapp fixture must keep yielding exactly 7 `.cshtml` documents and 23 total, with Razor occurrences non-zero. Never adjust a coverage test.
- **`src/Vela/Query/*` must stay byte-identical.** The query layer was hardened against a real solution at considerable cost and this feature has no business touching it.
- **House style: British English, plain hyphens.** No em dashes or en dashes.
- 293 tests pass before this plan. No existing assertion may be weakened or deleted.

## The unit of work is a project, and the hard part is the closure

Roslyn cannot give a semantic model without a compilation, and a compilation is per project. So the unit is a project, not a file: if anything a project compiles has changed, that project is rebuilt whole.

The trap is that a project is not independent. Change a public member in `ScentVerdict.Data` and every reference to it in `ScentVerdict.Web` moves, even though no file in `Web` was touched. So the set to rebuild is the changed projects **plus everything downstream of them in the project reference graph**, transitively. Getting that closure wrong is the silent-staleness failure.

On the real solution the graph is ten projects, and `ScentVerdict.Data` is upstream of almost all of them, so a change there will rebuild almost everything. That is correct, and it is worth saying plainly in the documentation: incremental helps most when you edit a leaf.

---

### Task 1: Record what each project was built from

Nothing today records which files fed which project, so there is nothing to compare against. This task adds the ledger and changes no behaviour.

**Files:** `src/Vela/Indexing/Schema.cs`, `src/Vela/Indexing/ProjectFingerprint.cs` (new), `src/Vela/Harvest/ScipEmitter.cs`, tests.

- [ ] **Step 1: Write the failing tests.** A fingerprint for a project is stable across two runs over an unchanged tree, and differs when any input changes: a source file's content, a file added, a file removed, the project file itself, or a `Directory.Build.props` above it. Use content hashes, not mtimes: an mtime changes when nothing did (a checkout, a touch) and does not change when something did (a restored file with a preserved timestamp).
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** A `project_input` table keyed by project, holding the project's identity, the set of documents it compiled with a hash each, and its project references. Bump the schema. Record it during the harvest, which already walks exactly these documents.
- [ ] **Step 4: Verify on the real solution.** Index ScentVerdict, confirm ten projects fingerprinted, confirm a second index produces identical fingerprints, and confirm the full-index timing has not regressed by more than a few seconds. Report the cost of fingerprinting.
- [ ] **Step 5: Commit** `feat: record what each project was built from`

---

### Task 2: Decide what to rebuild, and prove the closure

The decision, on its own, with no rebuilding. It is the part that can be wrong silently, so it gets its own task and its own tests.

**Files:** `src/Vela/Indexing/RebuildPlan.cs` (new), tests.

- [ ] **Step 1: Write the failing tests.** Given a set of prior fingerprints and a current tree, the plan must select: every project whose own inputs changed; every project transitively downstream of one of those; every project with no prior fingerprint; and every project when the schema version, the vela version, or the project set itself has changed. It must select nothing when nothing changed.
  Test the closure hard, because it is the failure mode: a diamond graph, a chain three deep, a cycle if the solution somehow has one, and a project that is upstream of nothing.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Pure, deterministic, no I/O beyond reading hashes. Given the same inputs it must return the same set in the same order.
- [ ] **Step 4: Prove it against the real graph.** On ScentVerdict, report the plan for: no change; a change to a leaf project; a change to `ScentVerdict.Data`. State how many of the ten projects each rebuilds. If a `Data` change rebuilds nearly everything, say so rather than presenting incremental as a universal win.
- [ ] **Step 5: Commit** `feat: work out which projects a change actually invalidates`

---

### Task 3: Rebuild only those projects

**Files:** `src/Vela/Program.cs`, `src/Vela/Indexing/ScipLoader.cs`, tests.

- [ ] **Step 1: Write the failing tests.** After an incremental reindex: rows for rebuilt projects are replaced, rows for skipped projects are untouched and still answer, orphaned FTS rows do not survive, and the health record says which projects were rebuilt and which were reused. Imported `.scip` languages must survive, since the replay machinery already exists and must keep working.
  **The test that matters most:** index, edit a file in an upstream project, incrementally reindex, and assert a reference in a DOWNSTREAM project reflects the edit. That is the silent-staleness case, and if the closure is wrong this is what catches it.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement `vela index --incremental`.** Delete and reinsert per project inside one transaction. Off by default. Fall back to a full rebuild, saying so plainly, whenever the plan cannot be trusted: no prior fingerprints, a schema change, a changed project set, or any error working out the plan. A fallback is a good outcome and must never be silent.
- [ ] **Step 4: Measure it on the real solution.** Report: full index; incremental with nothing changed; incremental after a one-line edit to a leaf project; incremental after a one-line edit to `ScentVerdict.Data`. Confirm 307 of 307 Razor views after each, and confirm `refs Perfume.Status` 24, `refs ILogger` 563 and `refs Count` 2,573 are unchanged.
- [ ] **Step 5: Commit** `feat: vela index --incremental`

---

### Task 4: Documentation

- [ ] Update `docs/reference.md` with the flag, what it rebuilds, when it falls back, and the measured figures.
- [ ] Add a section to `docs/guides/ci.md`, since keeping an index fresh is where this pays off.
- [ ] Update `docs/architecture.md` with the fingerprint and plan, and `docs/design-notes.md`, which currently records incremental reindex as deferred.
- [ ] Update `skills/vela/SKILL.md` so an agent knows the flag exists, that it is opt-in, and that a full index is the safe choice when in doubt.
- [ ] Be honest in all of it: incremental helps most when you edit a leaf, and a change low in the dependency graph rebuilds nearly everything.
- [ ] **Commit** `docs: incremental reindex, and when it does not help`

---

## Out of scope

- Making `--incremental` the default. That needs this version proven in use first.
- Per-document or per-symbol granularity. Roslyn's unit is a compilation; going finer means modelling what a change can affect inside a project, which is a much larger claim to have to defend.
- A file watcher or a daemon. Nothing stays resident between queries; that is the architecture.
- Incremental import of `.scip` files. A foreign index is opaque and arrives whole.
