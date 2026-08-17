# vela documentation

Compiler-exact code search for .NET. Start at the [project README](../README.md) if you
have not read it.

These pages are organised by what you are trying to do, rather than by feature. Each one is
a single kind of document, so you can tell from the heading whether it will help.

## Learning

**[Getting started](getting-started.md)** &nbsp; A tutorial. Index a real solution and run
your first three queries, in about five minutes. One happy path, no options.

## Doing

**[Answering real questions](guides/querying.md)** &nbsp; Is this used anywhere, who calls
it, what breaks if I change it, what is in this file, and when to use grep instead.

**[Indexing other languages](guides/multi-language.md)** &nbsp; Getting TypeScript, Python,
Go or anything else with a SCIP indexer into the same database, and what `vela.json` is
for.

**[Running vela in CI](guides/ci.md)** &nbsp; Exit codes as a gate, asserting Razor coverage
that would otherwise regress silently, and keeping a local index fresh.

## Looking things up

**[Reference](reference.md)** &nbsp; Every verb, argument, flag, exit code, matching rule,
output line and `vela.json` property.

**[The SCIP ecosystem](scip-ecosystem.md)** &nbsp; Every other SCIP indexer, with versions,
install commands and a last-checked date. Where to get an index for your language.

## Understanding

**[Architecture](architecture.md)** &nbsp; The four layers, the schema, the two-names
decision, and how we know the answers are right.

**[Design notes](design-notes.md)** &nbsp; The historical record: why the tool is shaped
this way, written before the implementation, with the measurements that drove it.

**[Upstream](upstream/README.md)** &nbsp; Where vela stands with the projects it builds on:
what has been contributed back, what is still open, the licensing position, and why a fork
is available but not the preferred route.

**[The Razor change we owe scip-dotnet](upstream/scip-dotnet-razor.md)** &nbsp; The patch
that gives Razor indexing back to Sourcegraph's own indexer, now open as
[PR #117](https://github.com/sourcegraph/scip-dotnet/pull/117).

**[Razor went missing on .NET SDK 10.0.400](upstream/razor-sdk-10-0-400.md)** &nbsp; The
one regression that took away the capability vela exists for, how it was proved, and why
the compiler vela hosts is set by whichever SDK you have installed.

## For contributors

- [AGENTS.md](../AGENTS.md), the working brief for anyone changing the code, human or
  otherwise.
- [CONTRIBUTING.md](../CONTRIBUTING.md).
- [The example config](examples/scentverdict-vela.json), which is the real `vela.json` for
  the solution vela is measured on.
