# Upstream

Where vela stands with the projects it builds on, what has been contributed back, and
what is still open. Written down here rather than living in someone's head, because the
decisions below have licensing and maintenance consequences and the reasoning matters as
much as the conclusion.

Last reviewed 9 August 2026.

## What has been contributed

| | |
|---|---|
| Change | Index Razor views and Blazor components |
| Pull request | [sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117) |
| Issue it addresses | [#61](https://github.com/sourcegraph/scip-dotnet/issues/61), "Support for Razer templates", closed as not planned |
| Fork it came from | [dbhq-uk/scip-dotnet](https://github.com/dbhq-uk/scip-dotnet) |
| Opened | 30 July 2026 |
| Status | Open. No comments, no review, no labels, no assignee. |
| Working notes | [scip-dotnet-razor.md](scip-dotnet-razor.md) |

The measurements, the diff and the reasoning are in the working notes. In short: on a
scaffolded app `scip-dotnet` goes from 0 to 6 `.cshtml` documents, and from 0 to 11
`.razor`, with its existing snapshots byte-identical on net8.0, net9.0 and net10.0.

Worth noting for anyone reading #61: a maintainer wrote there that "Blazor should be
supported. If you have an example of it not working, please provide a reproducer." A
stock `dotnet new blazor` produces zero `.razor` documents, and that reproducer is in the
pull request. The same thread says "We'll be happy to review a PR adding this feature."

## Why it may not land, and why that is survivable

`scip-dotnet` has had no commit to `main` since 27 May 2026 and carries four open pull
requests, two of them dependency bumps that have waited months. That is not our pull
request being ignored: the repository is not being merged into at all. It matches what
the maintainer said on #61, that they are "focusing on support, prioritising customer
support" with "no plans for any new features in SCIP indexers in general".

None of vela's Razor support depends on this landing. The upstream patch is a
contribution back, not a dependency. If it never merges, vela is unaffected and .NET
users of Sourcegraph keep the gap.

## The governance picture

SCIP moved from Sourcegraph ownership to independent governance on 25 March 2026, with a
Core Steering Committee drawn from Meta, Uber and Sourcegraph, and a SCIP Enhancement
Proposal process for protocol changes.

Some indexers moved to the [`scip-code`](https://github.com/scip-code) org and some did
not, and the split tracks activity closely:

| Repository | Org | Last push (9 Aug 2026) |
|---|---|---|
| `scip` | scip-code | 6 Aug |
| `scip-java` | scip-code | 9 Aug |
| `scip-go` | scip-code | 9 Aug |
| `scip-dotnet` | sourcegraph | 1 Aug, no `main` commit since 27 May |
| `scip-typescript` | sourcegraph | 7 Aug, 53 open |
| `scip-clang` | sourcegraph | 8 Aug, 46 open |

**A SEP is the wrong instrument for the Razor change.** The template describes it as for
"a major architectural change, protocol schema update, or significant new feature", and
it lives in the protocol repository. Razor support is an indexer feature and needs no
schema change at all: `Language.Razor = 62` already exists in `scip.proto` and
`scip-dotnet` simply never emits it. There is no SEP to write.

Moving a repository between orgs is a separate, governance question, and there is
precedent for it in `scip-java`, `scip-go` and `scip-rust`.

## The licensing position

`scip-dotnet` is Apache 2.0, copyright Sourcegraph 2022. There is **no CLA** anywhere in
the repository.

**No permission is needed to fork it and maintain it.** Apache 2.0 sections 2 and 4 grant
that outright, subject to four conditions: ship the licence, mark modified files as
changed, retain the existing copyright and attribution notices, and carry the `NOTICE`
file, which credits `tcz717/LsifDotnet` because scip-dotnet is itself partly derived work.
Section 3 also grants a patent licence, which MIT would not.

**Section 6 withholds one thing:** trade names and product names. A hard fork cannot keep
the name `scip-dotnet` or publish to that NuGet package id. Describing a fork as derived
from Sourcegraph's scip-dotnet is explicitly permitted, and is the customary use section 6
allows for.

So the two routes differ sharply in what they need:

| | Sourcegraph's agreement |
|---|---|
| Fork under a new name, publish under a new package id | no |
| Keep the `scip-dotnet` name, repository, stars, issues or package id | yes |
| `scip-code` adopting the existing repository | yes, and the steering committee's |

## The current position

**Ask before forking.** A transfer keeps the name, the history, the issue backlog and the
package id, and puts the repository where the maintenance actually is. It costs a message.
A fork needs nobody's agreement and could start today, but it splits the ecosystem, needs
a new name, and requires somebody to genuinely maintain a .NET SCIP indexer. A fork that
lands and then goes quiet is worse for everyone than the present situation.

The ask is drafted at [scip-code-adoption-ask.md](scip-code-adoption-ask.md) and has not
been sent. It is deliberately not tied to #117: an ask that reads as "merge my pull
request by changing who owns the repository" fails at both.

vela is unaffected either way. This is about whether .NET users of the wider SCIP
ecosystem get Razor support, not about whether vela does.
