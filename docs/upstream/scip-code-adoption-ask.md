# Draft: asking whether scip-code would adopt the remaining indexers

**Sent 9 August 2026** as [scip-code/scip#468](https://github.com/scip-code/scip/issues/468). Kept here as the record of what was asked. Intended as a plain issue on
[`scip-code/scip`](https://github.com/scip-code/scip), **not** a SEP: the SEP template is
for protocol and schema changes, and this is a governance question.

Posting it to `scip-code/scip` puts it in front of the steering committee, which includes
Sourcegraph, so it reaches both audiences at once without going round anybody.

**Before sending, decide the honest answer to one question:** how much ongoing maintenance
are you actually offering? The draft below offers to maintain the .NET indexer. If that is
not true, cut that paragraph, because an unmet offer is worse than no offer. The ask still
stands on its own without it.

---

**Title:** Would the scip-code org consider adopting the remaining indexers?

`scip-java`, `scip-go` and `scip-rust` moved to `scip-code` when SCIP's governance became
independent. `scip-typescript`, `scip-python`, `scip-clang`, `scip-ruby` and `scip-dotnet`
stayed under `sourcegraph`. I do not know whether that split was deliberate or simply
where things stopped, so this is a question rather than a proposal.

The reason for asking is that the split has started to track activity. As of today:

| Repository | Org | Last push | Open issues and PRs |
|---|---|---|---|
| `scip-java` | scip-code | 9 Aug | |
| `scip-go` | scip-code | 9 Aug | |
| `scip` | scip-code | 6 Aug | |
| `scip-clang` | sourcegraph | 8 Aug | 46 |
| `scip-typescript` | sourcegraph | 7 Aug | 53 |
| `scip-dotnet` | sourcegraph | 1 Aug, no commit to `main` since 27 May | 33 |

That is not a complaint about anyone's priorities. Sourcegraph have been straightforward
about them: on
[scip-dotnet#61](https://github.com/sourcegraph/scip-dotnet/issues/61) a maintainer wrote
that they have "no plans for any new features in SCIP indexers in general" and are
"focusing on support, prioritising customer support". That is a reasonable position for a
company to take, and it is exactly the situation the governance transition seems designed
for.

I have a concrete case, which is what prompted the question rather than being the point of
it. `scip-dotnet` does not index Razor or Blazor. Those reach the compilation as
source-generated documents, so iterating `project.Documents` misses every one of them: on a
scaffolded app that is 0 documents for 7 `.cshtml` files, and 0 for 11 `.razor`. I have a
patch for it, open as
[scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117), with the existing
snapshots byte-identical across net8.0, net9.0 and net10.0. It has not been reviewed, and
four other pull requests there have been waiting longer, so I do not read that as anything
personal.

I am not asking anyone to merge that patch. I am asking whether the adoption model already
used three times might suit the indexers that were left, and if so what the process would
be. If the answer is that the split was intentional and those repositories are staying
where they are, that is a perfectly good answer and worth knowing.

*(Optional paragraph, only if true: I would be glad to help maintain the .NET indexer if
that were useful. I have been working in that codebase and have a fork with the Razor work
in it, and I would rather contribute upstream than fork permanently.)*

For completeness: `scip-dotnet` is Apache 2.0 with no CLA, so forking is available and
needs nobody's agreement. I would much rather not. A fork would split the ecosystem, need
a new name under the trademark clause, and leave .NET users choosing between two
half-maintained indexers. Asking first seemed better.
