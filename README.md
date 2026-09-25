<div align="center">

<img src="assets/logo.svg" alt="vela skill for Claude Code, by DBHQ" width="560">

# vela

**Your codebase has 2,257 lines matching `Status`. Twenty-four of them are the property you meant.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Claude Code](https://img.shields.io/badge/Claude_Code-Plugin-blueviolet)](https://code.claude.com/docs/en/plugins)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20macOS%20%7C%20Windows-lightgrey)]()

A free, open-source tool by [DBHQ](https://dbhq.uk) - documented at [skills.dbhq.uk](https://skills.dbhq.uk/vela/)

</div>

---

vela builds a compiler-exact index and answers questions about it in about a second: where
is this symbol defined, everywhere it is used, who calls it, and what breaks if you change
it. For .NET the answers come from Roslyn, so they are what the compiler believes rather
than what a regular expression matched.

**It speaks [SCIP](https://github.com/scip-code/scip), which is what makes it a
whole-repository tool rather than a .NET one.** vela indexes C#, Visual Basic, Razor Pages,
MVC views and Blazor components itself; every other language reaches the same database by
importing the `.scip` file its own indexer produces, and then the same verbs answer over
both. It does not run those indexers - you run them, vela imports the result.

## What makes it different

An agent working in a .NET repository discovers structure by grepping. For distinctive
identifiers that is fine. For the ordinary ones it is close to useless, and the failure is
quiet: a plausible-looking answer that is mostly noise, or a missed call site and the
conclusion that a symbol is unused.

And grep cannot tell you what a name in a Razor view binds to.

## Why it sees what other tools miss

**It writes compiler-resolved Razor and Blazor references into a saved index.** We know of
no other tool that does. Razor views and Blazor components never exist as files the
compiler reads. They arrive as *source-generated documents*, so a tool that iterates the
files on disk skips them, and Sourcegraph's own Roslyn-based `scip-dotnet` is one of those.
On ScentVerdict, the real ten-project solution vela is developed against, such a tool
missed 334 views and 62,358 lines of the presentation layer on 30 July 2026. vela reads
the compilation instead of the directory, so they are simply there.

Some tools do reach Razor, in other ways. csharp-ls resolves `.cshtml` references through
the compiler behind its `--features razor-support` flag, and the Roslyn language server has
Razor cohosting from 5.8, but both answer from a language server that has to be running.
CodeGraph has a Razor extractor that works by pattern rather than through the compiler.
vela's answer is on disk before the question is asked, and any agent can query it with no
server running.

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
was.

For comparison, loading the same solution into a live Roslyn workspace costs 9.3s plus
23.8s to compile the web project, and it costs that **on every invocation**, because nothing
stays resident.

**Polyglot, proved not promised.** A real `scip-typescript` 0.4.0 index over four
TypeScript files imports beside the C# index, and both answer from one database. 426 tests,
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

**The skill and the `vela` command install separately.** The plugin and skills.sh
installs below copy the skill only. They do not build or install the command. Install the
command with the [local install](#local-install-claude-code-or-codex), which does both.
The skill checks for the command before its first step and says what is missing.

### As a Claude Code plugin (recommended)

```
/plugin marketplace add dbhq-uk/marketplace
/plugin install vela@dbhq
```

This installs the skill only. The `vela` command needs the local install as well.

### Any agent (Cursor, Copilot, Windsurf, Gemini, Cline and more)

```bash
npx skills add dbhq-uk/vela-skill
```

The [skills.sh](https://skills.sh) CLI installs into whichever agent directories
it finds, so this works outside Claude Code and Codex too. It installs the skill only.
The `vela` command needs the local install as well.

### Local install (Claude Code or Codex)

```bash
git clone https://github.com/dbhq-uk/vela-skill.git
cd vela-skill
./install.sh          # Claude Code: symlinks into ~/.claude/skills (edits are live)
./install-codex.sh    # Codex: installs into ~/.codex/skills
```

[`install.sh`](install.sh) and [`install-codex.sh`](install-codex.sh) are the
same install two ways: Claude Code substitutes `${CLAUDE_SKILL_DIR}`, so the
whole skill directory is symlinked untouched, while Codex does not, so its
`SKILL.md` is rewritten at install time. Re-run the Codex one after editing
`SKILL.md`.

**To upgrade, pull and run the installer again.** Every build gets a version of its
own, such as `1.1.0-dev.1790325656`, where the suffix is the commit's time, so the new
build always replaces the installed one. `vela --version` shows the version and the
commit it was built from.

## Requirements

**The .NET SDK 10**, and `~/.dotnet/tools` on your `PATH` - `install.sh`
warns and prints the export line if it is not, because the `vela` command
will not run until it is.

Nothing else for .NET code. **For any other language you need that
language's own SCIP indexer**, which vela does not run and does not
install: you produce the `.scip` file, vela imports it, and the same verbs
then answer over both.

## First use, in sixty seconds

```bash
mkdir demo && cd demo
dotnet new webapp -n RazorDemo -o RazorDemo
dotnet new sln -n RazorDemo
dotnet sln add RazorDemo/RazorDemo.csproj

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

## Also from DBHQ

Every DBHQ agent skill is free, open source and installable from the same
marketplace, and all of them are documented at
**[skills.dbhq.uk](https://skills.dbhq.uk)**. The marketplace itself is
[dbhq-uk/marketplace](https://github.com/dbhq-uk/marketplace) - one
`/plugin marketplace add` and every one of them is available.

| Skill | What it does |
|---|---|
| [outlook](https://skills.dbhq.uk/outlook/) | Microsoft 365 mail and calendar, from the terminal |
| [trello](https://skills.dbhq.uk/trello/) | Your boards, run from your agent |
| [legwork](https://skills.dbhq.uk/legwork/) | Research that settles a decision, and says when it cannot |
| [dovetail](https://skills.dbhq.uk/dovetail/) | Checks whether your repository still agrees with itself |
| [verve](https://skills.dbhq.uk/verve/) | Strips AI tells from prose and puts a voice back |
| [garmin](https://skills.dbhq.uk/garmin/) | Your Garmin data, answered in the terminal |
| [imager](https://skills.dbhq.uk/imager/) | Images from GPT Image 2, costed before it spends |
| [gitview](https://skills.dbhq.uk/gitview/) | Which branches are finished, and safe to delete |
| [atlassian](https://skills.dbhq.uk/atlassian/) | Jira issues and Confluence pages |
| [pennyblack](https://skills.dbhq.uk/pennyblack/) | A physical letter, posted from the terminal |
| [buildwork](https://skills.dbhq.uk/buildwork/) | Your open issues, run as parallel agents |
| [deskwork](https://skills.dbhq.uk/deskwork/) | What an agent noticed, tracked as real work |
| [groupwork](https://skills.dbhq.uk/groupwork/) | A second agent on the work, adversary or partner |
| [headwork](https://skills.dbhq.uk/headwork/) | One decision at a time, with a recommendation |

Plus [heliograph](https://skills.dbhq.uk/heliograph/), for a machine you cannot log into.

## Licence

MIT
