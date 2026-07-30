# AGENTS.md

Guidance for AI agents (and people) working in this repository.

## What this is

**vela** - compiler-exact code search for .NET, for AI coding agents. It follows the [Agent Skills](https://agentskills.io) layout (`skills/<name>/SKILL.md`) and ships as a [Claude Code plugin](https://code.claude.com/docs/en/plugins).

## Layout

```
.claude-plugin/plugin.json     # plugin manifest
skills/vela/SKILL.md           # the skill (agent-facing instructions)
src/Vela/                      # the CLI: Config, Harvest, Indexing, Query, Scip
tests/Vela.Tests/              # 355 tests, hermetic
install.sh / install-codex.sh  # local installers (Claude / Codex)
docs/                          # see docs/README.md for the index
```

Documentation follows [Diataxis](https://diataxis.fr). A page is a tutorial
(`docs/getting-started.md`), a how-to guide (`docs/guides/`), reference
(`docs/reference.md`, `docs/scip-ecosystem.md`) or explanation (`docs/architecture.md`,
`docs/design-notes.md`). Mixing modes on one page is the standard failure. Anything new
goes in [docs/README.md](docs/README.md) so every page stays reachable from one place, and
the README stays a shop window rather than a manual.

## The three constraints that define this tool

Break any of these and it stops being the thing people can trust:

1. **Deterministic only.** Every answer follows from Roslyn's semantic model. No model calls, no network, no telemetry, no heuristic ranking. A finding is what the compiler believes, or it is not a finding.

2. **Never write to the indexed repository.** The index lives outside the source tree. vela reads; it does not modify. Indexing someone's repository must leave it byte-identical.

3. **An incomplete index must never look like a complete one.** This matters more here than in most tools. If a project fails to load, every query touching it must say so and the exit code must reflect it. An agent that receives an empty reference list will conclude the symbol is unused and delete it. Absence of results is never evidence of absence - report the gap loudly or do not answer.

## Why Razor works here and nowhere else

Razor views and Blazor components never exist as files Roslyn reads from disk. The Razor source generator emits them into the compilation. Tools that iterate `project.Documents` see on-disk files only and miss every one of them - that is the single line that makes Sourcegraph's `scip-dotnet` Razor-blind (`ScipProjectIndexer.cs:110`).

vela iterates the **compilation's syntax trees**, which include source-generated documents, and maps locations back through their `#line` directives to the originating `.cshtml` or `.razor`.

If you are changing the harvester, this is the property to protect. A regression here is silent: the index still builds, queries still answer, and the Razor half of the codebase quietly disappears. Tests must assert generated-document coverage explicitly, by count.

The fix for `scip-dotnet` is written and open as [sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117), from the fork at [dbhq-uk/scip-dotnet](https://github.com/dbhq-uk/scip-dotnet). Full write-up: [docs/upstream/scip-dotnet-razor.md](docs/upstream/scip-dotnet-razor.md).

## Conventions

- House style: **British English, plain hyphens** (no em or en dashes).
- The tool emits and reads [SCIP](https://github.com/scip-code/scip). Deviating from the format costs interoperability with every other language's indexer, so extend it rather than fork it. `vela import` reads a `.scip` from any indexer into the same database, proven against a real `scip-typescript` 0.4.0 index. vela does not run other indexers.
- Roslyn covers C# and Visual Basic only. `LanguageNames` carries an `FSharp` constant with no implementation behind it - do not be misled by it. Both languages are handled in the harvester (reference folding and declaration anchoring), and the VB path is exercised against a synthetic VB compilation rather than a full MSBuild-loaded `.vbproj`. Razor Pages, MVC views and Blazor components arrive as generated **C#** whatever the host project's language.
- **The tool** makes no network calls: no model calls, no telemetry, nothing resident. **The test fixtures** are a different matter - they run `dotnet new webapp`, `dotnet new blazor` and `dotnet restore` to scaffold real projects in temp directories, so a cold NuGet cache means the first run needs network access. Nothing outside the temp directory is touched.

## Validating a change

Coverage assertions that must hold on the fixture solution:

```bash
vela index --stats
```

On a scaffolded Razor Pages app (`dotnet new webapp`) that prints:

```
documents            : 23
  generated          : 8   (compiled, not on disk)
  razor views        : 7   (.cshtml and .razor)
occurrences          : 2670
  in razor views     : 22
  definitions        : 182
```

The `razor views` count must equal the number of `.cshtml` files on disk, and `in razor views` must be non-zero - seven empty Razor documents would satisfy the first count and mean the mapping has collapsed. `EndToEndTests.IndexWithStats_ReportsTheCoverageThatMustNotRegress` asserts both by count.

And the suite, which must stay green:

```bash
dotnet test          # 355 passed, 0 failed
```
