---
name: vela
description: Compiler-exact code search for .NET solutions - find where a symbol is defined, every reference to it, who calls it, and what a change would break. Covers C#, VB, Razor Pages, MVC views and Blazor components, which grep and every other code-intelligence tool miss. Deterministic, built on Roslyn, never modifies the repository. Use instead of grep when searching for an ordinary identifier (Name, Status, Value, Id, Update), when you need callers or change impact, when a symbol might be used from a .cshtml or .razor file, or when grep returns too many hits to read. Trigger on phrases like "vela", "find references", "who calls", "where is this used", "change impact", "blast radius", "find usages".
---

# vela

Compiler-exact code search for .NET. Answers come from Roslyn's semantic model, so they are what the compiler believes rather than what a pattern matched.

Deterministic: no model calls, no network, and it never modifies the repository it indexes.

## When to use this instead of grep

Use grep when the identifier is distinctive. `grep -w PerfumeService` returns thirty-two lines, costs nothing, and needs no index.

Use vela when:

- the name is ordinary - `Name`, `Status`, `Value`, `Id`, `Update`. Measured on a real solution, grep is 91 to 99% noise for these.
- you need **callers**, not just textual matches
- you need to know **what breaks** if you change something
- the symbol might be referenced from a **`.cshtml` or `.razor`** file. Nothing else indexes these, including grep, which finds the text but cannot tell you it binds to a specific property on a specific type
- grep returned more hits than you can read, which means the answer is now a context-window problem rather than a search problem

## Steps

### 1. Ensure an index exists

```bash
vela index
```

Builds the index for the solution in the current directory. It costs about what a build costs: roughly 8 seconds on a scaffolded Razor Pages app, and 2 minutes 12 seconds at 1.5GB peak on a real 375,608-line, ten-project solution. It is needed once, plus after any code change: the index is a snapshot, and every verb reports it as degraded once a watched file under the repository root is newer than it.

**The watch is narrower than the index, so the absence of a banner is not proof the tree is unchanged.** What is watched is every `.cs`, `.vb`, `.cshtml`, `.razor`, `.csproj`, `.vbproj`, `.sln`, `.slnx`, `.props` and `.targets` file under the repository root - the sources vela indexes, plus the project and solution files that decide what is compiled - and nothing under `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules` or the index's own cache directory. A change anywhere else is invisible to the check: a checked-in generated artefact with another extension, a source file that only exists under an excluded directory, or a `Directory.Build.props` inside `obj`. If you have edited code yourself, or you know something ran that rewrites files, re-index rather than reading a quiet answer as confirmation the index is current.

The index is rooted at the **repository root** - the working tree the solution sits in, or the solution's own directory when it is in no repository. So a `repo/src/App.sln` layout still covers `repo/tests/`, every path you are given is relative to that root, and that is the form `outline` expects back.

The solution must build. If a project fails to load, or compiles with errors, vela says so - do not proceed as though the index were complete. Compilation errors matter more than they look: every reference that depends on a type the compiler could not resolve is simply absent from the index.

`vela index` may print a plain line such as `1 document(s) contributed by a NuGet package or the .NET SDK were not indexed`. That is not a warning and has no `!!` banner: those files live in the NuGet package cache or the .NET installation, none of the repository's code is missing, and the exit code stays 0. Do not treat it as a gap. Anything vela cannot attribute to a package or the SDK is treated as a gap instead, and arrives with the banner and exit 3.

Add `--stats` to see what was indexed, including how many Razor views were covered and the path of every document that was left out.

**There is a `--incremental` flag. It is opt-in, and a plain `vela index` is the safe choice whenever you are not sure.** `vela index --incremental` rebuilds only the projects whose inputs changed plus every project downstream of them, reusing the rest. A full rebuild cannot be stale, because it reads everything; an incremental one is a claim that what it skipped has not changed. What it saves depends entirely on where the edit was: measured on a real ten-project solution, nothing changed took 11.9s against a 158.1s full index, a one-line edit to a leaf project took 22.2s, and a one-line edit to the project everything else depends on rebuilt all ten and took 153.9s, which is a full rebuild and no saving at all. **It helps most when you edit a leaf, and not at all when you edit the bottom of the dependency graph.**

It refuses rather than guesses. If there is no index yet, if the schema or the vela version changed, if the set of projects changed, if the change reaches every project, or if anything at all goes wrong deciding, it prints `Falling back to a full rebuild:` with the reason and rebuilds everything. That is a good outcome, not a failure. A project that was skipped keeps saying it does not compile, so the banner cannot go quiet on you. What it cannot see is an assembly rebuilt in place at the same path and version, because references are compared by path rather than content - so after anything outside the solution was rebuilt, index without the flag. **If you are about to delete or rename on the strength of an answer, index without the flag first.**

If the repository has a `vela.json`, `vela index` says so and lists the jobs it declares. A job whose indexer is not `vela` is a language vela cannot produce itself: it names where that indexer's `.scip` is expected, and until you run the indexer and `vela import` that file, the index is missing that language, every answer carries the banner and the exit code is 3. That is a real gap, not noise - the language really is absent - so either import it or say plainly that the answer covers only the .NET half. `vela index` will also print which languages no job covers at all; that line is information and never raises the exit code, because vela was never going to index them.

The index is a cache, and it carries the schema version of the vela that wrote it. If you upgrade vela and the shape of the index has changed, every verb refuses to answer and tells you to re-index rather than querying a database it cannot read. Re-index; there is nothing else to do.

### 2. Establish shape before pulling content

```bash
vela outline <file>
```

Takes a path relative to the repository root, and returns the symbol tree without reading the file. Do this first: it is far cheaper than reading a 900-line source file to find out what is in it.

### 3. Ask the specific question

```bash
vela def    <symbol>          # declaration, signature, source span
vela refs   <symbol>          # every usage, grouped by file
vela impact <symbol>          # callers and blast radius
vela find   <pattern>         # symbol search by name
```

Symbols can be given bare (`Status`) or qualified (`Perfume.Status`). `def`, `refs` and `impact` match a **whole dotted segment**, case-sensitively: `Status` matches `App.Models.Perfume.Status` and does **not** match `HttpStatus`, `OrderStatus` or `status`. So a bare name is safe to use, and **vela will tell you when the name is ambiguous**: if several distinct symbols really do end in that segment, the answer names each one with its own count and suggests a longer name that picks one out.

A method matches with or without its parameter list (`Publish` or `PerfumeService.Publish`), and a local or a parameter matches by its own name rather than by the name of the method or type it is declared in: `refs PerfumeService` finds the type and its constructor, not the variables that constructor is handed, and `refs Get` finds the methods rather than every local declared inside one.

Generic type arguments are not part of a name either, so a bare name reaches a generic whatever it was constructed with: `refs ILogger` finds every `ILogger<T>` in the solution, and `refs RunWithAuditAsync` finds every instantiation of the method as well as its declaration. A type argument is not counted as an occurrence of the symbol it names, so `ILogger<PerfumeService>` is an occurrence of `ILogger` and not of `PerfumeService`, which has its own occurrence at its own position.

`find` is the exception: it searches name tokens with a trailing prefix, so `find Stat` finds `Status` where `refs Stat` finds nothing. Use `find` to discover a name and the other three to ask about it.

## Reading the output

Results are grouped by file and shaped for a context window rather than a terminal.

**Razor and Blazor hits are reported against the originating `.cshtml` or `.razor` file**, not the generated code, so the location is one you can open and edit.

**Some locations are not on disk.** The Razor generator's output is compiled but never written out, so `refs` and `impact` leave it out by default and print a line saying how much they left out. Pass `--include-generated` if you need it. `def` and `outline` always include it, marked `(generated)` - for some Razor page members the generated code holds the only declaration there is, and the marker is there to tell you the path cannot be opened.

**A total that spans several symbols says so.** Because matching is by whole dotted segment, `refs Perfume` on a real solution answered 3,104 results - the entity, the entity's constructor, an enum member called `Perfume`, and a property of an unrelated response type, all merged into one number. Every hit was real; the total counted nothing that exists. So when a pattern matches more than one distinct symbol, `def`, `refs` and `impact` print an ambiguity block after the results:

```
'Perfume' is ambiguous: the 3104 result(s) above span 25 distinct symbols:
    1958  ScentVerdict.Data.Entities.Perfume
     384  ScentVerdict.Data.Enums.EntityType.Perfume
     ...
     144  (+15 further symbol(s))
To ask about one of them, give more of its name: 'Entities.Perfume' matches
ScentVerdict.Data.Entities.Perfume and none of the others.
```

**Never size a change from a total that carries that block.** Ask again with the longer name it suggests, then use that answer. Nothing is filtered out to produce the block - the same results come back either way - it only says what they span. At most ten symbols are listed by name and the rest are summarised into one line, so the counts always add up to the reported total. `impact` labels its numbers differently, because its rows are the callers rather than the symbol you asked about.

The block describes the answer above it, not the index: the count is of the symbols these results span, and `refs` and `impact` leave generated code out by default. So its absence means the results above are all occurrences of one symbol - which is a statement about this answer. If the answer also reports further results in generated code, a second symbol of that name may be living there, uncounted; ask again with `--include-generated` before treating the name as resolved. `outline` never prints the block, since its argument is a file path and a file defines many symbols by nature.

One further note appears where the block does not. Where an answer is all of one symbol but covers several stored names that differ only inside their type arguments, vela says so in a sentence. That is either one generic used several ways or overloads no pattern can select between, so there is nothing to narrow to - the note exists so a single-symbol answer never silently stands for more than one signature.

## The rule that matters most

**An empty result is not proof that nothing uses the symbol.**

If vela reports that a project failed to load, that a project did not compile, that the index is stale relative to the working tree, or that freshness could not be checked at all - because the root the index was built against has moved or a directory under it could not be read - treat the answer as incomplete and say so. All of them print a banner above the results and exit 3. Do not delete or rename a symbol on the strength of an empty reference list from a degraded index. vela is built to report its own gaps loudly; honour that signal rather than reading past it.

Every verb also explains an empty answer rather than printing a bare zero, and the explanation distinguishes "nothing of that name is indexed" from "it is indexed and there is nothing to report" and from "it is indexed and every occurrence is in generated code". Read it: they mean different things, and only the first is about a name the codebase does not have.

## What it does not do

- It does not edit, refactor or rename. It reports.
- It does not do semantic or similarity search. The index is exact.
- It does not answer "what implements this interface". That is a SCIP relationship, and vela does not emit those yet.
- It does not cover F#. Roslyn covers C# and Visual Basic only.
- It does not run other languages' indexers. It imports their `.scip` output.

## Where the detail is

Every flag, exit code, output line and `vela.json` property is in
[docs/reference.md](../../docs/reference.md). The full documentation index is
[docs/README.md](../../docs/README.md).
