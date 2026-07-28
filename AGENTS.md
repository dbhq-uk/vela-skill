# AGENTS.md

Guidance for AI agents (and people) working in this repository.

## What this is

**vela** - compiler-exact code search for .NET, for AI coding agents. It follows the [Agent Skills](https://agentskills.io) layout (`skills/<name>/SKILL.md`) and ships as a [Claude Code plugin](https://code.claude.com/docs/en/plugins).

## Layout

```
.claude-plugin/plugin.json     # plugin manifest
skills/vela/SKILL.md           # the skill (agent-facing instructions)
skills/vela/scripts/           # CLI entrypoint and helpers
skills/vela/references/        # verb reference, output formats
skills/vela/tests/             # offline, hermetic
install.sh / install-codex.sh  # local symlink installers (Claude / Codex)
docs/design-notes.md           # why the tool is shaped this way, with measurements
```

## The three constraints that define this tool

Break any of these and it stops being the thing people can trust:

1. **Deterministic only.** Every answer follows from Roslyn's semantic model. No model calls, no network, no telemetry, no heuristic ranking. A finding is what the compiler believes, or it is not a finding.

2. **Never write to the indexed repository.** The index lives outside the source tree. vela reads; it does not modify. Indexing someone's repository must leave it byte-identical.

3. **An incomplete index must never look like a complete one.** This matters more here than in most tools. If a project fails to load, every query touching it must say so and the exit code must reflect it. An agent that receives an empty reference list will conclude the symbol is unused and delete it. Absence of results is never evidence of absence - report the gap loudly or do not answer.

## Why Razor works here and nowhere else

Razor views and Blazor components never exist as files Roslyn reads from disk. The Razor source generator emits them into the compilation. Tools that iterate `project.Documents` see on-disk files only and miss every one of them - that is the single line that makes Sourcegraph's `scip-dotnet` Razor-blind (`ScipProjectIndexer.cs:110`).

vela iterates the **compilation's syntax trees**, which include source-generated documents, and maps locations back through their `#line` directives to the originating `.cshtml` or `.razor`.

If you are changing the harvester, this is the property to protect. A regression here is silent: the index still builds, queries still answer, and the Razor half of the codebase quietly disappears. Tests must assert generated-document coverage explicitly, by count.

## Conventions

- House style: **British English, plain hyphens** (no em or en dashes).
- The tool emits [SCIP](https://github.com/scip-code/scip). Deviating from the format costs interoperability with every other language's indexer, so extend it rather than fork it.
- Roslyn covers C# and Visual Basic only. `LanguageNames` carries an `FSharp` constant with no implementation behind it - do not be misled by it.
- Tests are hermetic: no network, and they build throwaway solutions in temp directories rather than touching anything real.

## Validating a change

Coverage assertions that must hold on the fixture solution:

```bash
vela index --stats     # generated-document count must be non-zero for Razor projects
```
