# vela - design notes

Why the tool is shaped this way. Written before implementation, from measurements
taken on a real 375k-line .NET solution.

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
a CLI. Index once, query in milliseconds, nothing resident.

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

**2. SCIP as the interchange format.** SCIP is the standardised, mature format for
exactly this - language-server-grade intelligence harvested once and persisted.
Emitting it is what would let vela consume indexes produced by anyone -
`scip-typescript`, `scip-python`, and the rest - so that .NET is the flagship rather
than the limit. That is the reason for the choice, not a feature that exists: there
is no `.scip` import path today, and everything in the index is harvested from
Roslyn.

**3. SQLite.** Documents, symbols, occurrences and relationships, plus FTS5 for
name search. One portable file. Nothing resident between queries.

**4. Query.** A CLI answering in milliseconds with output shaped for a context
window rather than a terminal.

### Why not the alternatives

**Live Roslyn workspace per invocation.** Measured: 9.3s to load a 10-project
solution, 23.8s more to compile the web project, ~1GB peak. Correct but unusable
in a loop.

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

## Non-goals

- Editing or refactoring. vela reports.
- Semantic or vector search. The index is exact; ranking by similarity is a
  different tool with different failure modes.
- Replacing grep. For distinctive identifiers grep wins on zero setup, and the
  README should say so rather than oversell.
- An MCP server. The CLI is the interface; every agent can already run a command.

## Open questions

- **Staleness policy.** Settled for now at the cheap end: the index records when it
  was built, and every query compares that against the newest modification time under
  the solution directory, skipping `bin`, `obj` and `.git`. Anything newer degrades the
  answer, which means both the banner and exit code 3. It is timestamps only - no file
  is read and nothing is hashed - so it is coarse: it cannot say whether the symbol you
  asked about was the one that changed. A git ref and per-file hashes would narrow that,
  and are worth doing only alongside incremental reindex.
- **Incremental reindex.** Full index of a 10-project solution takes ~87s with
  `scip-dotnet` as a reference point. Whether per-project incremental work is worth
  the complexity is deferred until the full path is proven.
- **Index location.** A cache directory keyed by repo path, versus a file inside the
  repo that a team could commit. Leaning cache directory, to honour constraint 2.

## Etymology

Vela is the sail of Argo Navis - the largest constellation ever catalogued, later
broken into Carina the keel, Puppis the stern, and Vela the sails. A whole
decomposed into its named parts, which is what an index of a codebase is. The sails
are also the part you navigate by.
