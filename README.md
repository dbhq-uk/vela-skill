<div align="center">

<img src="assets/logo.svg" alt="vela skill for Claude Code, by DBHQ" width="420">

# vela

**Your codebase has 2,257 lines matching `Status`. Twenty-four of them are the property you meant.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Claude Code](https://img.shields.io/badge/Claude_Code-Plugin-blueviolet)](https://code.claude.com/docs/en/plugins)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20macOS%20%7C%20Windows-lightgrey)]()

A free, open-source tool by [DBHQ](https://dbhq.uk)

</div>

---

vela builds a compiler-exact index of a .NET solution and answers questions about it in
about a second: where is this symbol defined, everywhere it is used, who calls it, and what
breaks if you change it. The answers come from Roslyn, so they are what the compiler
believes, not what a regular expression matched.

## The problem

An agent working in a .NET repository discovers structure by grepping. For distinctive
identifiers that is fine. For the ordinary ones it is close to useless, and the failure is
quiet: a plausible-looking answer that is mostly noise, or a missed call site and the
conclusion that a symbol is unused. And nothing on the market can see inside a Razor view
at all.

## Why nothing else solves it

**It indexes Razor and Blazor. Nothing else does.** Razor views and Blazor components never
exist as files the compiler reads. They arrive as *source-generated documents*. Every
general-purpose code-intelligence tool iterates the files on disk and therefore skips them:
CodeGraph (63k stars), codebase-memory-mcp (36k), Serena (27k), and even Sourcegraph's own
Roslyn-based `scip-dotnet`. On ScentVerdict, the real ten-project solution vela is developed
against, that was 334 views and 62,358 lines of the presentation layer on 30 July 2026,
invisible. vela reads the compilation instead of the directory, so they are simply there.

**It is deterministic, and only deterministic.** No model calls, no API key, no network.
Every answer follows from the compiler's semantic model, so there is nothing to triage.

**Nothing stays resident.** Index once, query a SQLite file. A language server held open
costs about a gigabyte per project; vela costs a file on disk.

**It tells you when it does not know.** A tool that silently returns partial results is
worse than grep, because you believe it. If a project fails to load, every query that
touches it says so and the exit code reflects it. Absence of results is never reported as
evidence of absence.

**It does not replace grep.** For a distinctive identifier grep returns a screenful and
needs no index. vela earns its keep on the ordinary names and on the questions grep cannot
answer at any precision.

## Proof

Measured on 30 July 2026 against ScentVerdict, a real ten-project solution of 388,323 lines
of C# with 334 Razor views (62,358 lines). `grep -w` counts lines, vela counts occurrences,
over the same `.cs` and `.cshtml` files. It is a live repository, so re-measure rather than
trust these: the commands are in [the querying guide](docs/guides/querying.md).

| Question | vela | `grep -w` | Precision |
|---|---|---|---|
| `refs Entities.Perfume.Status` | 24 | 2,257 for `Status` | 1.1% |
| `refs Entities.Perfume.Name` | 248 | 3,780 for `Name` | 6.6% |
| `refs Brand.Name` | 326 | 3,780 for `Name` | 8.6% |
| `refs PerfumeService` | 7 | 33 | grep is fine |

Coverage on that solution on the same day: **334 of 334 `.cshtml` indexed**, 2,675
documents, 979,906 occurrences, 142,532 definitions. Indexing took about five minutes
(4m55s and 5m12s on two runs) at 2.1GB peak.

Query cost once the index exists: a 0.09s process floor, about 0.55s for a `def`, about
1.3s for a `refs` returning 3,156 results. Not milliseconds, and this README used to say it
was. For comparison, loading the same solution into a live Roslyn workspace costs 9.3s plus
23.8s to compile the web project, and it costs that **on every invocation**, because nothing
stays resident.

**Polyglot, proved not promised.** A real `scip-typescript` 0.4.0 index over four
TypeScript files imports beside the C# index, and both answer from one database. 420 tests,
all hermetic.

## Upstream

`scip-dotnet` cannot see Razor. We wrote the fix, in their code and their style, and
[opened it](https://github.com/sourcegraph/scip-dotnet/pull/117).

- **PR:** [sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117),
  "Index Razor views and Blazor components"
- **Fork:** [dbhq-uk/scip-dotnet](https://github.com/dbhq-uk/scip-dotnet)
- **The issue it closes:**
  [#61](https://github.com/sourcegraph/scip-dotnet/issues/61), closed as *not planned*,
  where a maintainer wrote "We'll be happy to review a PR adding this feature"

Measured against their `main` at `4788446`: `dotnet new webapp` goes from 0 to 6 `.cshtml`
documents, `dotnet new blazor` from 0 to 11 `.razor`, and their existing snapshots stay
byte-identical on net8.0, net9.0 and net10.0. We would rather the ecosystem gained Razor
support than that we kept it. The write-up is in
[docs/upstream/scip-dotnet-razor.md](docs/upstream/scip-dotnet-razor.md).

## Install

As a Claude Code plugin:

```
/plugin marketplace add dbhq-uk/marketplace
/plugin install vela@dbhq
```

Or into any agent - Cursor, Copilot, Windsurf, Gemini, Cline and more - via the [skills.sh](https://skills.sh) CLI:

```bash
npx skills add dbhq-uk/vela-skill
```

Or locally, for Claude Code or Codex:

```bash
git clone https://github.com/dbhq-uk/vela-skill.git
cd vela-skill
./install.sh          # Claude Code: symlinks into ~/.claude/skills (edits are live)
./install-codex.sh    # Codex: installs into ~/.codex/skills
```

Requires the .NET SDK 10.0 or newer, and the solution you are indexing must build.

## First use, in sixty seconds

```bash
mkdir demo && cd demo
dotnet new webapp -n RazorDemo -o RazorDemo
dotnet new sln -n RazorDemo --format sln
dotnet sln RazorDemo.sln add RazorDemo/RazorDemo.csproj

vela index --stats
vela refs ShowRequestId
```

```
RazorDemo/Pages/Error.cshtml
      10:12   ref  RazorDemo.Pages.ErrorModel.ShowRequestId
RazorDemo/Pages/Error.cshtml.cs
      13:17   def  RazorDemo.Pages.ErrorModel.ShowRequestId

2 result(s)
```

That first line is a reference inside a Razor view, bound to a specific property on a
specific type, at a line and column you can open. `scip-dotnet` indexes this same app and
finds zero `.cshtml` documents. The whole tutorial is
[docs/getting-started.md](docs/getting-started.md).

## The verbs

```bash
vela index                        # build the index once
vela index --stats                # ... and report what is in it
vela import other-language.scip   # merge in another indexer's output
vela outline Services/PerfumeService.cs
vela def    Perfume.Status
vela refs   Perfume.Status        # includes .cshtml and .razor
vela impact PerfumeService
vela find   Repository
vela cache                        # what the index cache holds, and how to clear it
```

`def`, `refs` and `impact` match a **whole dotted segment**, case-sensitively: `Status`
finds `Perfume.Status` and not `HttpStatus`. When a bare name really does span several
symbols, vela says so and suggests a longer name. `find` matches a trailing prefix instead,
so `find Stat` finds `Status`. Full rules, every flag and every exit code:
[docs/reference.md](docs/reference.md).

Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into those
compilations. F# has its own compiler and is out of scope. Everything else reaches the index
through `vela import`. vela does not edit, refactor or rename. It reports.

## Documentation

**[The documentation index](docs/README.md)** reaches everything. Start with
[getting started](docs/getting-started.md) to learn it,
[answering real questions](docs/guides/querying.md) to use it,
[the reference](docs/reference.md) to look something up, and
[architecture](docs/architecture.md) to understand it. There are also guides for
[other languages](docs/guides/multi-language.md) and [CI](docs/guides/ci.md), a catalogue of
[every other SCIP indexer](docs/scip-ecosystem.md), and the original
[design notes](docs/design-notes.md).

## Etymology

Vela is the sail of Argo Navis, the largest constellation ever catalogued, later broken into
Carina the keel, Puppis the stern, and Vela the sails: a whole decomposed into its named
parts, which is what an index of a codebase is. The sails are also the part you navigate by.

## Licence

MIT
