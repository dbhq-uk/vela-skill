# Contributing

Thanks for looking. vela works: the CLI is built, 420 tests pass, and it is measured on a
real ten-project solution of 388,323 lines of C#. The design is written up in
[docs/design-notes.md](docs/design-notes.md), which is now a historical record;
[docs/architecture.md](docs/architecture.md) is the current picture.

## Before opening a pull request

Read [AGENTS.md](AGENTS.md), in particular the three constraints. They are not
style preferences - a change that breaks one of them changes what the tool is:

1. Deterministic only. No model calls, no network, no heuristic ranking.
2. Never write to the indexed repository.
3. An incomplete index must never look like a complete one.

The third is the easiest to break by accident and the most damaging. An agent
that receives an empty reference list concludes the symbol is unused.

It also cuts both ways. A banner that fires when nothing is wrong is a banner nobody reads
by the time it is right, which is why a document contributed by the NuGet package cache is
reported plainly and never raises the exit code.

## The property to protect

Razor and Blazor coverage comes from iterating the compilation's syntax trees
rather than `project.Documents`. A regression here is silent: the index still
builds and queries still answer, while the Razor half of a codebase disappears.
Any change to the harvester needs a test asserting generated-document coverage
by count.

Both counts, not one. Seven empty Razor documents satisfy a document count and mean the
`#line` mapping has collapsed.

The property has an upstream half as well, and it is not vela's to control. The Razor
generator lives inside whichever .NET SDK is installed, and Roslyn refuses to load a
generator built against a newer compiler than the host - silently, with zero generators
returned. So the `Microsoft.CodeAnalysis.*` versions in `Vela.csproj` are a floor set by
the newest SDK in use, not a preference, and `NuGet.config` exists to reach a build of
them that nuget.org does not yet carry. If
`RazorGeneratorTests.HostedCompiler_IsAtLeastTheOneTheSdksRazorGeneratorWasBuiltAgainst`
fails, that is what it is telling you, and it names the version to raise the pin to. The
whole story is in [docs/upstream/razor-sdk-10-0-400.md](docs/upstream/razor-sdk-10-0-400.md).

```bash
dotnet test
vela index --stats     # in a dotnet new webapp scaffold
```

## House style

- British English, plain hyphens. No em or en dashes.
- Tests are hermetic: no network for the tool, throwaway solutions in temp directories. The
  fixtures do run `dotnet new webapp`, `dotnet new blazor` and `dotnet restore`, so a cold
  NuGet cache needs network for test setup.
- Documentation follows [Diataxis](https://diataxis.fr): a page is a tutorial, a how-to
  guide, reference, or explanation, and mixing modes on one page is the standard failure.
  Put a new page in [docs/](docs/) under the mode it belongs to and add it to
  [docs/README.md](docs/README.md), so everything stays reachable from one place.
- Numbers in the documentation are measurements, with the command that produced them
  wherever it is short enough to give. If you cannot measure it, leave it out.

## Upstream first

The two ways vela's harvester differs from Sourcegraph's `scip-dotnet` -
source-generated documents, and recorded enclosing ranges - are both upstreamable.
If you are improving either, consider sending it to `scip-dotnet` as well. We would
rather the ecosystem gained Razor support than that we kept it.

**This is not aspirational.** The Razor half is upstream now:

- **Pull request:**
  [sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117),
  "Index Razor views and Blazor components", open against their `main`.
- **Fork:** [dbhq-uk/scip-dotnet](https://github.com/dbhq-uk/scip-dotnet).
- **Issue it closes:**
  [#61](https://github.com/sourcegraph/scip-dotnet/issues/61), "Support for Razer
  templates", closed as *not planned*, where a maintainer wrote "We'll be happy to review a
  PR adding this feature".
- **The write-up**, including the full diff, the licensing reasoning and every
  measurement: [docs/upstream/scip-dotnet-razor.md](docs/upstream/scip-dotnet-razor.md).

The enclosing-ranges half has not been sent yet. It is the obvious next contribution.

`scip-dotnet` is Apache 2.0 and vela is MIT. Nothing was copied in either direction: the
patch was written from scratch against their types, naming and control flow, and no Apache
2.0 code has come back. If you contribute upstream on vela's behalf, keep it that way.
