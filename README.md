<div align="center">

<img src="assets/logo.svg" alt="vela skill for Claude Code, by DBHQ" width="420">

# vela

**Your codebase has 2,760 matches for `Name`. Twenty-three of them are the one you meant.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Claude Code](https://img.shields.io/badge/Claude_Code-Plugin-blueviolet)](https://code.claude.com/docs/en/plugins)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20macOS%20%7C%20Windows-lightgrey)]()

A free, open-source tool by [DBHQ](https://dbhq.uk)

</div>

---

Vela is the sail of Argo Navis - the largest constellation ever catalogued, later broken into Carina the keel, Puppis the stern, and Vela the sails. A whole decomposed into its named parts, which is what an index of a codebase is. The sails are also the part you navigate by.

vela builds a compiler-exact index of a .NET solution and answers questions about it in about a second: where is this symbol defined, everywhere it is used, who calls it, and what breaks if you change it. The answers come from Roslyn, so they are what the compiler believes, not what a regular expression matched.

## What makes it different

**It indexes Razor and Blazor. Nothing else does.** Razor views and Blazor components never exist as files the compiler reads - they arrive as *source-generated documents*. Every general-purpose code-intelligence tool iterates the files on disk and therefore skips them: CodeGraph (63k stars), codebase-memory-mcp (36k), Serena (27k), and even Sourcegraph's own Roslyn-based `scip-dotnet`. On one real solution that is 307 views and 59,000 lines of the presentation layer, invisible. vela reads the compilation instead of the directory, so they are simply there.

**It is deterministic, and only deterministic.** No model calls, no API key, no network. Every answer follows from the compiler's semantic model, so there is nothing to triage and nothing to second-guess.

**Nothing stays resident.** Index once, query a SQLite file. A language server held open costs about a gigabyte per project; vela costs a file on disk. This is the difference between a tool you leave installed and one you turn off.

**It tells you when it does not know.** A code-intelligence tool that silently returns partial results is worse than grep, because you believe it. If a project fails to load, every query that touches it says so and the exit code reflects it. Absence of results is never reported as evidence of absence.

**It does not replace grep.** For a distinctive identifier, `grep` returns twenty-four lines and needs no index at all. vela earns its keep on the ordinary names - `Name`, `Status`, `Value`, `Id`, `Update` - where grep is 88 to 98% noise, and on the questions grep cannot answer at any precision: which occurrence is the definition, which overload was meant, whether `@Model.Perfume` in a `.cshtml` binds to a particular property on a particular type, and where an inherited or extension member called as `x.Foo()` actually lives.

## Measured

On a 375,608-line C# solution with 307 Razor views:

| Symbol | Real references | `grep -w` hits | Precision |
|---|---|---|---|
| `Perfume.Status` | 23 | 1,430 | **1.6%** |
| `Perfume.Name` | 243 | 2,760 | 8.8% |
| `Brand.Name` | 324 | 2,760 | 11.7% |
| `PerfumeService` | - | 24 | grep is fine |

Query latency, several runs each, after the index is already built:

| Query | Results | Time |
|---|---|---|
| `def Perfume.Status` | 2 | ~0.45s |
| `refs Perfume` | 3,104 | ~1.5s |

Not milliseconds, and this README used to say it was. For comparison, loading the
same solution into a live Roslyn workspace costs 9.3s, plus 23.8s more to compile
the web project - every invocation, because nothing stays resident. vela pays a
cost like that once, at index time, and answers everything after it from a file.

## Install

### As a Claude Code plugin (recommended)

```
/plugin marketplace add dbhq-uk/marketplace
/plugin install vela@dbhq
```

### Local install (Claude Code or Codex)

```bash
git clone https://github.com/dbhq-uk/vela-skill.git
cd vela-skill
./install.sh          # Claude Code: symlinks into ~/.claude/skills (edits are live)
./install-codex.sh    # Codex: installs into ~/.codex/skills
```

Requires the .NET SDK, and the solution you are indexing must build.

## Use

```bash
vela index                        # build the index once
vela index --stats                # ... and report what is in it
vela outline Services/PerfumeService.cs
vela def    Perfume.Status
vela refs   Perfume.Status        # includes .cshtml and .razor
vela impact PerfumeService
vela find   Repository
```

`def`, `refs` and `impact` match a **whole dotted segment**, case-sensitively: `Status` finds `Perfume.Status` and does not find `HttpStatus` or `OrderStatus`. A method is reachable with or without its parameter list, and a local or a parameter is reachable by its own name rather than by the name of the method or type it is declared in, so `refs PerfumeService` finds the type and its constructor and not the three variables that constructor is handed. A generic type or method is reachable whatever it was constructed with, so `refs ILogger` finds every `ILogger<T>` and `refs RunWithAuditAsync` finds every instantiation of it, and a type argument is not counted as an occurrence of the symbol it names. `find` is the discovery verb and matches name tokens and a trailing prefix instead, so `find Stat` finds `Status`.

**A bare name can still name more than one thing, and vela says so when it does.** A whole-segment match finds every symbol whose last segment is that name, so `refs Perfume` finds the entity, the entity's constructor, an enum member called `Perfume` and a property of an unrelated response type. Every hit is real, but the one number at the bottom counts nothing that exists. `def`, `refs` and `impact` therefore print an ambiguity block naming each distinct symbol with its own count - most hits first, ties broken by name, so the ordering is the same on every machine - and suggest a longer name that resolves to one of them. Nothing is filtered to produce it: the same results come back either way, and the block only says what they span. Long tails are summarised into one line rather than listed, so the counts always add up to the reported total. The block describes the answer above it and not the whole index, which is why it says the results *span* so many symbols: `refs` and `impact` leave generated code out by default, so a symbol of the same name that lives only there is in neither the results nor the count. Its absence means the results above are occurrences of one symbol; if the answer also reports further results in generated code, ask again with `--include-generated` before treating that as the whole story.

`refs` and `impact` leave out source-generated code by default, because the Razor generator's output is compiled but never written to disk and those paths cannot be opened. They always say how much they left out, and `--include-generated` brings it back. `def` and `outline` always include it, marked `(generated)`.

Paths are relative to the **repository root** - the working tree the solution sits in, or the solution's own directory when it is in no repository. That is what the index is rooted at, so a `repo/src/App.sln` layout still covers `repo/tests/`, and it is what `outline` expects and what every answer prints.

The index is a snapshot. Every query compares its build time against the files under that same root that vela watches, and if one of them is newer, every verb says so and exits 3. The watched set is the sources vela indexes plus the files that decide what is compiled - `.cs`, `.vb`, `.cshtml`, `.razor`, `.csproj`, `.vbproj`, `.sln`, `.slnx`, `.props` and `.targets` - and it skips `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules` and the cache directory the index itself lives in. It is deliberately a subset of what is indexed, because walking everything cost more than the queries did. So a quiet answer means no watched file has changed; it is not proof that nothing has.

`vela index` may also print a plain line saying that some documents were not indexed, for example `1 document(s) contributed by a NuGet package or the .NET SDK were not indexed`. That is information, not a warning: those files live in the package cache or the .NET installation, none of your code is missing, and the exit code stays 0. `vela index --stats` lists them by path. A file that vela cannot attribute to a package or the SDK is a different matter and is reported as a gap, with the banner and exit 3.

## Configuration

**You do not need a config file.** With no `vela.json`, vela does exactly what it has always done: indexes the C# and Razor the solution compiles, and says nothing about anything else.

A `vela.json` at the solution root, or anywhere between it and the repository root, is how a polyglot repository declares what it wants indexed and what is not source code at all. It is JSON because that is the convention `global.json` and `dotnet-tools.json` established for .NET, and it is a jobs **array** rather than a flat language list because "which language" and "which indexer produced it" are separate facts, and one repository can need two jobs for one language at different roots.

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

That is the real configuration for the solution vela is measured on, and it is in the repository at [`docs/examples/scentverdict-vela.json`](docs/examples/scentverdict-vela.json).

**Why the excludes are the feature.** On that repository, a naive count by extension reports 5,775 Python files. 5,695 of them are vendored `venv` and `site-packages`. The honest first-party picture is 1,866 C#, 307 Razor, 151 JavaScript, 80 Python, 30 TypeScript, 40 SQL and 3 Java - and one directory of that repository, `src/ScentVerdict.Web/wwwroot/app/`, is gitignored build output of the mobile app holding 64 of its 81 JavaScript files. Indexing that would index the same code twice, once as readable source and once as minified bundles nobody can open, and a directory that appears and disappears between builds would make the index non-deterministic. So vela ships opinionated default excludes - build output, vendored dependencies, minified files - and the config is how a repository overrides them. `vela index` prints what the exclude list kept out and which languages no job covers, so the numbers can be checked rather than trusted.

**A job vela cannot run degrades the index until it is imported.** vela does not run other indexers; a job whose `indexer` is not `vela` declares where its `.scip` is expected to come from (`index` names the file, defaulting to `index.scip` under the job's `root`). Since `vela index` rebuilds the database from nothing, every such job starts out unsatisfied, so `vela index` names it, exits 3, and every answer carries the `!!` banner until that exact file is imported with `vela import`. A config file must never become a quiet way to lose a language. The same holds if the indexer is not installed, if the job's root does not exist, or if the indexer runs and fails: nothing clears a job but a clean import of the file it named.

**Globs follow gitignore**, because that is the dialect a developer already knows: a pattern with no slash matches a file name at any depth, a pattern with a slash is anchored to the root, `**/` means any depth, `*` and `?` never cross a `/`, a trailing `/` means a directory and everything under it, `!` takes an earlier pattern back, the last matching pattern decides, and matching is case-sensitive. Including the rule people trip over: **a negation cannot re-include a file whose parent directory is excluded.** A config's `exclude` list is *appended* to the defaults, so a repository states only what is different about it, and `"!**/dist/"` is how it takes a default back. Writing a directory as `**/node_modules/` rather than `**/node_modules/**` lets the walk skip the subtree unread; both exclude the same files.

The excludes govern what vela *says* about a repository, never what the compiler saw. What Roslyn compiles is what gets indexed, so a generated file under `obj` that the compiler was handed is in the index whatever this file says - which is the only reading under which adding a config cannot silently shrink an existing index.

`version`, `solution`, `jobs`, `exclude` and `$schema` are the only properties, and an unknown one is refused by name rather than ignored: `excludes` for `exclude` would quietly index the vendored tree, and `jobbs` for `jobs` would quietly lose a language.

## Scope

Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into those compilations - which is how Razor Pages, MVC views and Blazor components arrive. Those all arrive as generated C# whatever the host project's language. F# has its own compiler and is out of scope.

vela emits and reads [SCIP](https://github.com/scip-code/scip), so an index another language's indexer produced - `scip-typescript`, `scip-python` and friends - can be merged into the same database with `vela import` and queried by the same verbs. vela does not run those indexers: `vela.json` declares which ones a repository expects, and `vela index` reports any whose output has not been imported yet.

## Documentation

- [Design notes](docs/design-notes.md) - why the tool is shaped this way, with the measurements behind it

## Licence

MIT
