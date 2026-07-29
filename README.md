<div align="center">

# vela

**Your codebase has 2,760 matches for `Name`. Twenty-three of them are the one you meant.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Claude Code](https://img.shields.io/badge/Claude_Code-Plugin-blueviolet)](https://code.claude.com/docs/en/plugins)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20macOS%20%7C%20Windows-lightgrey)]()

A free, open-source tool by [DBHQ](https://dbhq.uk)

</div>

---

Vela is the sail of Argo Navis - the largest constellation ever catalogued, later broken into Carina the keel, Puppis the stern, and Vela the sails. A whole decomposed into its named parts, which is what an index of a codebase is. The sails are also the part you navigate by.

vela builds a compiler-exact index of a .NET solution and answers questions about it in milliseconds: where is this symbol defined, everywhere it is used, who calls it, and what breaks if you change it. The answers come from Roslyn, so they are what the compiler believes, not what a regular expression matched.

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

`def`, `refs` and `impact` match a **whole dotted segment**, case-sensitively: `Status` finds `Perfume.Status` and does not find `HttpStatus` or `OrderStatus`. A method is reachable with or without its parameter list, and a local or a parameter is reachable by its own name rather than by the name of the method or type it is declared in, so `refs PerfumeService` finds the type and its constructor and not the three variables that constructor is handed. `find` is the discovery verb and matches name tokens and a trailing prefix instead, so `find Stat` finds `Status`.

**A bare name can still name more than one thing, and vela says so when it does.** A whole-segment match finds every symbol whose last segment is that name, so `refs Perfume` finds the entity, the entity's constructor, an enum member called `Perfume` and a property of an unrelated response type. Every hit is real, but the one number at the bottom counts nothing that exists. `def`, `refs` and `impact` therefore print an ambiguity block naming each distinct symbol with its own count - most hits first, ties broken by name, so the ordering is the same on every machine - and suggest a longer name that resolves to one of them. Nothing is filtered to produce it: the same results come back either way, and the block only says what they span. Long tails are summarised into one line rather than listed, so the counts always add up to the reported total. The block describes the answer above it and not the whole index, which is why it says the results *span* so many symbols: `refs` and `impact` leave generated code out by default, so a symbol of the same name that lives only there is in neither the results nor the count. Its absence means the results above are occurrences of one symbol; if the answer also reports further results in generated code, ask again with `--include-generated` before treating that as the whole story.

`refs` and `impact` leave out source-generated code by default, because the Razor generator's output is compiled but never written to disk and those paths cannot be opened. They always say how much they left out, and `--include-generated` brings it back. `def` and `outline` always include it, marked `(generated)`.

Paths are relative to the **repository root** - the working tree the solution sits in, or the solution's own directory when it is in no repository. That is what the index is rooted at, so a `repo/src/App.sln` layout still covers `repo/tests/`, and it is what `outline` expects and what every answer prints.

The index is a snapshot. If anything under that same root has changed since it was built, every verb says so and exits 3.

`vela index` may also print a plain line saying that some documents were not indexed, for example `1 document(s) contributed by a NuGet package or the .NET SDK were not indexed`. That is information, not a warning: those files live in the package cache or the .NET installation, none of your code is missing, and the exit code stays 0. `vela index --stats` lists them by path. A file that vela cannot attribute to a package or the SDK is a different matter and is reported as a gap, with the banner and exit 3.

## Scope

Roslyn covers **C# and Visual Basic**, plus anything a source generator emits into those compilations - which is how Razor Pages, MVC views and Blazor components arrive. Those all arrive as generated C# whatever the host project's language. F# has its own compiler and is out of scope.

vela emits [SCIP](https://github.com/scip-code/scip). Reading indexes produced by other languages' indexers - `scip-typescript`, `scip-python` and friends - is the reason for that choice, but it is a design intent rather than a shipped feature: there is no `.scip` import path yet, and today the index is built from Roslyn only.

## Documentation

- [Design notes](docs/design-notes.md) - why the tool is shaped this way, with the measurements behind it

## Licence

MIT
