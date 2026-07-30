# Answering real questions

**A how-to guide.** Four questions you actually have about a codebase, and the command for
each.

Every example below was run on 30 July 2026 against a real 375,608-line C# solution with
307 Razor views. The numbers are what came back.

- [Is this used anywhere?](#is-this-used-anywhere)
- [Who calls this?](#who-calls-this)
- [What breaks if I change this?](#what-breaks-if-i-change-this)
- [What is in this file?](#what-is-in-this-file)
- [When a name is ambiguous](#when-a-name-is-ambiguous)
- [When to use grep instead](#when-to-use-grep-instead)
- [When not to trust the answer](#when-not-to-trust-the-answer)

## Is this used anywhere?

```bash
vela refs Entities.Perfume.Status
```

```
src/ScentVerdict.Data/Entities/Perfume.cs
      60:23   def  ScentVerdict.Data.Entities.Perfume.Status
src/ScentVerdict.ServiceInterface/Api/Admin/PerfumeListService.cs
     159:13   ref  ScentVerdict.Data.Entities.Perfume.Status
    2678:30   ref  ScentVerdict.Data.Entities.Perfume.Status
...

24 result(s)
```

Twenty-four occurrences, one of which is the definition.

`grep -rw --include='*.cs' --include='*.cshtml' Status .` over the same tree returns 2,267
lines. You cannot read 2,267 lines, and if you feed them to an agent you have spent the
context window rather than answered the question.

The gap for the ordinary names, measured on that solution, `grep -w` counting lines and
vela counting occurrences over the same `.cs` and `.cshtml` files:

| Question | vela | `grep -w` | Precision |
|---|---|---|---|
| `refs Entities.Perfume.Status` | 24 | 2,267 for `Status` | 1.1% |
| `refs Entities.Perfume.Name` | 244 | 3,653 for `Name` | 6.7% |
| `refs Brand.Name` | 325 | 3,653 for `Name` | 8.9% |

**An empty answer is not proof.** vela never prints a bare zero: it tells you which absence
this is. "Nothing of that name is indexed" and "it is indexed and every occurrence is in
generated code" mean completely different things, and only the first is about a name the
codebase does not have.

### Including the Razor and Blazor hits

They are already there. A reference from a `.cshtml` or `.razor` file is reported against
that file, with the line and column you can open:

```
src/ScentVerdict.Web/Pages/Admin/Partials/_ReviewBanner.cshtml
       6:28   ref  ScentVerdict.Data.Entities.TaskInstance.Metadata
       7:29   ref  ScentVerdict.Data.Entities.TaskInstance.Metadata
```

What is *not* there by default is the generated C# the Razor compiler produced, because
those paths do not exist on disk. `refs` and `impact` leave them out and always say how
many they left out:

```
2 further result(s) in generated code, which is not on disk. Pass --include-generated to
see them.
```

`def` and `outline` always include them, marked `(generated)`, because for some Razor page
members the generated document holds the only declaration there is.

## Who calls this?

```bash
vela impact Entities.Perfume.Status
```

```
src/ScentVerdict.ServiceInterface/Api/Admin/PerfumeListService.cs
      63:53   def  ScentVerdict.ServiceInterface.Api.Admin.PerfumeListService.Post(...)
    2596:55   def  ScentVerdict.ServiceInterface.Api.Admin.PerfumeListService.BuildResponseAsync(...)
src/ScentVerdict.ServiceInterface/Api/AdminSearchApiService.cs
     133:31   def  ScentVerdict.ServiceInterface.Api.AdminSearchApiService.Get(...)
...

17 result(s)
```

Seventeen callers behind twenty-four references, which is the difference between "where is
this mentioned" and "whose behaviour depends on it".

`impact` rows are the **calling** symbols, so the symbol you asked about does not appear in
them. Only the innermost enclosing definition counts: a reference inside a method inside a
type inside a namespace is attributed to the method, not to all three.

**`impact` cannot name a caller for a reference that sits inside no recorded body.** Top
level statements and Razor views are the normal cases, and an empty `impact` says so
outright rather than implying nothing calls the symbol. Run `refs` to see the references
themselves.

## What breaks if I change this?

Two commands, in this order.

```bash
vela impact Entities.Perfume.Status     # whose code depends on it
vela refs   Entities.Perfume.Status     # every place to edit
```

`impact` sizes the change. `refs` is the checklist.

Three things to check before you act on the answer:

1. **Is there an ambiguity block?** If so the total spans several symbols and is not the
   size of anything. See below.
2. **Is there a "further result(s) in generated code" line?** If so, some of the blast
   radius is in Razor output. Re-run with `--include-generated`.
3. **Did the command exit 3?** Then the index is missing code, out of date, or could not be
   verified, and there is a banner above the results saying which.

## What is in this file?

```bash
vela outline src/ScentVerdict.Data/Entities/Perfume.cs
```

```
src/ScentVerdict.Data/Entities/Perfume.cs
       5:11   def  ScentVerdict.Data.Entities
      11:14   def  ScentVerdict.Data.Entities.Perfume
      14:17   def  ScentVerdict.Data.Entities.Perfume.Id
      14:22   def  ScentVerdict.Data.Entities.Perfume.Id.get
      14:27   def  ScentVerdict.Data.Entities.Perfume.Id.set
      20:17   def  ScentVerdict.Data.Entities.Perfume.TenantId
...
```

That file is 343 lines. The outline is the shape of it, and it costs no file read.

**Do this before pulling content.** Outline to find the member, `def` to get its exact
location, then read only the lines you need. Reading a 900-line source file to discover
what is in it is the most expensive mistake available.

The path is relative to the repository root and matched exactly. If you get "No document
with the path ... is in the index", check the path is rooted at the repository and not at
the solution directory or at your shell's current directory.

### Finding a name you only half remember

```bash
vela find Stat
```

`find` is the discovery verb and the only one that matches loosely: whole name tokens plus a
prefix of the last one. So `find Stat` finds `Status`, and `refs Stat` finds nothing.

Use `find` to get the name, then use `def`, `refs` or `impact` to ask about it.

## When a name is ambiguous

A bare name can name more than one thing. Ask about `Status` on that solution:

```
2088 result(s)

'Status' is ambiguous: the 2088 result(s) above span 154 distinct symbols across
155 construction(s) of them:
     738  ScentVerdict.Data.Entities.TaskInstance.Status
     216  ScentVerdict.Data.Entities.Note.Status
     142  ScentVerdict.Data.Entities.Accord.Status
      ...
      24  ScentVerdict.Data.Entities.Perfume.Status
      ...
     754  (+144 further symbol(s))
To ask about one of them, give more of its name: 'Entities.TaskInstance.Status' matches
ScentVerdict.Data.Entities.TaskInstance.Status and none of the others.
```

Every one of those 2,088 hits is a real occurrence of a real symbol. The number at the
bottom is still the size of nothing: no single thing in that codebase has 2,088 references.

**Do exactly what the block says.** Lengthen the name until one symbol is left, then use
that answer. `Entities.Perfume.Status` gets you 24, and 24 is the number you can act on.

Nothing is filtered to produce the block. The same results come back either way; it only
tells you what they span. At most ten symbols are listed and the rest are summarised into
one line, so the counts always add up to the total.

**The block describes the answer, not the index.** `refs` and `impact` leave generated code
out by default, so its absence means the results above are occurrences of one symbol. If
there is also a "further result(s) in generated code" line, a second symbol of that name
may be living there uncounted.

## When to use grep instead

For a distinctive identifier, grep wins on zero setup.

```bash
grep -rw --include='*.cs' --include='*.cshtml' PerfumeService .
```

Thirty-two lines on that solution, most of them documentation comments mentioning the
class, all of them readable in one screen. vela's answer is 7, and it is the more correct
one, but you did not need it.

vela earns its keep on:

- **the ordinary names.** `Name`, `Status`, `Value`, `Id`, `Update`. See the table above.
- **the questions grep cannot answer at any precision.** Which occurrence is the
  definition. Which overload was meant. Whether `@Model.Perfume` in a `.cshtml` binds to a
  particular property on a particular type. Where an inherited or extension member called
  as `x.Foo()` actually lives. What `using Foo = Bar` aliases.
- **anything in a `.cshtml` or `.razor`.** grep finds the text; it cannot tell you what the
  text binds to.
- **callers**, which grep has no concept of.
- **any time grep returns more hits than you can read**, because at that point the answer
  has become a context-window problem rather than a search problem.

## When not to trust the answer

**Exit code 3 and a `!!` banner.** The banner names the reason. Every one of them means the
index is missing code, out of date, or could not be checked, and all of them mean the same
thing for how you should read the results: do not delete or rename a symbol on the strength
of a short reference list.

```
!! INCOMPLETE INDEX - these results may be missing references.
   stale index: 1 source file(s) changed after the index was built at ...
   Do not treat an empty or short result as proof the symbol is unused.
```

The fix for a stale index is `vela index`. The fix for a project that failed to load or
compiled with errors is to make the solution build; **compilation errors matter more than
they look**, because every reference that depended on a type the compiler could not resolve
is simply absent from the index.

**The freshness check is narrower than the index.** It watches `.cs`, `.vb`, `.cshtml`,
`.razor`, `.csproj`, `.vbproj`, `.sln`, `.slnx`, `.props` and `.targets`, and never
descends into `bin`, `obj`, `.git`, `.vs`, `.idea` or `node_modules`. So a quiet answer
means no watched file has changed; it is not proof that nothing has. If you have edited code
yourself, or something ran that rewrites files, re-index rather than reading silence as
confirmation.

## Next

- [The reference](../reference.md) for every flag and every line of output.
- [Indexing other languages](multi-language.md).
- [Why the answers are what the compiler believes](../architecture.md).
