# vela - design notes

Why the tool is shaped this way. Written before implementation, from measurements
taken on a real 375k-line .NET solution.

> **This page is a historical record, kept deliberately.** It says what was decided
> and why, before any of it existed, and most of it turned out to be right. Where the
> implementation has since moved past it, a **Since then** note says so rather than the
> text being quietly rewritten, because a design document that edits itself to match the
> code stops being evidence of anything.
>
> For the architecture as it stands today, read
> [architecture.md](architecture.md). For what the tool does, read
> [reference.md](reference.md).

## The problem

An AI coding agent working in a .NET repository discovers structure by grepping.
For distinctive identifiers that is fine. For ordinary ones it is close to useless,
and the failure is quiet: the agent gets a plausible-looking answer that is mostly
noise, or misses call sites entirely and concludes a symbol is unused.

Measured on a 375,608-line C# solution with 307 Razor views:

| Symbol | Real references | `grep -w` hits | Precision |
|---|---|---|---|
| `Perfume.Status` | 23 | 1,430 | **1.6%** |
| `Perfume.Name` | 243 | 2,760 | 8.8% |
| `Brand.Name` | 324 | 2,760 | 11.7% |
| `PerfumeService` | - | 24 | grep is fine |

> **Since then.** Those grep counts were taken with
> `grep -rw --include='*.cs' --include='*.cshtml' <name> src`, over `src/` alone. Re-measured
> on 30 July 2026 over the whole repository, the real reference counts are 24, 244 and 325,
> and `grep -w` returns 2,267 lines for `Status` and 3,653 for `Name`. The ratios are of the
> same order and the conclusion is unchanged. The current table, with the exact commands,
> is in [the querying guide](guides/querying.md#is-this-used-anywhere).

The names where grep collapses - `Name`, `Status`, `Value`, `Id`, `Update` - are
exactly the ones you most need answered. 2,760 hits is not context; it is a denial
of service on the context window.

Beyond noise, there are questions grep cannot answer at any precision: which
occurrence is the definition, whether `@Model.Perfume` in a `.cshtml` binds to a
particular property on a particular type, where an inherited or extension member
called as `x.Foo()` is actually defined, which overload is meant, and what
`using Foo = Bar` aliases.

(Interface implementations are a SCIP `Relationship`, which vela does not emit yet
and no verb answers. They are not in the list above for that reason.)

## The gap nothing else fills

Every general-purpose code-intelligence tool for agents stops at what is on disk.
Checked directly, not inferred:

| Tool | Stars | Indexes `.cshtml` / `.razor` |
|---|---|---|
| CodeGraph | 63k | no |
| codebase-memory-mcp | 36k | no |
| Serena | 27k | no - matcher is `.cs` only (`ls_config.py:374`) |
| Sourcegraph `scip-dotnet` | 32 | **no** - verified by indexing a real solution |

Sourcegraph's indexer is Roslyn-based and still misses Razor, for one identifiable
reason. `ScipProjectIndexer.cs:110`:

```csharp
foreach (var document in project.Documents)
```

`project.Documents` is on-disk files only. Razor views reach the compilation as
**source-generated documents**, which that loop never sees.

Measured on the same solution's web project:

```
on-disk documents : 146
syntax trees      : 454
generated trees   : 308
  of which Razor  : 307   (vs 307 .cshtml on disk)
#line mapping     : YES
```

`SymbolFinder.FindReferencesAsync` returns hits inside them, attributed to the
right view:

```
Perfume  707 refs |  5 in Razor  ->  Pages_Shared__DupeBanner_cshtml.g.cs
```

The same generator handles Blazor. A default Blazor app emits 11 generated
documents from its `.razor` components. So the whole Razor family - Razor Pages,
MVC views, and Blazor - is reachable, and nothing on the market reaches it.

## What vela is

A local, deterministic code index for .NET, built by the compiler and queried from
a CLI. Index once, then query in about a second, nothing resident.

It is not a language server, not an MCP server, and not a daemon.

## Architecture

Four layers, each replaceable without touching the others.

```
harvest  ->  SCIP  ->  SQLite  ->  query
```

**1. Harvest.** A Roslyn harvester walks the solution and emits SCIP. It differs
from `scip-dotnet` in two deliberate ways:

- it iterates the **compilation's syntax trees**, including source-generated
  documents, which is what buys Razor and Blazor
- it records **enclosing ranges**, so callers are a stored edge rather than an
  inference

Both are upstreamable to `scip-dotnet`; we carry them until they land.

> **Since then.** The first of the two has been sent upstream:
> [sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117),
> "Index Razor views and Blazor components", is open against their `main`. See
> [the write-up](upstream/scip-dotnet-razor.md).

**2. SCIP as the interchange format.** SCIP is the standardised, mature format for
exactly this - language-server-grade intelligence harvested once and persisted.
Emitting it is what would let vela consume indexes produced by anyone -
`scip-typescript`, `scip-python`, and the rest - so that .NET is the flagship rather
than the limit. That is the reason for the choice, not a feature that exists: there
is no `.scip` import path today, and everything in the index is harvested from
Roslyn.

> **Since then.** `vela import` exists and is proven. A real `scip-typescript` 0.4.0
> index over four TypeScript files sits beside 2,205 C# documents and 307 Razor views in
> one database, and both halves answer to the same verbs. `vela.json` declares which
> indexers a repository expects, and a job that has not been imported degrades the index
> until it is. See [the multi-language guide](guides/multi-language.md).
>
> SCIP itself also moved out of Sourcegraph's ownership into independent governance on
> 25 March 2026, which makes the bet safer than it was when this was written. See
> [the SCIP ecosystem](scip-ecosystem.md).

**3. SQLite.** Documents, symbols, occurrences and relationships, plus FTS5 for
name search. One portable file. Nothing resident between queries.

> **Since then.** The schema is version 7 and holds documents, occurrences, an FTS5
> symbol table, the external documents deliberately left out, and three health and
> provenance tables. It does not hold relationships: interface implementations are still
> unanswered. Two names are stored for every symbol, the Roslyn display name and the SCIP
> moniker, which is [explained in architecture.md](architecture.md#the-schema-and-the-two-names-decision).

**4. Query.** A CLI answering in about a second, with output shaped for a context
window rather than a terminal.

### Why not the alternatives

**Live Roslyn workspace per invocation.** Measured: 9.3s to load a 10-project
solution, 23.8s more to compile the web project, ~1GB peak. Correct but unusable
in a loop.

vela's own cost, measured on the same 375,608-line solution once the index is
built: about 0.45s for a typical query (`def Perfume.Status`) and about 1.5s for
one returning several thousand results (`refs Perfume`, 3,104 of them). Not
milliseconds - an earlier version of this document and the README both said so,
and neither had measured it. The comparison above is still the honest one: a
live workspace pays 9.3s plus 23.8s on every single invocation, because nothing
stays resident; vela pays a cost like that once, when the index is built, and
every query after that is seconds, not tens of seconds.

> **Since then.** Re-measured on 30 July 2026, against a larger index of the same
> solution which now also holds an imported TypeScript index: a 0.08 to 0.09s process
> floor, about 0.57s for `def Perfume.Status`, about 1.3s for `refs Perfume`. The current
> table is in [architecture.md](architecture.md#why-not-the-alternatives).

**A resident daemon or MCP server.** Amortises that cost but keeps ~1GB alive per
project. The cost is a *build* cost, not a *query* cost, so paying it once at index
time removes the reason to hold anything open.

**Consuming `scip-dotnet` unmodified.** It is missing both things vela exists to
provide.

## The verbs

| Verb | Answers |
|---|---|
| `outline <file\|type>` | the symbol tree, without reading the file |
| `def <symbol>` | declaration, signature, source span |
| `refs <symbol>` | every usage, grouped by file, Razor included |
| `impact <symbol>` | callers and blast radius before you change it |
| `find <pattern>` | symbol search by name and kind |

`outline` then `def` is the intended path: establish shape cheaply, pull only the
body you actually need.

> **Since then.** Two of those signatures were optimistic. `outline` takes a **file path
> only**, not a type; and `find` searches names only, not kinds. There is also a sixth
> verb, `import`, which this document did not anticipate at all. The verbs as built are in
> [reference.md](reference.md#verbs).

## The three constraints

Break any of these and it stops being trustworthy.

1. **Deterministic only.** Every answer follows from the compiler. No model calls,
   no network, no telemetry, no heuristics. Nothing to triage.

2. **Never write to the indexed repository.** The index lives outside the source
   tree by default. vela reads; it does not modify.

3. **An incomplete index must never look like a complete one.** This matters more
   here than in most tools. A code-intelligence tool that silently returns partial
   references is worse than grep, because the agent believes it. If a project fails
   to load, every query touching it says so, and the exit code reflects it. Absence
   of results must never be reported as evidence of absence.

## Scope

Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into
those compilations - which is where Razor Pages, MVC views and Blazor components
arrive. `LanguageNames` carries an `FSharp` constant but there is no Roslyn F#
implementation, so F# is out of scope.

Both languages are handled in the harvester. Razor Pages, MVC views and Blazor
components arrive as generated **C#** whatever the host project's language, so the
Razor family is C#-side either way.

Other languages would be reachable by consuming their existing SCIP indexers. vela
neither implements them nor, yet, imports them.

> **Since then.** It imports them. `vela import` reads a `.scip` from any indexer into the
> same database, and `vela.json` declares which ones a repository expects.

## Non-goals

- Editing or refactoring. vela reports.
- Semantic or vector search. The index is exact; ranking by similarity is a
  different tool with different failure modes.
- Replacing grep. For distinctive identifiers grep wins on zero setup, and the
  README should say so rather than oversell.
- An MCP server. The CLI is the interface; every agent can already run a command.

## Open questions

- **Staleness policy.** Settled for now at the cheap end: the index records when it
  was built, and every query compares that against the newest modification time of the
  files it watches under the repository root the index was built against. Anything
  newer degrades the answer, which means both the banner and exit code 3.

  The root is the one place both the emitter and the check read from, so nothing can
  be indexed from outside the tree the check walks: a `repo/src/App.sln` layout used
  to have everything under `repo/tests/` indexed and unwatched, and that is fixed. The
  watched *set* is a different matter, and it is a proper subset of the indexed set.
  Only files vela could index or that decide what is compiled are examined - `.cs`,
  `.vb`, `.cshtml`, `.razor`, `.csproj`, `.vbproj`, `.sln`, `.slnx`, `.props`,
  `.targets` - and `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules` and the cache
  directory are never descended into. On the real solution 340 indexed documents sit
  under `bin` and `obj` alone, and a change to one of those does not degrade anything.
  The exclusions are deliberate: build output changes on every build and `.git` on
  every command, so watching them would leave every query permanently degraded, which
  is a warning nobody reads. The cost of that narrowing is stated plainly in the
  README and in SKILL.md, because an agent must not read the absence of a banner as
  proof the tree is unchanged.

  The walk used to be unbounded, and stating all 50,906 files under the project root
  was what made a 1.0s `def` and a 3.4s `refs` out of a 0.12s process floor. It is
  now bounded by exactly the rules above, and the per-query figures under *Why not
  the alternatives* were measured after that fix.

  It is timestamps only - no file is read and nothing is hashed - so it stays coarse:
  it cannot say whether the symbol you asked about was the one that changed. A git ref
  and per-file hashes would narrow that, and would also let the watched set be the
  indexed set again rather than a cheaper approximation of it. Both are worth doing
  only alongside incremental reindex.

  > **Since then.** Incremental reindex exists and it does hash per file, so the material
  > is there. The freshness check is deliberately not built on it: hashing every input
  > costs 554ms cold on the real solution, and the freshness check runs on every query
  > where a fingerprint runs on every index. A check that made a sub-second `refs` half a
  > second slower to be more precise about a suspicion is the wrong trade. Still
  > timestamps, still coarse, still stated as such.
- **Incremental reindex.** Full index of a 10-project solution takes ~87s with
  `scip-dotnet` as a reference point. Whether per-project incremental work is worth
  the complexity is deferred until the full path is proven.

  > **Since then.** Built, on 30 July 2026, as `vela index --incremental`, and **off by
  > default**. The unit is a project, because Roslyn cannot give a semantic model without a
  > compilation and a compilation is per project. The set rebuilt is the projects whose
  > inputs changed plus everything transitively downstream of them, and a ledger of what
  > each project was built from - content hashes of every file the compiler was handed -
  > is what makes the claim checkable.
  >
  > **It was worth the complexity, conditionally, and the condition is the thing to say.**
  > On the real solution: nothing changed, 11.9s against a 158.1s full index, 0 of 10
  > projects rebuilt. One line in a leaf project, 22.2s, 1 of 10. One line in
  > `ScentVerdict.Data`, which is upstream of all nine others, 153.9s and 10 of 10, which
  > is a full rebuild reached the long way and saves nothing at all. Incremental helps most
  > when you edit a leaf; a change low in the dependency graph rebuilds nearly everything.
  > That is the closure being right rather than a defect, and it is why the flag is opt-in
  > rather than the default.
  >
  > Two things the complexity turned out to be, which were not obvious from here. **A
  > fingerprint opens a Constraint 3 hole:** a project that will not compile is
  > fingerprinted like any other, so it can be skipped, and its `compile-error:` note is
  > regenerated by each harvest and by nothing else. Recorded against the run, the note
  > would have vanished the moment the project was skipped and the index would have stopped
  > calling itself degraded while still holding an incomplete picture of it. The notes are
  > now recorded against the project. **And a document is not owned by one project:** two
  > projects can compile one file and both their occurrences land in one row, so replacing
  > it for one of them would delete the other's. That needed a second closure and a second
  > table.
  >
  > What it still cannot see is an assembly rebuilt in place at the same path and the same
  > version, because references are hashed by path rather than by content. A full index is
  > the answer to that, and it remains the default.

- **Index location.** A cache directory keyed by repo path, versus a file inside the
  repo that a team could commit. Leaning cache directory, to honour constraint 2.

  > **Since then.** Settled: a cache directory. `$XDG_CACHE_HOME/vela/<Name>-<hash>.db`,
  > where the hash is of the absolute solution path, and vela refuses to run if that
  > resolves to somewhere inside the solution's own tree.

Three things have been settled since this list was written, and are worth recording
because none of them was foreseen here.

- **The matching rule needed two corrections, both silent.** Reading a parameter list as
  part of a name made `refs Get` answer 9,613 where 423 are real; not folding generic type
  arguments made `refs ILogger` answer 24 where 563 exist. Both were verified over every
  symbol in the index, and both are written up in
  [architecture.md](architecture.md#how-we-know-it-is-right).
- **An ambiguity block was needed.** Whole-segment matching means a bare name can span
  several real symbols, and a single total across them is the size of nothing. `refs
  Status` on the real solution spans 154 distinct symbols.
- **An import has to survive a rebuild.** `vela index` deletes the database, so the first
  version of the import path lost every imported language on the next routine re-index,
  silently, at exit 0. The rebuild now replays what it replaced.

## Etymology

Vela is the sail of Argo Navis - the largest constellation ever catalogued, later
broken into Carina the keel, Puppis the stern, and Vela the sails. A whole
decomposed into its named parts, which is what an index of a codebase is. The sails
are also the part you navigate by.
