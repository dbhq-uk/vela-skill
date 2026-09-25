---
name: vela
description: 'Compiler-exact code search over a SCIP index - find where a symbol is defined, every reference to it, who calls it, and what a change would break. Indexes .NET itself: C#, VB, Razor Pages, MVC views and Blazor components, with every Razor reference resolved by the compiler into a saved index, where grep sees only text. Any other language - TypeScript, Python, Go, Java - reaches the same database by importing the .scip file its own indexer produces, and then the same verbs answer over it; vela does not run those indexers itself. Deterministic, built on Roslyn, never modifies the repository. Use instead of grep when searching for an ordinary identifier (Name, Status, Value, Id, Update), when you need callers or change impact, when a symbol might be used from a .cshtml or .razor file, or when grep returns too many hits to read. Trigger on phrases like "vela", "find references", "who calls", "where is this used", "change impact", "blast radius", "find usages".'
---

# vela

Compiler-exact code search over a SCIP index. .NET answers come from Roslyn, other languages from their own indexers' `.scip` files. It never writes to the repository.

## Which tool

- **grep** when the name is distinctive. It is free and needs no index.
- **The LSP tool**, where your host has one, for code you have just edited and for "what implements this". It stays live after an edit, where vela needs a re-index, which takes minutes on a large solution.
- **vela** for:
  - ordinary names (`Name`, `Status`, `Id`), where grep is mostly noise and vela reports when a name is ambiguous
  - references from `.cshtml` and `.razor` files
  - callers, and what a change touches
  - a survey of a clean tree before a change
- **Not vela** in a repository with no .NET and no `.scip` to import.

## Steps

### 0. Check the command is installed

```bash
command -v vela
```

Plugin and skills.sh installs copy this file, not the command. If it prints nothing, stop and tell the user rather than quietly falling back to grep. vela needs:

- the .NET SDK 10.0 or newer
- the tool itself: clone `https://github.com/dbhq-uk/vela-skill` and run `./install.sh`
- `~/.dotnet/tools` on `PATH`

### 1. Build the index

vela does not restore packages. Run `dotnet restore` first if the solution has never been built.

```bash
vela index
```

vela uses the solution `vela.json` names, or else the only `.sln` or `.slnx` in this directory or the nearest one above it, up to the repository root. If it cannot pick one, pass `--solution <path>` to `vela index` and to every verb after it.

Indexing costs about what a build costs. Re-index after every code change: once a watched file is newer than the index, answers carry the stale banner. `vela index --incremental` rebuilds only what changed. Index without it before you delete or rename anything.

If a project fails to load or compile, vela says so. Anything that depended on it is missing from the index.

### 2. Add other languages

```bash
vela import path/to/index.scip             # first time
vela import --replace path/to/index.scip   # after re-running that indexer
```

Run `vela index` first, then import. A later `vela index` replays the imports. In a git repository with no solution, `vela import` alone makes the index. If `vela index` names a `.scip` that a `vela.json` job expects, run that indexer and import it: until then every answer carries the banner.

### 3. Ask

```bash
vela outline <file>    # what a file defines, cheaper than reading it
vela def     <symbol>  # where it is declared
vela refs    <symbol>  # every use, grouped by file
vela impact  <symbol>  # direct callers, one hop
vela find    <pattern> # discover a name by prefix
```

`def`, `refs` and `impact` match a whole dotted segment, case-sensitively. `Status` matches `App.Models.Perfume.Status` but not `HttpStatus`. Give more of the name (`Perfume.Status`) to narrow it. Paths are relative to the repository root, which is the form `outline` takes.

## Reading the answer

- **Razor and Blazor hits name the `.cshtml` or `.razor` file**, so you can open them. A Blazor component's definition and its uses by tag (`<Badge />`) print `file` instead of a line, because the Razor compiler records none. Search that file for the tag.
- **Generated code** is left out of `refs` and `impact` by default. A line says how much, and `--include-generated` shows it. `def` and `outline` show it, marked `(generated)`.
- **An ambiguity block** after the results means the total spans several symbols. Never size a change from that total. Ask again with the longer name it suggests.
- **`impact` names direct callers only**, one hop. A reference from a Razor view or a top level statement has no caller, and `impact` prints how many there are. Run `refs` to see them, and `impact` on a caller to go further.

## The rule that matters most

**An empty result is not proof that nothing uses the symbol.**

A banner starting `!! INCOMPLETE INDEX`, with exit code 3, means the index is missing code, out of date, or could not be checked, and it says which. Treat the answer as incomplete and say so. Never delete or rename on an empty answer from such an index. A quiet answer is not proof either: the freshness check watches .NET source and project files, and the files an imported `.scip` names. If you have edited code, re-index.

Every empty answer says which absence it is: no such name, nothing to report, or only in generated code. Read that line.

## What it does not do

- It does not edit, refactor or rename.
- It does not answer "what implements this". Use the LSP tool.
- It does not cover F#, and it does not run other languages' indexers.

## More detail

Every flag, output line, exit code and `vela.json` property: https://github.com/dbhq-uk/vela-skill/blob/main/docs/reference.md. All documentation: https://github.com/dbhq-uk/vela-skill/blob/main/docs/README.md.
