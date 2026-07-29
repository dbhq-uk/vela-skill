# vela: SCIP interoperability and multi-language indexes

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make vela a first-class SCIP citizen in both directions. Emit symbols any SCIP consumer can read, consume `.scip` indexes any other language's indexer produces, let a repository declare which languages it wants indexed, and give the Razor fix back to `scip-dotnet` so the tool everyone else uses stops being Razor-blind.

**Evidence base:** `docs/research/SCIP_Multilanguage_Research_20260729/SCIP_Multilanguage_Research.md`. Read it before starting; it carries the schema quotations and the reasoning behind every decision below.

## The decision that shapes everything: two names, not one

vela stores a Roslyn display string, `ScentVerdict.Data.Entities.Perfume.Status`. A SCIP symbol is a different grammar entirely:

```
scip-dotnet nuget ScentVerdict.Data 1.0.0 ScentVerdict/Data/Entities/Perfume#Status.
```

Namespaces end `/`, types `#`, terms `.`, methods `().`. It is not dotted, and it carries a package and a version.

**vela will store both, and will not replace one with the other.** The display name is what every query matches against, what the whole-dotted-segment rule operates on, what the ambiguity block tallies, and what a user or an agent types and reads. All of that was measured and hardened on the real solution over the previous plan and none of it should be thrown away. The SCIP moniker is what makes an index exportable and what lets an imported foreign index be correlated with vela's own.

So: a `symbol` column keeps the display name, and a new `scip_symbol` column carries the moniker. Queries continue to use `symbol`. Nothing about the query layer changes.

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework `net10.0`.** Do not change it or any package version, in particular the three `Microsoft.Build*` 17.11.48 entries with `ExcludeAssets="runtime" PrivateAssets="all"`.
- **Deterministic only.** No model calls, no network at index or query time, no telemetry, no heuristic ranking, no fuzzy matching.
- **Never write to the indexed repository.**
- **An incomplete index must never look like a complete one.** A configured job that fails, a `.scip` file that cannot be parsed, a language that could not be indexed: every one of them degrades the index and exits 3.
- **The load-bearing property is Razor and Blazor coverage.** The webapp fixture must keep yielding exactly 7 `.cshtml` documents and 23 total, with Razor occurrences non-zero. Never adjust a coverage test; if coverage drops, stop and investigate.
- **House style: British English, plain hyphens.** No em dashes or en dashes anywhere.
- 148 tests pass before this plan. No existing assertion may be weakened or deleted.
- **SCIP is the wire format.** Extend it through its own fields. The vendored `src/Vela/Scip/scip.proto` carries the specification in its comments and is the authority.

---

### Task 1: Emit real SCIP symbols

**Why.** `SymbolIdentity.For` returns a Roslyn display string. The implementation plan's own self-review named this the single interoperability limitation and the one place to change. Until it changes, no other SCIP tool can read a vela index and no foreign index can be correlated with one.

`scip-dotnet` has a 66-line reference implementation at `ScipDotnet/ScipSymbol.cs`, Apache 2.0. Port the approach with attribution; do not vendor the file.

**Files:**
- Create: `src/Vela/Harvest/ScipMoniker.cs`
- Modify: `src/Vela/Harvest/ScipEmitter.cs`, `src/Vela/Indexing/Schema.cs`, `src/Vela/Indexing/ScipLoader.cs`

- [ ] **Step 1: Write the failing tests**

Assert the grammar from `scip.proto`: `<scheme> ' ' <manager> ' ' <package-name> ' ' <version> ' ' (<descriptor>)+`. Cover a namespace, a type, a nested type, a method with and without parameters, a property, a field, a constructor, a generic type, a type parameter, a parameter, and a local. Assert locals use the `local <id>` form and are document-scoped, as the spec requires. Assert names needing escapes are backtick-escaped and that a backtick inside a name is doubled.

- [ ] **Step 2: Run them and see them fail**

- [ ] **Step 3: Implement the moniker**

Scheme `scip-dotnet` for compatibility with the existing .NET ecosystem, manager `nuget`, package name and version from the containing assembly. Emit `SymbolInformation` alongside so a consumer gets documentation and kind.

- [ ] **Step 4: Store both names**

Add a `scip_symbol` column to `occurrence` (schema 3 to 4). `symbol` keeps the display name and every query keeps using it. Set `Occurrence.Symbol` on the emitted SCIP to the moniker, since that field is the wire format.

- [ ] **Step 5: Verify against the real solution and commit**

Re-index ScentVerdict, confirm 307 of 307 Razor views, and confirm queries answer exactly as before. Commit: `feat: emit SCIP-standard symbol monikers alongside the display name`

---

### Task 2: Consume .scip indexes from any language

**Why.** This is what turns vela from a .NET tool into a polyglot one. `ScipLoader.Load` already takes a `Scip.Index`, and a `.scip` file is a serialised `Index`, so the parsing is small. The hazards are not.

**Files:**
- Create: `src/Vela/Indexing/ScipImporter.cs`
- Modify: `src/Vela/Indexing/Schema.cs`, `src/Vela/Program.cs`
- Create: `tests/Vela.Tests/ScipImporterTests.cs`

**Three hazards, all from the research report. Each needs a test.**

1. **Position encoding.** The spec tells indexers to pick by implementation language: .NET and TypeScript use UTF-16, Python UTF-32, Go and Rust and C++ UTF-8. A merged index legitimately holds all three. vela's `document` table has no encoding column, so importing a `scip-python` index today makes every column on a line with a non-ASCII character wrong. Store `position_encoding` per document and normalise on read.
2. **Local symbols.** `local 1` from a Python file and `local 1` from a TypeScript file are unrelated; the spec says locals are document-scoped. Namespace them per document on import or they merge.
3. **Project roots.** Each index carries its own `project_root` and every `relative_path` is relative to it. Rebase onto vela's own root, and record the ones that cannot be rebased rather than dropping them silently.

- [ ] **Step 1: Write the failing tests** covering all three hazards plus a round trip: emit a vela index, write it as `.scip`, import it into a fresh database, and assert the queries answer identically.
- [ ] **Step 2: Run them and see them fail**
- [ ] **Step 3: Implement `vela import <file.scip>`**, streaming rather than loading whole (the spec warns the payload is large and recommends field-at-a-time consumption). A `.scip` that cannot be parsed degrades the index; it never half-imports in silence.
- [ ] **Step 4: Prove it on a real foreign index.** Install `scip-typescript`, run it over `/home/devops/scentverdict/src/ScentVerdict.Mobile`, import the result, and query a TypeScript symbol. If the indexer cannot be installed offline, say so and fall back to a fixture, but say which you did.
- [ ] **Step 5: Commit** `feat: import .scip indexes from any language's indexer`

---

### Task 3: The config file

**Why.** ScentVerdict reports 5,775 Python files, of which 5,695 are vendored `venv` and `site-packages`. Opinionated default excludes are the feature; the config is how they are overridden. Prior art in the research report: Sourcegraph's `index_jobs` array is the right shape, because "which language" and "which indexer produced it" are separate facts.

**Files:**
- Create: `src/Vela/Config/VelaConfig.cs`
- Modify: `src/Vela/Program.cs`
- Create: `tests/Vela.Tests/VelaConfigTests.cs`

- [ ] **Step 1: Write the failing tests.** A jobs array, cumulative language selection, gitignore-style glob semantics, and the default excludes applied when no config exists. Assert that a job which fails to run marks the index degraded, because a config file must never become a quiet way to lose a language.
- [ ] **Step 2: Run them and see them fail**
- [ ] **Step 3: Implement.** `vela.json` at the solution root, JSON because that is what `global.json` and `dotnet-tools.json` established for .NET. Schema as in the research report. Absent config means today's behaviour exactly, so nothing breaks for an existing user.
- [ ] **Step 4: Write the ScentVerdict config** covering C#, Razor, the Mobile TypeScript and the Web JavaScript, with the default excludes, as agreed.
- [ ] **Step 5: Commit** `feat: declare which languages a repository wants indexed`

---

### Task 4: Give Razor back to scip-dotnet

**Why.** `scip-dotnet` is Razor-blind at `ScipProjectIndexer.cs:110`, verified from source: `foreach (var document in project.Documents)`. The only "Razor" in its codebase is a protobuf enum constant it never emits. It is Apache 2.0, actively maintained, about 1,163 hand-written lines, and has no competing issue or PR.

**The change is two pieces, not one.** Enumerating source-generated documents alone produces `RelativePath` values like `obj/Debug/net10.0/.../Pages_Index_cshtml.g.cs`, files that do not exist on disk. The `#line` mapping is what makes it useful, and vela already has both, tested.

- [ ] **Step 1: Clone `sourcegraph/scip-dotnet` into a temp directory.** Do NOT work inside the vela repository, and do NOT push anything anywhere.
- [ ] **Step 2: Port the two changes** with tests in their style, following their `Development.md`.
- [ ] **Step 3: Verify** their snapshot tests still pass and that a scaffolded Razor app now yields `.cshtml` documents.
- [ ] **Step 4: Write the patch and a PR description** to `docs/upstream/scip-dotnet-razor.md` in the vela repository, including the diff and the measurements. **The operator opens the PR, not the agent.**
- [ ] **Step 5: Commit** the write-up only: `docs: the Razor change we owe scip-dotnet`

---

## Out of scope

- Incremental reindex. A full rebuild of 434,000 lines takes 2m12s, which is tolerable.
- SCIP `Relationship`s, and therefore "what implements this interface". A separate feature.
- Orchestrating other indexers automatically. `scip-io` exists and does that; vela consumes the merged output. Revisit only if that proves inadequate.
- The carried items in the previous plan's ledger, unless a task here touches the same code.
