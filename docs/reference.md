# Reference

Every verb, argument, flag, exit code, matching rule and output line.

If you are looking for how to do a particular thing, the [guides](guides/) are the better
place. This page is for looking things up.

- [Verbs](#verbs)
- [Global options](#global-options)
- [Exit codes](#exit-codes)
- [How a symbol name is matched](#how-a-symbol-name-is-matched)
- [Output format](#output-format)
- [The banner](#the-banner)
- [Lines that are not the banner](#lines-that-are-not-the-banner)
- [Empty answers](#empty-answers)
- [Where the index lives](#where-the-index-lives)
- [Freshness](#freshness)
- [vela.json](#velajson)
- [Requirements](#requirements)

## Verbs

```
vela index                     Build the index for a solution
vela import <index>            Add a .scip index from another language's indexer
vela find <pattern>            Symbol search by name
vela def <symbol>              Where a symbol is defined
vela refs <symbol>             Every usage of a symbol
vela outline <file>            Symbols defined in a file
vela impact <symbol>           Callers and blast radius
```

### `vela index`

Loads the solution through MSBuild, harvests the compilation with Roslyn, and writes a
SQLite index. Rebuilds from nothing every time: the old database file is deleted first,
unless you pass [`--incremental`](#--incremental), which is off by default.

| Option | Meaning |
|---|---|
| `--solution <path>` | Path to the `.sln`. Defaults to the only `.sln` in the current directory. |
| `--stats` | After indexing, print document, generated-document, Razor, occurrence and definition counts, and list every document that was left out. |
| `--incremental` | Rebuild only the projects whose inputs changed, and every project downstream of them. **Off by default.** See [`--incremental`](#--incremental). |

`--stats` output on a `dotnet new webapp` scaffold:

```
documents            : 23
  generated          : 8   (compiled, not on disk)
  razor views        : 7   (.cshtml and .razor)
occurrences          : 2670
  in razor views     : 22
  definitions        : 182
```

`razor views` must equal the number of `.cshtml` and `.razor` files on disk, and
`in razor views` must be non-zero. Those two numbers are the only visible sign of the one
regression that is otherwise silent, so validate a change to the harvester with `--stats`.

Two further lines appear when the counts warrant them. `No Razor views are indexed.` means
source-generated documents are not reaching the index at all. `Razor views are indexed but
carry no occurrences` means the documents arrived and the `#line` mapping did not.

`vela index` rebuilds the C# half from nothing, and it replays every `.scip` that had been
imported into the index it replaced. See [`vela import`](#vela-import).

#### `--incremental`

Rebuilds only the projects whose inputs changed, plus every project transitively downstream
of them. **Off by default, and a full index is the right choice when in doubt.**

A full rebuild cannot be stale, because it reads everything. An incremental rebuild is a
claim that what it skipped has not changed, and if that claim is wrong the index holds rows
describing code that no longer exists, at line numbers that have moved, while reporting
itself complete. That is worse than the slowness it replaces, which is why it is opt-in.

**The unit is a project, not a file.** Roslyn cannot give a semantic model without a
compilation, and a compilation is per project, so if anything a project compiles has changed
that project is rebuilt whole. Then the closure: change a public member in a project and
every reference to it in the projects downstream moves, though not one of their files was
touched, so everything downstream is rebuilt too.

**What counts as an input.** Every file the compiler was handed, hashed by content: sources,
`.cshtml` and `.razor` additional documents, `.editorconfig` and generated `.globalconfig`,
the project file, and every `Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props`, `global.json` and `NuGet.config` between it and the root.
Content hashes, not modification times: an mtime changes when nothing did, and does not
change when something did.

Assembly references are hashed **by path and not by content**, because reading several
hundred assemblies per project would cost more than the rebuild it avoids. The path carries
the version, so a package upgrade is caught. **A rebuilt assembly at the same path and the
same version is not.** That is the one deliberate hole, and a full index is the answer to it.

**What it falls back to a full rebuild for**, saying so every time:

| Reason | Line it prints |
|---|---|
| No index yet | `there is no index at <path> yet, so there is nothing to compare this tree against` |
| Schema changed | `the index at <path> was built against schema version N and this vela reads schema version M` |
| An index built before vela kept the ledger | `no project in this index has a recorded fingerprint, so there is nothing to compare the tree against` |
| A different build of vela wrote it | `a different build of vela wrote this index ... Two builds can emit different occurrences from identical source` |
| The set of projects changed, in either direction | `the set of projects changed since this index was built: added ...; removed ...` |
| The closure reaches every project | `the change reaches every project in this solution, all N of them, so there is nothing left to reuse` |
| Anything at all went wrong deciding | `the plan could not be worked out: <message>` |

Each is followed by `A full rebuild cannot be stale, because it reads everything. This is
the safe outcome and not a failure.` **A fallback is a good outcome and it is never silent.**

**"Anything at all" is meant literally.** Every exception except the one raised by you
cancelling the run, whose whole point is that less work should happen rather than more.
There is no failure for which refusing to build an index is better than building it the slow
way, and nothing is hidden by choosing the slow way, because the failure's own message is
printed on the line that announces the fallback.

**"A different build of vela" means a different binary, not a different version number.**
The identity recorded against every project is the assembly version followed by the module
version id of the binary that ran, for example `1.0.0.0+8f3a2b1c9d4e`. It is derived from
the compiled module rather than declared in a file, because a version number is a promise
somebody has to remember to keep on the day they change the moniker grammar, and this is the
one record that has to be right about that day. C# builds are deterministic, so rebuilding
vela from unchanged source produces the same id and invalidates nothing; changing any line
of vela produces a different one. **So the first incremental run after upgrading or
rebuilding vela falls back to a full rebuild, and says so.** That is broader than "the
harvest changed" on purpose: it errs towards the rebuild that cannot be stale.

**What it prints when it does go incremental:**

```
Incremental rebuild: 1 of 10 project(s) rebuilt, 9 reused.
  rebuilt tests/ScentVerdict.Benchmarks/ScentVerdict.Benchmarks.csproj: its own inputs changed
  reused src/ScentVerdict.Data/ScentVerdict.Data.csproj
  ...
Replaced 11 document(s) with 11 in /home/devops/.cache/vela/ScentVerdict-cf73472b44f18ae0.db.
Every other document in it is the one the last build wrote, and this run did not look at the
code behind it.
```

The same sentence is stored in `index_health.rebuild`, which is `NULL` for a full rebuild.
So an absent value means "this index was built whole", and a reader who distrusts an answer
can find out whether the project it came from was looked at.

**A project that was skipped keeps saying what it could not do.** A project that will not
compile is fingerprinted like any other, so an incremental run can skip it. Its
`compile-error:` note is recorded against the project, not against the run, so a skipped
project goes on degrading the index and the banner does not go quiet. Anything else would
be the exact failure vela exists to prevent.

**An imported `.scip` survives.** A full rebuild deletes the database and replays every
import into the new one. An incremental rebuild never deletes the database and simply does
not touch rows another source contributed.

#### What `--incremental` actually saves

Measured on the ten-project, 375,608-line solution vela is developed against, on
30 July 2026. Each figure is a wall clock, and after each one the load-bearing counts were
unchanged: 307 of 307 Razor views with 50,355 occurrences in them, `refs Perfume.Status` 24,
`refs ILogger` 563, `refs Count` 2,573.

| What changed | Wall clock | What it rebuilt |
|---|---|---|
| nothing (full index for comparison) | 158.1s | all ten, from an empty database |
| nothing | **11.9s** | 0 of 10 |
| one line in a leaf project | **22.2s** | 1 of 10, 11 documents replaced |
| one line in the project everything depends on | **153.9s** | fell back to a full rebuild: the closure reached 10 of 10 |

So the saving is about 13x, about 7x, or nothing at all, and **which one you get is decided
by your dependency graph rather than by vela**. `ScentVerdict.Data` is upstream of all nine
other projects, so one line in it invalidates every row in the index. That is the closure
being right, not a defect: every reference to a `Data` type in every other project sits at a
line number a one-line insertion in `Data` can move.

**Incremental helps most when you edit a leaf. A change low in the dependency graph rebuilds
nearly everything, and trying costs a little more than not trying.** The decision itself is
cheap: fingerprinting ten projects and 6,070 inputs is 554ms cold and 76ms warm, and working
out the plan over ten projects and thirty-four reference edges is 7 to 9ms. The fallback is
taken before the harvest and reuses the workspace load the rebuild needed anyway, so a
wasted attempt costs about 0.6s on a 155s rebuild, which is inside the run-to-run noise.

**One warm-up cost, in the safe direction.** MSBuild regenerates
`obj/**/*.AssemblyInfo.cs` for every project, and it is a file the compiler is handed, so it
is an input. On a tree whose build output is stale, the first incremental run can therefore
report `its own inputs changed` for a project you did not touch and fall back to a full
rebuild. It converges: the run after it does not. It errs towards rebuilding, which is the
safe direction, and it says out loud that it did.

### `vela import`

Reads a `.scip` file produced by any language's SCIP indexer into the same database, so
`def`, `refs`, `outline` and `impact` answer across languages.

| Argument or option | Meaning |
|---|---|
| `<index>` | Path to the `.scip`. Looked for in the current directory first, then under the repository root, which is where `vela index` names a job's `.scip`. |
| `--solution <path>` | As for `vela index`. |
| `--replace` | Import over a previous import of the same `.scip`: delete the documents this index carries, with their occurrences, and write them again. Only the paths this `.scip` itself names are touched, whoever contributed them, and vela reports how many it replaced. |

`import` adds. `index` deletes and rebuilds. So the order is `vela index` and then
`vela import`, and a later `vela index` replays what was imported rather than losing it.

Importing into no index at all is legitimate: a repository with no .NET in it is still a
repository vela can answer about.

### `vela find`

Symbol search by name, over an FTS5 table. **This is the discovery verb and it is the one
that matches loosely**: it matches whole name tokens plus a prefix of the last one, so
`find Stat` finds `Status`, and `find tatus` finds nothing.

Answers with a plain list of symbol names and a count, not with locations.

### `vela def`

Where a symbol is defined: declaration site, one line per definition.

Always includes source-generated documents, marked `(generated)`. For some Razor page
members the generated document holds the only declaration there is, so excluding it would
leave `def` with nothing to say about a symbol vela can see perfectly well.

### `vela refs`

Every occurrence of a symbol, definitions included, grouped by file.

| Option | Meaning |
|---|---|
| `--include-generated` | Also report occurrences in source-generated code, which is compiled but not written to disk and so cannot be opened. |

Excludes generated documents by default, and always says how many it left out.

### `vela outline`

The definitions in one document, by path. Cheaper than reading the file.

The argument is a path relative to the repository root, matched exactly. Generated
documents are outlined like any other: the caller named a specific document, so refusing to
describe it would answer a question nobody asked.

`outline` never prints the ambiguity block, because its argument is a file path and every
file defines several symbols by nature.

### `vela impact`

Callers and blast radius. A reference to the target that falls inside another symbol's
recorded enclosing range is a call from it.

| Option | Meaning |
|---|---|
| `--include-generated` | As for `refs`. |

Only the innermost enclosing definition counts. Containment is tested on (line, character)
pairs rather than on lines, because C# permits several members on one line and generated
Razor emits a great deal of code that way.

**`impact` finds no caller for a reference that sits where no enclosing definition was
recorded.** Top level statements and Razor views are the normal cases. An empty `impact`
says so rather than implying nothing calls the symbol.

## Global options

`--solution <path>` is available on every verb. With no value, vela looks for exactly one
`.sln` in the current directory; zero or two or more is an error, and a `vela.json` may
name the solution instead.

`--help` and `--version` behave as usual. `--version` reports the package version and the
commit it was built from.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The question was answered, and the index behind the answer reports no problem. |
| `1` | The question could not be answered at all: no solution found, no index built yet, a `.scip` that is not there, a config that cannot be honoured, or an index whose schema version this build cannot read. |
| `3` | An answer was produced, and the index behind it is known to be missing code, out of date, or unverifiable. |

`3` is the interesting one. It is deliberately not `1`, because the answer above it is
real and usually useful; it is deliberately not `0`, because a script must be able to tell.
Every exit `3` prints the banner.

## How a symbol name is matched

`def`, `refs` and `impact` share one rule. `find` does not, and that is the only difference
between the discovery verb and the other three.

**A pattern matches a whole trailing run of a symbol's dotted segments, case-sensitively.**

- `Status` matches `App.Models.Perfume.Status`.
- `Status` does **not** match `HttpStatus`, `OrderStatus` or `status`.
- `Perfume.Status` matches the same symbol. So does the full name.
- `atus` matches nothing. A match always begins at a segment boundary.

**A method is reachable with or without its parameter list.** `Publish`,
`PerfumeService.Publish` and `Publish(App.Models.Perfume)` all reach the same method, so a
signature copied out of an answer still finds what the answer was about.

**A local or a parameter is reachable by its own name**, not by the name of the method or
type it is declared in. `refs PerfumeService` finds the type and its constructor, not the
variables that constructor is handed.

**A generic is reachable whatever it was constructed with.** `refs ILogger` finds every
`ILogger<T>`; `refs RunWithAuditAsync` finds every instantiation as well as the
declaration.

**A type argument is not an occurrence of the symbol it names.** `ILogger<PerfumeService>`
is an occurrence of `ILogger`. `PerfumeService` has its own occurrence, at its own
position.

Both of the last two rules cost real accuracy when they were missing, and both are
measured. See [How we know it is right](architecture.md#how-we-know-it-is-right).

### The ambiguity block

A bare name can still name more than one thing, and vela says so when it does.

```
'Perfume' is ambiguous: the 3104 result(s) above span 25 distinct symbols:
    1958  ScentVerdict.Data.Entities.Perfume
     384  ScentVerdict.Data.Enums.EntityType.Perfume
     ...
     144  (+15 further symbol(s))
To ask about one of them, give more of its name: 'Entities.Perfume' matches
ScentVerdict.Data.Entities.Perfume and none of the others.
```

Rules that hold every time:

- **Nothing is filtered to produce it.** The same results come back either way. It only
  says what they span.
- Most hits first, ties broken by name, so the ordering is identical on every machine.
- At most ten symbols are listed and the rest are summarised into one line, so the counts
  always add up to the reported total.
- `impact` labels its numbers differently, because its rows are callers rather than
  occurrences of the symbol asked about. It prints the block even when it named nobody.
- The block describes **the answer above it, not the index.** `refs` and `impact` leave
  generated code out by default, so a symbol of the same name that lives only there is in
  neither the results nor the count. If the answer also reports further results in
  generated code, ask again with `--include-generated` before treating the name as
  resolved.

**Never size a change from a total that carries the block.** Ask again with the longer name
it suggests.

Where an answer is all of one symbol but covers several stored names that differ only
inside their type arguments, vela says so in a sentence instead. That is either one generic
used several ways or overloads no pattern can select between, so there is nothing to narrow
to.

## Output format

Grouped by file, shaped for a context window rather than a terminal.

```
src/ScentVerdict.ServiceModel/Admin/CrudRetailerDtos.cs
     643:19   def  ScentVerdict.ServiceModel.Admin.AdminFeedSyncRunSummary.Status
src/ScentVerdict.Web/Pages/Admin/Commerce/RetailerDetail.cshtml
     222:55   ref  ScentVerdict.ServiceModel.Admin.AdminFeedSyncRunSummary.Status

2 result(s)
```

- Line and column are one-based, as every editor shows them.
- `def` or `ref` marks whether the occurrence is the definition.
- The last column is the full stored symbol name.
- Files are ordered ordinally and hits within a file by position, so the same index answers
  the same question identically on every machine.
- **Razor and Blazor hits are reported against the originating `.cshtml` or `.razor`**, not
  the generated code, so the location is one you can open and edit.
- A file whose hits are in source-generated code is marked `  (generated)` after its path,
  and one line explains what the marker means.

`find` answers differently: a bare list of symbol names, then a blank line, then
`N symbol(s)`.

## The banner

Printed above the results, to stderr for `index` and `import` and to stdout for the query
verbs, whenever the index cannot be vouched for. It always comes with exit code 3.

```
!! INCOMPLETE INDEX - these results may be missing references.
   stale index: 1 source file(s) changed after the index was built at 2026-07-30 09:31:16Z,
   most recently 'RazorDemo/Pages/Index.cshtml.cs' at 2026-07-30 09:31:34Z. Line numbers and
   references in this answer describe the code as it was, not as it is. Run vela index.
   Do not treat an empty or short result as proof the symbol is unused.
```

`vela index` and `vela import` print their own wording of the same thing:

```
!! The index is INCOMPLETE. <detail>
   Answers from it may be missing code. Do not treat an empty result as proof.
```

Every reason the banner fires:

| Detail begins | What happened |
|---|---|
| `load-failure:` | A project failed to load. |
| `compile-error:` | A project compiled with errors. Every reference that depended on a type the compiler could not resolve is simply absent. |
| `no-compilation:` | A project produced no compilation at all. |
| `outside-project-root:` | A document could not be represented because it lies outside the project root. |
| `stale index:` | A watched file under the repository root is newer than the index. |
| `index freshness could not be checked:` | The root the index was built against has moved, been renamed or been removed. |
| `index freshness could only be partly checked:` | A directory under the root could not be read. |
| `index has no health record` | The index does not record whether its own build succeeded. |
| `index health table holds N records` | The health table is in a state nothing in vela produces. |
| `imported from <path>:` | An imported `.scip` lost something, or has gone missing, or will not read. |
| `import-lost:` | A `.scip` that had been imported is not there any more, so its language is not in the rebuilt index. |
| `import-unreadable:` | A `.scip` that had been imported is still there and will not read. |
| `<path>: nothing has been imported from it` | A `vela.json` job whose indexer is not vela has not been satisfied. |

Details beyond ten are summarised as `(+N more)`, everywhere a detail is built.

## Lines that are not the banner

These are printed plainly, carry no `!!`, and never raise the exit code. Reporting them
through the banner would teach the reader to ignore the banner.

**Documents a package or the SDK contributed.**

```
1 document(s) contributed by a NuGet package or the .NET SDK were not indexed. They live in
the package cache or the .NET installation, not in this repository, so none of your code is
missing because of them. Run vela index --stats to list them.
```

A file vela cannot attribute to a package or the SDK is a different matter and goes to the
banner.

**Languages no job covers**, when a `vela.json` exists.

```
No job covers javascript 1 file(s), so none of it is in this index. Nothing of yours is
missing that a job asked for; this is what the repository holds beside it.
The exclude list kept this count out of 3 director(ies) and rejected 0 further file(s).
```

**Occurrences suppressed for living in generated code**, after `refs` or `impact`.

```
2 further result(s) in generated code, which is not on disk. Pass --include-generated to
see them.
```

**Imports that were replayed**, after a rebuild.

```
Replayed 1 imported .scip file(s) that the index this run replaced had been built from.
  web/index.scip: 2 document(s) and 17 occurrence(s).
```

A replayed file whose content hash has changed since it was imported says so, and the index
holds what is in it now.

**Notes from an import.** Each of these is a property of the `.scip` that was read, not a
fault in it:

- documents that count character offsets in something other than UTF-16 code units and
  whose text could not be found, so the offsets were stored unconverted
- documents that declare no position encoding, read as UTF-16 code units, which is what
  every other row in the index means
- occurrences carrying no symbol at all, which SCIP permits, so they are in the index and
  cannot be found by name
- names reached from more than one distinct SCIP symbol, so a query for one answers for
  both, named individually up to ten
- symbols that do not fit the grammar in `scip.proto`, stored under the symbol itself

## Empty answers

**No verb prints a bare zero.** Every one of them says which absence it is, because
"nothing uses this" and "I could not see the code that uses it" are the same output
otherwise, and an agent handed the first deletes the symbol.

| Verb | Absences it distinguishes |
|---|---|
| `find` | the index holds no symbols at all; the index holds nothing of that name |
| `def` | nothing of that name is indexed; something is, and it is defined outside this solution |
| `refs` | nothing of that name is indexed; every occurrence is in generated code |
| `outline` | the document is indexed and defines nothing; no document of that path is indexed |
| `impact` | every caller is in generated code; nothing of that name is indexed; the symbol is known only from generated code; nothing refers to it; references exist but fall inside no recorded enclosing range |

The rule that matters most: **an empty result is not proof that nothing uses the symbol.**

## Where the index lives

`$XDG_CACHE_HOME/vela/<SolutionName>-<hash>.db`, or `~/.cache/vela/...` when
`XDG_CACHE_HOME` is unset. The hash is the first 16 hex characters of the SHA-256 of the
absolute solution path, so two checkouts of the same repository have separate indexes.

vela refuses to run if that directory resolves to somewhere inside the solution's own tree.
Indexing must never write into the repository being indexed.

The index carries a schema version (currently 9). If you upgrade vela and the shape has
changed, every verb refuses to answer and tells you to re-index rather than querying a
database it cannot read. The index is a cache, so it is rebuilt rather than migrated.

### The repository root

Paths in every answer, and the path `outline` expects, are relative to the **repository
root**: the nearest ancestor directory of the solution holding a `.git` entry, or the
solution's own directory when there is none. The walk looks for `.git` directly rather than
asking git, so the answer cannot be changed by a git configuration vela did not set, and a
`.git` file (a linked worktree) counts as well as a `.git` directory.

That is what the index is rooted at, so a `repo/src/App.sln` layout still covers
`repo/tests/`.

## Freshness

The index is a snapshot. Every query compares its build time against the modification times
of the files it watches under the repository root, and if one of them is newer, every verb
says so and exits 3.

Watched extensions: `.cs`, `.vb`, `.cshtml`, `.razor`, `.csproj`, `.vbproj`, `.sln`,
`.slnx`, `.props`, `.targets`.

Never descended into: `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules`, and the cache
directory the index lives in.

**The watched set is deliberately a proper subset of the indexed set, so the absence of a
banner is not proof the tree is unchanged.** A checked-in generated artefact with another
extension, a source file that only exists under an excluded directory, or a
`Directory.Build.props` inside `obj` are all invisible to the check. Walking everything cost
more than the queries did, and watching `bin` and `.git` would leave every query permanently
degraded, which is a warning nobody reads.

It is timestamps only. No file is read and nothing is hashed, so the check cannot say
whether the symbol you asked about was the one that changed.

## vela.json

Optional. With no `vela.json`, vela indexes the C# and Razor the solution compiles and says
nothing about anything else, exactly as it always has.

```json
{
  "version": 1,
  "solution": "ScentVerdict.sln",
  "jobs": [
    { "language": "csharp", "indexer": "vela", "root": "." },
    { "language": "razor", "indexer": "vela", "root": "." },
    { "language": "typescript", "indexer": "scip-typescript", "root": "src/ScentVerdict.Mobile" },
    { "language": "javascript", "indexer": "scip-typescript", "root": "src/ScentVerdict.Web",
      "include": ["wwwroot/js/**/*.js"] }
  ],
  "exclude": ["**/venv/", "**/site-packages/", "src/ScentVerdict.Web/wwwroot/app/"]
}
```

It is looked for from the solution's own directory upwards, bounded by the repository root.

### Top-level properties

| Property | Type | Meaning |
|---|---|---|
| `$schema` | string | Ignored by vela, there so an editor can offer completion. |
| `version` | integer | Must be `1`. A later version is refused rather than half-honoured. |
| `solution` | string | Which `.sln` this repository means, so `--solution` need not be repeated. |
| `jobs` | array | See below. Omitting it keeps the default csharp and razor jobs. |
| `exclude` | array of glob patterns | **Appended** to the defaults, so a repository states only what is different about it. |

**An unknown property is refused by name rather than ignored.** `excludes` for `exclude`
would quietly index a vendored tree, and `jobbs` for `jobs` would quietly lose a language.

### Job properties

| Property | Default | Meaning |
|---|---|---|
| `language` | required | For a `vela` job, one of `csharp`, `vb`, `razor`. For any other indexer it is free text, but use the name vela's own census uses or the "no job covers" line will not line up with it: `typescript`, `javascript`, `python`, `go`, `rust`, `java`, `kotlin`, `scala`, `ruby`, `php`, `dart`, `sql`, `c`, `cpp`, `vue`, `svelte`, `astro`, `mdx`. |
| `indexer` | required | `vela`, or the name of the tool that produces the `.scip`. |
| `root` | `.` | The directory the job covers. |
| `index` | `index.scip` | Where the job's `.scip` is expected, relative to `root`. |
| `include` | all | Gitignore-style patterns scoping what the job claims. |
| `exclude` | none | Gitignore-style patterns scoping what the job claims. |

Rules vela enforces, each with its own message:

- A job with no `language` is refused. Language is never guessed from a file's contents.
- `vela` produces `csharp`, `vb` and `razor` and nothing else.
- A `vela` job is rooted at `.` and nothing else, because vela indexes whatever the solution
  compiles.
- A `vela` job cannot carry `include` or `exclude`: what is in the compilation is what is in
  the index.
- The same `language`, `indexer` and `root` cannot appear twice, because two identical jobs
  cannot be told apart.
- Every problem in the file is reported in one pass, and nothing is read or written until
  the file is honoured.

**A job vela cannot run degrades the index until it is imported.** vela does not run other
indexers. Since `vela index` rebuilds from nothing, every such job starts out unsatisfied,
so `vela index` names it, exits 3, and every answer carries the banner until that exact file
is imported. Nothing clears a job but a clean import of the file it named. A config file
must never become a quiet way to lose a language.

### Glob rules

Gitignore's, because that is the dialect a developer already knows.

- A pattern with no slash matches a file name at any depth: `*.min.js`.
- A pattern with a slash is anchored to the root: `src/Web/wwwroot/app/`.
- `**/` means any depth, including none.
- `*` and `?` never cross a `/`. `**` is the only thing that does.
- A trailing `/` means a directory and everything under it.
- `!` takes an earlier pattern back. `"!**/dist/"` is how a repository takes a default back.
- The last matching pattern decides.
- Matching is case-sensitive, and `\` in a path reads as `/`.
- **A negation cannot re-include a file whose parent directory is excluded.** This is the
  rule people trip over, and vela follows it rather than smoothing it away.

Writing a directory as `**/node_modules/` rather than `**/node_modules/**` lets the walk
skip the subtree unread. Both exclude the same files.

### Default excludes

```
**/.git/  **/.hg/  **/.svn/  **/.vs/  **/.idea/
**/bin/  **/obj/
**/node_modules/  **/bower_components/  **/wwwroot/lib/
**/venv/  **/.venv/  **/site-packages/  **/__pycache__/  **/.tox/
**/vendor/  **/Pods/
**/dist/  **/out/  **/.next/  **/.nuxt/  **/.svelte-kit/  **/target/  **/coverage/  **/.gradle/
**/*.min.js  **/*.min.css  **/*.map
```

**The excludes govern what vela says about a repository, never what the compiler saw.** What
Roslyn compiles is what gets indexed, so a generated file under `obj` that the compiler was
handed is in the index whatever this file says. That is the only reading under which adding
a config cannot silently shrink an existing index.

## Requirements

- The .NET SDK, 10.0 or newer.
- The solution must build. If a project fails to load or compiles with errors, vela says so,
  and the index is genuinely missing whatever depended on what the compiler could not
  resolve.
- Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into those
  compilations, which is how Razor Pages, MVC views and Blazor components arrive. F# has its
  own compiler and is out of scope.
- No network, no model calls, no API key, nothing resident between queries.

## What vela does not do

- It does not edit, refactor or rename. It reports.
- It does not do semantic or similarity search. The index is exact.
- It does not answer "what implements this interface". That is a SCIP relationship, and vela
  does not emit those yet.
- It does not run other languages' indexers. It imports their output.
- It is not a language server, not an MCP server, and not a daemon.
