# Contributing

Thanks for looking. vela is early - the design is settled and written up in
[docs/design-notes.md](docs/design-notes.md); the implementation is not yet built.

## Before opening a pull request

Read [AGENTS.md](AGENTS.md), in particular the three constraints. They are not
style preferences - a change that breaks one of them changes what the tool is:

1. Deterministic only. No model calls, no network, no heuristic ranking.
2. Never write to the indexed repository.
3. An incomplete index must never look like a complete one.

The third is the easiest to break by accident and the most damaging. An agent
that receives an empty reference list concludes the symbol is unused.

## The property to protect

Razor and Blazor coverage comes from iterating the compilation's syntax trees
rather than `project.Documents`. A regression here is silent: the index still
builds and queries still answer, while the Razor half of a codebase disappears.
Any change to the harvester needs a test asserting generated-document coverage
by count.

## House style

- British English, plain hyphens. No em or en dashes.
- Tests are hermetic: no network, throwaway solutions in temp directories.

## Upstream first

The two ways vela's harvester differs from Sourcegraph's `scip-dotnet` - source-
generated documents, and recorded enclosing ranges - are both upstreamable. If
you are improving either, consider sending it to `scip-dotnet` as well. We would
rather the ecosystem gained Razor support than that we kept it.
