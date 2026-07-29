---
name: vela
description: Compiler-exact code search for .NET solutions - find where a symbol is defined, every reference to it, who calls it, and what a change would break. Covers C#, VB, Razor Pages, MVC views and Blazor components, which grep and every other code-intelligence tool miss. Deterministic, built on Roslyn, never modifies the repository. Use instead of grep when searching for an ordinary identifier (Name, Status, Value, Id, Update), when you need callers or change impact, when a symbol might be used from a .cshtml or .razor file, or when grep returns too many hits to read. Trigger on phrases like "vela", "find references", "who calls", "where is this used", "change impact", "blast radius", "find usages".
---

# vela

Compiler-exact code search for .NET. Answers come from Roslyn's semantic model, so they are what the compiler believes rather than what a pattern matched.

Deterministic: no model calls, no network, and it never modifies the repository it indexes.

## When to use this instead of grep

Use grep when the identifier is distinctive. `grep -w PerfumeService` returns twenty-four lines, costs nothing, and needs no index.

Use vela when:

- the name is ordinary - `Name`, `Status`, `Value`, `Id`, `Update`. Measured on a real solution, grep is 88 to 98% noise for these.
- you need **callers**, not just textual matches
- you need to know **what breaks** if you change something
- the symbol might be referenced from a **`.cshtml` or `.razor`** file. Nothing else indexes these, including grep, which finds the text but cannot tell you it binds to a specific property on a specific type
- grep returned more hits than you can read, which means the answer is now a context-window problem rather than a search problem

## Steps

### 1. Ensure an index exists

```bash
vela index
```

Builds the index for the solution in the current directory. Takes tens of seconds on a large solution and is needed once, plus after any code change: the index is a snapshot, and every verb reports it as degraded once anything under the solution directory is newer than it.

The solution must build. If a project fails to load, or compiles with errors, vela says so - do not proceed as though the index were complete. Compilation errors matter more than they look: every reference that depends on a type the compiler could not resolve is simply absent from the index.

Add `--stats` to see what was indexed, including how many Razor views were covered.

The index is a cache, and it carries the schema version of the vela that wrote it. If you upgrade vela and the shape of the index has changed, every verb refuses to answer and tells you to re-index rather than querying a database it cannot read. Re-index; there is nothing else to do.

### 2. Establish shape before pulling content

```bash
vela outline <file|type>
```

Returns the symbol tree without reading the file. Do this first: it is far cheaper than reading a 900-line source file to find out what is in it.

### 3. Ask the specific question

```bash
vela def    <symbol>          # declaration, signature, source span
vela refs   <symbol>          # every usage, grouped by file
vela impact <symbol>          # callers and blast radius
vela find   <pattern>         # symbol search by name
```

Symbols can be given bare (`Status`) or qualified (`Perfume.Status`). `def`, `refs` and `impact` match a **whole dotted segment**, case-sensitively: `Status` matches `App.Models.Perfume.Status` and does **not** match `HttpStatus`, `OrderStatus` or `status`. So a bare name is safe to use, and qualifying only narrows further when several types really do declare the same member name - the answer will show you, because each hit prints its full symbol name.

`find` is the exception: it searches name tokens with a trailing prefix, so `find Stat` finds `Status` where `refs Stat` finds nothing. Use `find` to discover a name and the other three to ask about it.

## Reading the output

Results are grouped by file and shaped for a context window rather than a terminal.

**Razor and Blazor hits are reported against the originating `.cshtml` or `.razor` file**, not the generated code, so the location is one you can open and edit.

**Some locations are not on disk.** The Razor generator's output is compiled but never written out, so `refs` and `impact` leave it out by default and print a line saying how much they left out. Pass `--include-generated` if you need it. `def` and `outline` always include it, marked `(generated)` - for some Razor page members the generated code holds the only declaration there is, and the marker is there to tell you the path cannot be opened.

## The rule that matters most

**An empty result is not proof that nothing uses the symbol.**

If vela reports that a project failed to load, that a project did not compile, or that the index is stale relative to the working tree, treat the answer as incomplete and say so. All three print a banner above the results and exit 3. Do not delete or rename a symbol on the strength of an empty reference list from a degraded index. vela is built to report its own gaps loudly; honour that signal rather than reading past it.

Every verb also explains an empty answer rather than printing a bare zero, and the explanation distinguishes "nothing of that name is indexed" from "it is indexed and there is nothing to report" and from "it is indexed and every occurrence is in generated code". Read it: they mean different things, and only the first is about a name the codebase does not have.

## What it does not do

- It does not edit, refactor or rename. It reports.
- It does not do semantic or similarity search. The index is exact.
- It does not answer "what implements this interface". That is a SCIP relationship, and vela does not emit those yet.
- It does not cover F#. Roslyn covers C# and Visual Basic only.
