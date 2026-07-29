# vela: fixes from the ScentVerdict validation run

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Fix the three defects the first real-world index exposed, so that indexing a normal .NET repository does not permanently mark itself degraded, a bare-name query cannot silently merge distinct symbols, and the latency claim matches reality.

**Evidence base:** vela indexed `/home/devops/scentverdict` on 2026-07-29: 2,512 documents, 307 of 307 Razor views, 935,029 occurrences, 136,814 definitions, 207 MB index, 2m12s at 1.5 GB peak. Constraint 2 held (0 dirty files). Precision matched the design notes (`Perfume.Status` 25 hits vs grep 1,430; `Perfume.Name` 245 vs 2,760).

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework `net10.0`.** Do not change it, and do not touch package versions, in particular the three `Microsoft.Build*` 17.11.48 entries with `ExcludeAssets="runtime" PrivateAssets="all"`.
- **Deterministic only.** No model calls, no network at index or query time, no telemetry, no heuristic ranking, no fuzzy matching.
- **Never write to the indexed repository.** Verified empirically on ScentVerdict; do not regress it.
- **An incomplete index must never look like a complete one.** This plan makes vela *less* noisy about degradation. Every change must reduce false alarms without ever suppressing a true one.
- **The load-bearing property is Razor and Blazor coverage.** The webapp fixture must keep yielding exactly 7 `.cshtml` documents and 23 documents total, and Razor occurrences must stay non-zero. A change that reduces `.cshtml` coverage is a stop-and-investigate, never a test adjustment.
- **House style: British English, plain hyphens.** No em dashes or en dashes anywhere.
- 89 tests pass before this plan. No existing assertion may be weakened or deleted.

---

### Task 1: Stop treating other people's files as gaps in yours

**The defect.** Indexing ScentVerdict produced exit code 3 on every single query, permanently, because of one file:

```
outside-project-root: /home/devops/.nuget/packages/microsoft.net.test.sdk/18.4.0/
                      build/net8.0/Microsoft.NET.Test.Sdk.Program.cs
```

The test SDK contributes a generated entry point that lives in the NuGet package cache. SCIP requires every document to sit under `project_root`, so vela correctly declines to emit it and records the omission. But recording it as *degradation* is wrong: nothing about the user's own code is missing. The result is a banner that fires on a stock .NET solution with no unusual layout, on every query forever, which teaches an agent to ignore the one signal Constraint 3 depends on.

There is a second, related defect already known and deferred: `project_root` is the solution directory, so the common `repo/src/App.sln` layout strands every file above `src/`.

**Files:**
- Modify: `src/Vela/Harvest/ScipEmitter.cs`, `src/Vela/Program.cs`
- Modify: `tests/Vela.Tests/ScipEmitterTests.cs`

- [ ] **Step 1: Write the failing tests**

Two behaviours, both new:

1. A document whose path lies outside the project root **and** inside a package cache or otherwise outside the repository is recorded as `external-document:` and does **not** set the health record degraded. Assert the emitted index records it and that `Program.BuildHealthRecord` returns `Degraded: false` for an index whose only problem entries are external documents.
2. A document that lies outside the project root but **inside** the repository is still recorded as `outside-project-root:` and **does** set degraded. This is a genuine coverage gap and must stay loud.

- [ ] **Step 2: Run the tests and see them fail**

- [ ] **Step 3: Widen project_root to the repository root**

Resolve `project_root` as the git repository root when the solution sits inside a working tree (walk up for a `.git` directory or file; a `.git` file means a worktree and still counts), falling back to the solution directory when there is no repository. This alone fixes the deferred `repo/src/App.sln` case, and shrinks how many documents can fall outside the root at all.

Do not shell out to `git`. Constraint 1 forbids nothing here, but a directory walk is deterministic, dependency-free and faster.

- [ ] **Step 4: Classify out-of-root documents**

Split the single `outside-project-root:` channel in two:

- `external-document: <path>` for a document outside the repository root, which includes anything under the NuGet package cache. Informational. Does not degrade.
- `outside-project-root: <path>` for a document inside the repository but outside `project_root`. Degrades, as now.

Detect the package cache from `NUGET_PACKAGES` if set, else `~/.nuget/packages`, and treat any path outside the resolved repository root as external regardless. Update `Program.BuildHealthRecord` so only the degrading prefixes set `Degraded`.

- [ ] **Step 5: Surface the informational count without crying wolf**

`vela index` should still say how many external documents were skipped, as a plain line, not a `!!` banner. `vela index --stats` should report the count. Neither may set exit code 3 on its own.

- [ ] **Step 6: Run the tests, then re-index ScentVerdict and confirm exit 0**

Run `dotnet test tests/Vela.Tests -v q`. Then, on `/home/devops/scentverdict`, `vela index` must exit 0 and a subsequent `vela refs Perfume.Status` must exit 0 with no INCOMPLETE banner. Quote both.

- [ ] **Step 7: Commit**

```bash
git commit -m "fix: a file outside the repository is not a gap in the repository"
```

---

### Task 2: A bare name must never silently merge distinct symbols

**The defect.** On ScentVerdict, `vela refs Perfume` returns 3,104 results, which is *more* than `grep -w Perfume` returns (1,897). Dotted-segment matching matches every symbol whose final segment is `Perfume`, so the answer silently merges at least four distinct symbols:

```
ScentVerdict.Data.Entities.Perfume
ScentVerdict.Data.Entities.Perfume.Perfume()          (constructor)
ScentVerdict.Data.Enums.EntityType.Perfume
ScentVerdict.ServiceModel.Api.FragranticaPerfumeDetailResponse.Perfume
```

Every hit is real. The *count* describes something that does not exist, and an agent sizing a change reads that count. `skills/vela/SKILL.md` previously claimed "vela will tell you when the name is ambiguous"; that claim was removed during the pre-merge fix wave because it was untrue. This task makes it true and restores it.

**Files:**
- Modify: `src/Vela/Query/OutputWriter.cs`, and the query verbs as needed
- Modify: `tests/Vela.Tests/QueryTests.cs`
- Modify: `skills/vela/SKILL.md`, `README.md`

- [ ] **Step 1: Write the failing tests**

Seed a database where one pattern matches occurrences of three distinct symbols across two files, and assert:
- the rendered output names each distinct symbol with its own count
- it states plainly that the pattern is ambiguous and that the total spans several symbols
- a pattern matching exactly one distinct symbol renders **no** ambiguity notice (no crying wolf)
- the per-symbol counts sum to the reported total

- [ ] **Step 2: Run the tests and see them fail**

- [ ] **Step 3: Implement ambiguity reporting**

In `OutputWriter.Render`, when the hits span more than one distinct symbol, print a block after the results listing each distinct symbol and its hit count, most hits first, with ties broken by symbol name so the ordering is total and deterministic (Constraint 1). Tell the reader how to disambiguate: a more qualified pattern such as `Data.Entities.Perfume` narrows it.

Do not change which rows are returned. This is a reporting change; suppressing or ranking results would break Constraint 1.

- [ ] **Step 4: Restore the claim in the docs**

Put the ambiguity sentence back into `skills/vela/SKILL.md`, now that it is true, and make sure `README.md` describes the behaviour accurately.

- [ ] **Step 5: Run the tests, then verify on ScentVerdict**

`vela refs Perfume` must list the distinct symbols and their counts. Quote the real output.

- [ ] **Step 6: Commit**

```bash
git commit -m "feat: report when a bare name matches several distinct symbols"
```

---

### Task 3: Make the latency claim true

**The defect.** Measured on ScentVerdict: process start floor 0.12s, a small `def` query about 1.0s, and `refs Perfume` with 3,104 results 3.4s. The staleness check stats every file under the solution root, which is 50,906 files on that repository, on every single invocation. `docs/design-notes.md` and `README.md` claim answers "in milliseconds".

Two things are wrong: the walk is unbounded, and the claim is unearned.

**Files:**
- Modify: `src/Vela/Indexing/Staleness.cs`
- Modify: `tests/Vela.Tests/` as appropriate
- Modify: `README.md`, `docs/design-notes.md`

- [ ] **Step 1: Write the failing test**

Assert that the staleness walk only considers files whose extension vela actually indexes, and that it skips the default-excluded directories. A test that creates a temp tree with many irrelevant files (for example `.png`, `.txt`, files under `node_modules`) plus one relevant `.cs` file, and asserts the walk visits the relevant one and reports staleness correctly while not being affected by the irrelevant ones.

Then measure: add a test or a benchmark assertion that the walk over a tree of N irrelevant files does not scale with N. If a timing assertion would be flaky, assert the *count of files examined* instead by making that count observable. Prefer the deterministic assertion.

- [ ] **Step 2: Run the test and see it fail**

- [ ] **Step 3: Bound the walk**

Restrict the staleness walk to files with extensions vela indexes (`.cs`, `.vb`, `.cshtml`, `.razor`, and the project and solution files that change what is compiled), and skip the default-excluded directories (`bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules`, and the index cache directory). Keep the existing behaviour of naming the most recently changed file, because the ScentVerdict run showed that message is genuinely useful.

Do not cache mtimes between runs and do not add a daemon. Nothing stays resident between queries; that is the architecture.

- [ ] **Step 4: Correct the claim**

Replace "milliseconds" in `README.md` and `docs/design-notes.md` with the measured truth, and cite the measurement: on a 375,608-line C# solution with 307 Razor views, a typical query answers in about a second, and a query returning several thousand results takes about three. Compare it honestly to the alternative the design notes already measure: 9.3s to load plus 23.8s to compile a live workspace, per invocation.

Keep the comparison, because it is the real argument. Drop the overstatement.

- [ ] **Step 5: Re-measure on ScentVerdict and record the numbers**

Time `vela def Perfume.Status` and `vela refs Perfume` after the change and report both, alongside the before figures (1.0s and 3.4s).

- [ ] **Step 6: Run the whole suite and commit**

```bash
git commit -m "perf: bound the staleness walk, and claim the latency we actually have"
```

---

## Out of scope

Deferred deliberately, and tracked in the research report at `docs/research/SCIP_Multilanguage_Research_20260729/`:

- Real SCIP symbol monikers replacing `SymbolIdentity.For`. The keystone for interoperability, and a task in its own right.
- The `.scip` importer, with its three hazards: per-document position encoding, `local N` collisions, and project-root rebasing.
- The config file for language selection.
- The upstream Razor contribution to `scip-dotnet`.
- Incremental reindex. The 2m12s full rebuild on 434,000 lines is tolerable, so this stays deferred until it is not.
