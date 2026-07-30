# Getting started

**A tutorial.** By the end you will have indexed a real .NET solution and run three
queries, one of which finds something no other code-intelligence tool can find.

It takes about five minutes. Every command and every piece of output below was run on
30 July 2026 on .NET SDK 10 and Linux, and is reproduced verbatim.

## Before you start

You need the .NET SDK, version 10.0 or newer.

```bash
dotnet --version
```

## 1. Install vela

```bash
git clone https://github.com/dbhq-uk/vela-skill.git
cd vela-skill
./install.sh
```

That builds the tool, installs it as a global .NET tool called `vela`, and symlinks the
skill into `~/.claude/skills`. If the installer warns that `~/.dotnet/tools` is not on your
`PATH`, add it and open a new shell:

```bash
export PATH="$HOME/.dotnet/tools:$PATH"
```

Check it:

```bash
vela --version
```

## 2. Make a solution to index

We will use a scaffolded Razor Pages app, because it is the smallest thing that shows what
vela is for and you can create it in one command.

```bash
mkdir ~/velatut && cd ~/velatut
dotnet new webapp -n RazorDemo -o RazorDemo
dotnet new sln -n RazorDemo --format sln
dotnet sln RazorDemo.sln add RazorDemo/RazorDemo.csproj
```

## 3. Build the index

```bash
vela index --stats
```

```
Indexed 23 documents to /home/you/.cache/vela/RazorDemo-6cbef186e8416dc7.db
documents            : 23
  generated          : 8   (compiled, not on disk)
  razor views        : 7   (.cshtml and .razor)
occurrences          : 2670
  in razor views     : 22
  definitions        : 182
```

Eight seconds, and the numbers are the point.

`razor views: 7` is seven `.cshtml` files, and there are seven `.cshtml` files on disk.
`in razor views: 22` is twenty-two symbol occurrences inside them. Those views are never
files the compiler reads; the Razor source generator turns them into C# and hands them
straight to the compilation. Every code-intelligence tool that walks the directory misses
all of them. vela reads the compilation instead.

The index went to a cache directory outside the project. Your `~/velatut` is byte-identical
to what it was a moment ago.

## 4. Ask where something is defined

The scaffolded app has an error page with a `RequestId` property. Ask where it is:

```bash
vela def RequestId
```

```
RazorDemo/Pages/Error.cshtml.cs
      11:20   def  RazorDemo.Pages.ErrorModel.RequestId

1 result(s)
```

Line 11, column 20, and the full name of the symbol. Paths are relative to the repository
root.

## 5. Ask what is in a file, without reading it

```bash
vela outline RazorDemo/Pages/Error.cshtml.cs
```

```
RazorDemo/Pages/Error.cshtml.cs
       5:11   def  RazorDemo.Pages
       9:14   def  RazorDemo.Pages.ErrorModel
      11:20   def  RazorDemo.Pages.ErrorModel.RequestId
      11:32   def  RazorDemo.Pages.ErrorModel.RequestId.get
      11:37   def  RazorDemo.Pages.ErrorModel.RequestId.set
      13:17   def  RazorDemo.Pages.ErrorModel.ShowRequestId
      15:17   def  RazorDemo.Pages.ErrorModel.OnGet()

7 result(s)
```

The whole shape of the file, without opening it. On a nine-hundred-line source file this is
the difference between one screen and a context window.

## 6. Ask what uses something

`ShowRequestId` is the interesting one, because of where it is used:

```bash
vela refs ShowRequestId
```

```
RazorDemo/Pages/Error.cshtml
      10:12   ref  RazorDemo.Pages.ErrorModel.ShowRequestId
RazorDemo/Pages/Error.cshtml.cs
      13:17   def  RazorDemo.Pages.ErrorModel.ShowRequestId

2 result(s)
```

**Look at the first line.** The reference is in `Error.cshtml`, a Razor view, at line 10,
column 12, and vela knows it binds to `ErrorModel.ShowRequestId` and not to anything else
of that name. The hit is reported against the `.cshtml` you can open, not against the
generated C# under `obj/` that nobody can.

That is the thing nothing else does. Sourcegraph's own Roslyn-based `scip-dotnet` indexes
this same app and finds zero `.cshtml` documents. So does every general-purpose
code-intelligence tool for agents.

## That is the tutorial

You have an index, and you know the three questions: where is it, what is in this file, and
what uses it.

## Next

- [Answering real questions](guides/querying.md), including the fourth verb, `impact`, and
  what to do when a name is ambiguous.
- [Indexing other languages](guides/multi-language.md), if your repository is not only
  .NET.
- [Running vela in CI](guides/ci.md).
- [The reference](reference.md), for every flag and every line of output.

## Cleaning up

The index is a cache. Delete the directory and it is gone:

```bash
rm -rf ~/velatut
rm -f ~/.cache/vela/RazorDemo-*.db
```
