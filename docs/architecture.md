# Architecture

**An explanation.** How vela is put together, why the schema has the shape it has, and how
we know the answers are right.

If you want to run something, the [guides](guides/) are the place. Nothing here is a
command.

## The four layers

```
harvest  ->  SCIP  ->  SQLite  ->  query
```

Each is replaceable without touching the others, and each exists for a reason that is not
"that is how these things are usually built".

### 1. Harvest

A Roslyn harvester loads the solution through MSBuild and walks it, emitting SCIP.

It differs from Sourcegraph's `scip-dotnet` in two deliberate ways:

- **It iterates the compilation's syntax trees**, including source-generated documents,
  rather than `project.Documents`. This is what buys Razor and Blazor. See
  [Why Razor works here](#why-razor-works-here-and-nowhere-else).
- **It records enclosing ranges**, so `impact` is a stored edge rather than an inference.

Both are upstreamable. The first
[has been sent upstream](https://github.com/sourcegraph/scip-dotnet/pull/117); we carry
them until they land.

### 2. SCIP as the interchange format

[SCIP](https://github.com/scip-code/scip) is the standardised, mature format for exactly
this: language-server-grade intelligence harvested once and persisted. Emitting it is what
lets vela consume indexes produced by anyone else, so .NET is the flagship rather than the
limit.

That is no longer only an intention. `vela import` reads a `.scip` from any indexer, and it
has been proved against a real `scip-typescript` 0.4.0 index: four TypeScript files from a
Vue mobile app sit beside the C# and Razor halves in one database, and all of them answer to
the same verbs. On ScentVerdict, the solution vela is developed against, that was 2,341 C#
documents and 334 Razor views on 30 July 2026.

The format is also a safer bet than it was. SCIP moved out of Sourcegraph's ownership into
independent governance on 25 March 2026, with a steering committee drawn from Meta, Uber and
Sourcegraph and an enhancement-proposal process for schema changes. See
[the SCIP ecosystem](scip-ecosystem.md).

### 3. SQLite

Documents, symbols, occurrences and imported-source records, plus FTS5 for name search. One
portable file, in a cache directory outside the repository. Nothing resident between
queries.

### 4. Query

A CLI answering in about a second, with output shaped for a context window rather than a
terminal.

## Why not the alternatives

**A live Roslyn workspace per invocation.** Measured on a 375,608-line, ten-project
solution: 9.3s to load, 23.8s more to compile the web project, about 1GB peak. Correct, and
unusable in a loop, because nothing stays resident and you pay it again on the next
question.

vela's own cost on the same solution, measured on 30 July 2026 once the index is built:

| | Results | Time |
|---|---|---|
| process floor (`vela --version`) | | 0.08 to 0.09s |
| `find Perfume` | 14,917 symbols | about 0.40s |
| `def Perfume.Status` | 2 | about 0.57s |
| `refs Entities.Perfume.Status` | 24 | about 1.05s |
| `refs Perfume` | 3,156 | about 1.3s |
| `impact PerfumeService` | 4 | about 1.2 to 1.5s |

Not milliseconds, and this documentation used to say it was. The comparison is still the
honest one: a live workspace pays 33 seconds on every single invocation; vela pays a cost
like that once, at index time, and answers everything after it from a file.

**A resident daemon or an MCP server.** Amortises that cost and keeps about a gigabyte alive
per project. But the cost is a *build* cost, not a *query* cost, so paying it once at index
time removes the reason to hold anything open. A tool you leave installed should not be a
tool that costs a gigabyte to have installed.

**Consuming `scip-dotnet` unmodified.** It is missing both of the things vela exists to
provide.

## Why Razor works here and nowhere else

Razor views and Blazor components never exist as files Roslyn reads from disk. The Razor
source generator turns each `.cshtml` and `.razor` into C# and hands it straight to the
compilation, so those documents live behind `Project.GetSourceGeneratedDocumentsAsync`.

Every general-purpose code-intelligence tool iterates the files on disk and therefore skips
them. Checked directly rather than inferred:

| Tool | Stars | Indexes `.cshtml` / `.razor` |
|---|---|---|
| CodeGraph | 63k | no |
| codebase-memory-mcp | 36k | no |
| Serena | 27k | no, the matcher is `.cs` only (`ls_config.py:374`) |
| Sourcegraph `scip-dotnet` | 32 | **no**, verified by indexing a real solution |

For `scip-dotnet` the cause is one line, `ScipProjectIndexer.cs:110`:

```csharp
foreach (var document in project.Documents)
```

Measured on the same solution's web project, on 30 July 2026:

```
on-disk documents : 174
syntax trees      : 509
generated trees   : 335
  of which Razor  : 334   (against 334 .cshtml on disk)
#line mapping     : YES
```

vela iterates the compilation's syntax trees instead, and maps every location back through
its `#line` directives to the originating `.cshtml` or `.razor`, so the hit you are shown is
one you can open.

**This is the property that regresses silently.** Lose it and the index still builds, every
query still answers, and the Razor half of a codebase is simply not there. There is no error
to see. `vela index --stats` counts it, and the test suite asserts both the document count
and the occurrence count, because seven empty Razor documents would satisfy the first alone.

## The schema, and the two-names decision

The interesting part of the schema is the `occurrence` table, which stores **two names for
every symbol**. They are not interchangeable, and choosing one would have broken something.

```sql
CREATE TABLE occurrence (
    id            INTEGER PRIMARY KEY,
    document_id   INTEGER NOT NULL REFERENCES document(id),
    symbol        TEXT NOT NULL,        -- the Roslyn display name
    scip_symbol   TEXT NOT NULL,        -- the SCIP moniker
    is_definition INTEGER NOT NULL,
    start_line    INTEGER NOT NULL,
    start_char    INTEGER NOT NULL,
    enc_end_line  INTEGER,
    enc_end_char  INTEGER
);
```

**`symbol` is the Roslyn display string:**

```
ScentVerdict.Data.Entities.Perfume.Status
```

This is what a person or an agent types and reads. Every query matches against it, the
whole-dotted-segment rule operates on it, and the ambiguity tally groups by it.

**`scip_symbol` is the SCIP moniker for the same thing:**

```
scip-dotnet nuget ScentVerdict.Data 1.0.0.0 ScentVerdict/Data/Entities/Perfume#Status.
```

This is what makes the index exportable, and what lets vela correlate an index somebody
else's tool produced with this one.

### Why not just one of them

The moniker alone cannot be typed. Nobody is going to write
`scip-dotnet nuget ScentVerdict.Data 1.0.0.0 ScentVerdict/Data/Entities/Perfume#Status.` at
a prompt, and the whole-segment matching rule that makes `Status` safe to type has no
meaning in that grammar.

The display name alone cannot be exported or correlated. It is Roslyn's rendering of a
Roslyn symbol; nothing outside .NET produces it or can match it.

So both are stored, and each is used for exactly what it is for. Two things follow that a
consumer of the database has to know, because both are sentinels rather than monikers:

- **`scip_symbol = ''` means the occurrence has no moniker**, not "the empty moniker".
  `scip.proto` makes `Occurrence.symbol` optional, and vela leaves it empty rather than
  claim a document scope that an array type or the global namespace does not have. On the
  real solution's index 23,200 occurrences of 935,731 carry no moniker, 2.48%. A join on
  this column without `AND scip_symbol <> ''` makes one equivalence class of all 23,200.
- **`scip_symbol` beginning `local ` is scoped to one document.** `scip.proto`: local
  symbols "MUST only be used for entities which are local to a `Document`, and cannot be
  accessed from outside the `Document`". The ids are per-document counters, so `local 1` in
  two files is two unrelated things, and on this index three documents carry one. A join on
  this column must also match `document_id`.

The display name in `symbol` is namespaced by document and has neither problem, which is why
it is the column every query uses.

### Enclosing ranges

`enc_end_line` and `enc_end_char` are the other departure from `scip-dotnet`. A definition
records where its body ends, so `impact` can ask "which definition's range contains this
reference" as a join rather than reconstructing scope at query time.

Containment is tested on (line, character) pairs and never on lines alone. C# permits
several members on one line, and generated Razor emits a great deal of code that way, so a
line-granular test attributes a call in `A` to `B` in
`class C { void A(){ Helper.Do(); } void B(){} }`. That is not noise beside the right
answer; it is a single confident wrong one, and a named caller invites no second look.

Only the innermost enclosing definition counts, which is the candidate that opens last.
Every remaining tie-break is an exact stored value, in a fixed order, so the same index
answers the same question identically on every machine. Nothing is scored, ranked or
guessed.

### The rest of the tables

| Table | Holds |
|---|---|
| `document` | path, language, whether it is source-generated, its declared position encoding, and which `.scip` it came from (`''` means vela's own harvest) |
| `symbol_fts` | an FTS5 index over symbol names, which is what `find` searches |
| `external_document` | the paths this index deliberately does not hold, named rather than counted |
| `index_health` | the indexing pass's verdict on itself, when it ran, and how it was built (`rebuild` is `NULL` for a full rebuild) |
| `import_health` | one row per imported `.scip` whose last import lost something; presence is the degradation |
| `imported_source` | every `.scip` ever imported, with its content hash, which is what makes an import survive a rebuild |
| `project_input`, `project_input_document`, `project_input_reference` | the ledger: what each project was built from, and the project reference graph |
| `project_note` | every reason one project is missing code from this index, against the project rather than against the run |
| `project_document` | which documents each project contributed to |

`import_health` and `imported_source` are separate tables keyed the same way, and the
separation is load-bearing. `import_health` holds only the imports that lost something, so a
perfect import leaves no row there, and a perfect import is exactly the one a rebuild must
not throw away.

That was found the hard way. A cache rebuilt one morning held 2,205 C# documents, 307 Razor
and zero TypeScript, because a proven `scip-typescript` import had been wiped by a routine
re-index, silently, at exit 0, with `degraded = 0`. A whole language had disappeared from an
index that called itself complete.

### The ledger, and deciding what to rebuild

The last four tables exist for one feature: `vela index --incremental`, which is off by
default. Nothing else reads them, and a full rebuild writes them and never looks at them
again.

**A fingerprint is what a project was built from.** Every file the compiler was handed,
hashed by content: sources, the `.cshtml` and `.razor` additional documents, the analyzer
configs, the project file and every `Directory.Build.props` and friend between it and the
root. Content hashes rather than modification times, because an mtime changes when nothing
did (a checkout, a `touch`) and does not change when something did (a file restored with its
timestamp preserved). The first costs a needless rebuild; the second leaves the index
describing code that no longer exists while reporting itself complete, which is Constraint
3's exact failure.

A source-generated document is deliberately **not** hashed. Its content is derived, so
hashing it records a consequence rather than a cause, and it could only be computed by
running every generator again, which is most of the work incremental exists to avoid. Roslyn
hands over the real input instead: a `.cshtml` arrives as an additional document, on disk and
readable. Assembly references are hashed by **path** and not by content, because reading
several hundred assemblies per project would cost more than the rebuild it avoids; the path
carries the version, so an upgrade is caught and a rebuilt assembly at the same path is not.
That is the one deliberate hole and it is why the flag is opt-in.

Fingerprinting all ten projects of the real solution, 6,070 inputs, costs 554ms cold and
76ms warm, against a full index of about 158s. Roughly 0.4%, which is what it has to be for
the decision it enables to be worth making.

**The plan is pure.** `RebuildPlan.For` takes the current fingerprints, the ledger, the
schema version and the vela version, and returns which projects to rebuild, which to reuse,
and one sentence per decision. No I/O, no clock, no environment, so it is tested without a
workspace or a database and the same inputs give the same set in the same order.

**The hard part is the closure**, and it is where silent staleness would come from. A
project is not independent: change a public member in one and every reference to it in the
projects downstream moves, though not one of their files was touched. So the set is the
changed projects plus everything transitively downstream over the **current** reference
graph. Current rather than recorded, because an edge that has since been deleted cannot
propagate a change, and the project that deleted it changed its own project file anyway. The
walk is breadth-first over a membership set, so a diamond names a project once and a cycle
terminates rather than hanging.

There is a second closure, over shared documents. A document in this index is keyed by the
file a developer can open, and two projects can compile one file, so both projects'
occurrences land in one row. Replacing that row on behalf of one project would delete the
other's occurrences and nothing would put them back. So a project sharing a document with a
selected project is selected too.

It walks **both** what `project_document` recorded and what each project's fingerprint says
it compiles now, and it needs both. The ledger catches a project that has stopped compiling
a shared file: its rows are still in the database and are deleted on its behalf, and only
the ledger remembers that anybody else contributed to the same document. The current compile
set catches the reverse, which the ledger cannot see at all: a project that has just started
compiling a file another project was already compiling has no ledger entry joining the two,
and the load deletes every path the fresh harvest names, so that document goes and takes the
other project's occurrences with it, at exit 0 with no banner.

**What it counts as a document is narrower than what Roslyn hands over, and deliberately.**
Every source file the compiler is given becomes a document. Additional documents do not:
Roslyn passes every `AdditionalFiles` item a project declares, and only the ones the Razor
generator reads, the `.cshtml` and `.razor` files, become documents with occurrences on
them. The rest are analyser inputs, a `stylecop.json` or a `BannedSymbols.txt`. They are
hashed into the project's fingerprint, because changing one changes what the project
compiles to, and they are left out of this closure, because a file that becomes no document
cannot be a document two projects share. Counting them was not wrong in the dangerous
direction, but it was expensive in a way that would have gone unnoticed: one root-level
`<AdditionalFiles Include="../stylecop.json" />`, which is the ordinary way to configure an
analyser across a solution, put every project into a single shared group, so any edit at all
closed over the whole solution and `1 of 10` became `10 of 10` with the reason "it compiles
stylecop.json".

Roslyn's reference edges are a transitive superset of the declared `<ProjectReference>`
entries, 34 against 21 on the real solution, because MSBuild flows project references
transitively and Roslyn reports the resolved set. A superset only ever widens the closure,
which is the safe direction.

**`project_note` is what stops a skipped project going quiet.** A project that will not
compile is fingerprinted like any other, so an incremental run can skip it, and its
`compile-error:` note is produced fresh by each harvest and by nothing else. Recording the
note against the run would have meant a skipped project lost it: the index would stop calling
itself degraded while still holding an incomplete picture of that project. A broken project
that goes quiet is precisely the failure vela exists to prevent, so the notes are recorded
against the project and survive being skipped.

**On the real graph, the honest shape of the feature** is that nothing changed rebuilds 0 of
10, a leaf edit rebuilds 1 of 10, and one line in `ScentVerdict.Data` rebuilds 10 of 10,
because `Data` is upstream of every other project. The last case falls back to a genuine full
rebuild, since rebuilding all ten one at a time is a slower route to the same index. See
[the reference](reference.md#what---incremental-actually-saves) for the timings.

### No migrations

The index carries a schema version, currently 9, and a build that reads a different one
refuses to answer. There is no migration path, deliberately: re-indexing takes seconds and
rebuilds from the truth, where a migration would rebuild from a guess about what the old
rows meant.

## The three constraints

Break any of these and it stops being trustworthy.

**1. Deterministic only.** Every answer follows from the compiler. No model calls, no
network, no telemetry, no heuristic ranking. Ordering is total and ordinal everywhere, so
the same index answers the same question identically on every machine and in every locale.
Nothing to triage.

**2. Never write to the indexed repository.** The index lives in a cache directory keyed by
solution path, and vela refuses to run if that path resolves to somewhere inside the
solution's own tree. Indexing someone's repository must leave it byte-identical.

**3. An incomplete index must never look like a complete one.** This matters more here than
in most tools. A code-intelligence tool that silently returns partial references is worse
than grep, because you believe it, and an agent handed an empty reference list concludes the
symbol is unused and deletes it.

The third constraint is why so much of the output is sentences rather than numbers: why no
verb prints a bare zero, why `refs` says how much it suppressed, why a config job that has
not been imported degrades every answer, and why the ambiguity block exists at all.

**It cuts both ways.** A banner that fires when nothing is wrong is a banner nobody reads by
the time it is right. So documents contributed by the NuGet package cache or the .NET
installation are reported plainly and never raise the exit code, and neither does a language
no job covers, because vela was never going to index it. Only a real gap gets the `!!`.

## How we know it is right

Two changes to the matching rule are the clearest evidence, because both were silent
mis-answers on a real solution and both were verified over every symbol in the index rather
than on the example that found them. **Every count in this section is from the ScentVerdict
index as it stood on 29 July 2026, when the two bugs were found and fixed.** They are a
record of what the fixes recovered, not figures today's index reproduces: that repository
has grown since, and the same queries now return larger numbers.

**Parameter lists were being read as part of a name.** Cutting a stored name at its first
`(` also threw away every segment after the closing `)`, and a local or a parameter is
stored as exactly that:

```
App.Services.PerfumeService.PerfumeService(ILogger<...>, IImageService).logger
```

Cut at the first `(`, its last segment reads `PerfumeService`. So `refs PerfumeService`
answered with the constructor's parameters as though they were the type, and `refs Get`
answered **9,613** occurrences where 423 were real.

Reading the parameter list correctly removes 2,318 symbols and 9,190 rows from that answer.
Checked over all 135,555 distinct symbols in the index: **not one of the removed symbols is
actually named `Get`** (they are locals and parameters declared inside some `Get(...)`), and
**not one legitimate row was lost.**

**Generic type arguments were hiding symbols.** A constructed generic carries its arguments
inside its own last dotted segment:

```
Microsoft.Extensions.Logging.ILogger<ScentVerdict.Web.Pages.IndexModel>
ScentVerdict.Ai.Auditing.AuditRunner.RunWithAuditAsync<(int, int)>(System.String)
```

so no bare name reached either of them. `refs ILogger` answered **24** where **563** existed
that day. The same query answers 619 on 30 July 2026, which is the count that has moved and
not the bug.

Folding the type arguments out recovers 269 symbols and 539 rows. Every one of them really
is named `ILogger`, and **not one row was lost** to the change. 20,281 of the index's 135,555
distinct symbols carry a `<` at all, and 101,073 occurrences sit on them, so this was not an
edge case.

That second one is the more dangerous shape of error. An answer that says a symbol used
everywhere is barely used is the one that gets it deleted.

A type argument list is read by matching brackets rather than by cutting at the first `<`,
because it can hold anything a type can, including tuples with their own parentheses and
further generics. Two guards go with it, and both are load-bearing: a name whose brackets do
not pair is returned untouched (18 symbols in that index have an angle bracket that opens or
closes nothing, `System.DateTime.operator <(System.DateTime, System.DateTime)` among them),
and a group is only removed when what follows its closing bracket cannot continue an
identifier, which is the guarantee that taking a group out never joins two identifiers into
one.

### Coverage that must not regress

On a `dotnet new webapp` scaffold, `vela index --stats` prints:

```
documents            : 23
  generated          : 8   (compiled, not on disk)
  razor views        : 7   (.cshtml and .razor)
occurrences          : 2670
  in razor views     : 22
  definitions        : 182
```

`razor views` must equal the `.cshtml` count on disk and `in razor views` must be non-zero.
`EndToEndTests.IndexWithStats_ReportsTheCoverageThatMustNotRegress` asserts both by count,
and CI runs it as a separate named step so a failure says what broke.

420 tests, all hermetic: no network for the tool, throwaway solutions in temp directories.
The fixtures do run `dotnet new webapp`, `dotnet new blazor` and `dotnet restore`, so a cold
NuGet cache needs network for test setup.

## Scope

Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into those
compilations, which is how Razor Pages, MVC views and Blazor components arrive. They are
generated **C#** whatever the host project's language, so the Razor family is C#-side either
way. `LanguageNames` carries an `FSharp` constant with no Roslyn implementation behind it,
so F# is out of scope.

Everything else reaches the index through `vela import`.

## Non-goals

- **Editing or refactoring.** vela reports.
- **Semantic or vector search.** The index is exact. Ranking by similarity is a different
  tool with different failure modes.
- **Replacing grep.** For distinctive identifiers grep wins on zero setup, and the
  documentation should say so rather than oversell.
- **An MCP server.** The CLI is the interface; every agent can already run a command.
- **Running other languages' indexers.** `scip-io` exists for orchestration; vela consumes
  merged output.

## Further reading

- [Design notes](design-notes.md), the historical record of why the tool is shaped this way.
- [The Razor change we owe scip-dotnet](upstream/scip-dotnet-razor.md), the patch and its
  measurements.
- [The SCIP ecosystem](scip-ecosystem.md).
