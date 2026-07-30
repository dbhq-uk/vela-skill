# The SCIP ecosystem

**Reference.** vela consumes `.scip`, so this page is where to get an index for a
language vela cannot index itself.

vela indexes C#, Visual Basic, Razor Pages, MVC views and Blazor components. Everything
else in a repository has to come from that language's own indexer, as a `.scip` file that
`vela import` reads into the same database. See
[the multi-language guide](guides/multi-language.md) for the mechanics; this page is the
catalogue.

**Last checked: 30 July 2026.** Every version, date and repository below was read from the
GitHub API or the project's own README on that date. How to re-check is at the bottom.

## One thing that catches people out

SCIP is no longer a Sourcegraph project. Some of the indexers moved to the new
`scip-code` organisation and some did not, so the org in a URL is not a reliable guide to
who maintains what. GitHub redirects the old paths, so `sourcegraph/scip-java` still
resolves, but the canonical name is [`scip-code/scip-java`](https://github.com/scip-code/scip-java).

| Now in `scip-code` | Still under `sourcegraph` |
|---|---|
| `scip` (the protocol), `scip-java`, `scip-go`, `scip-rust` | `scip-typescript`, `scip-python`, `scip-clang`, `scip-ruby`, `scip-dotnet` |

## The indexers

| Language | Tool | Repository | Maintainer | Latest |
|---|---|---|---|---|
| TypeScript, JavaScript | `scip-typescript` | [`sourcegraph/scip-typescript`](https://github.com/sourcegraph/scip-typescript) | Sourcegraph | v0.4.0, 2 Oct 2025 (commits to 24 Jul 2026) |
| Python | `scip-python` | [`sourcegraph/scip-python`](https://github.com/sourcegraph/scip-python) | Sourcegraph, a Pyright fork | npm 0.6.6, no GitHub releases (commits to 30 Jul 2026) |
| Java, Scala, Kotlin | `scip-java` | [`scip-code/scip-java`](https://github.com/scip-code/scip-java) | the `scip-code` org | v0.13.1, 2 Jul 2026 |
| Go | `scip-go` | [`scip-code/scip-go`](https://github.com/scip-code/scip-go) | the `scip-code` org | v0.2.7, 25 May 2026 |
| Rust | `rust-analyzer` | [`rust-lang/rust-analyzer`](https://github.com/rust-lang/rust-analyzer) | rust-lang, built in | n/a |
| C, C++, CUDA | `scip-clang` | [`sourcegraph/scip-clang`](https://github.com/sourcegraph/scip-clang) | Sourcegraph | v0.4.0, 23 Feb 2026 |
| Ruby | `scip-ruby` | [`sourcegraph/scip-ruby`](https://github.com/sourcegraph/scip-ruby) | Sourcegraph, built on Sorbet | v0.4.7, 7 Nov 2025 |
| PHP | `scip-php` | [`davidrjenni/scip-php`](https://github.com/davidrjenni/scip-php) | community, David Jenni | no releases (commits to 9 Jul 2026) |
| Dart | `scip-dart` | [`Workiva/scip-dart`](https://github.com/Workiva/scip-dart) | Workiva | 1.6.2, 28 May 2025 (commits to 3 Apr 2026) |
| C#, Visual Basic | `scip-dotnet` | [`sourcegraph/scip-dotnet`](https://github.com/sourcegraph/scip-dotnet) | Sourcegraph | v0.2.14, 5 May 2026 |
| Debian packaging | `debian-lsp` | [`jelmer/debian-lsp`](https://github.com/jelmer/debian-lsp) | community, Jelmer Vernooij | v0.1.7, 30 Mar 2026 |

### TypeScript and JavaScript

```bash
npm install -g @sourcegraph/scip-typescript
scip-typescript index
```

Its README states that Node v18 and Node v20 are the supported versions. It can exhaust
Node's heap on a large tree; the README's own remedy is to raise the limit:
`node --max-old-space-size=16000 "$(which scip-typescript)" index`. There are flags for
Yarn and pnpm workspaces.

This is the indexer vela's polyglot support was proved against. See
[the multi-language guide](guides/multi-language.md).

### Python

```bash
npm install -g @sourcegraph/scip-python
scip-python index . --project-name=NAME
```

Python 3.10 or newer, Node 16 or newer. It is a fork of Pyright, so it wants your virtual
environment activated before it runs, and it shells out to `pip` to work out what is
installed unless you pass `--environment`. There are no GitHub releases at all: the npm
package is the release channel, and the repository is still committed to.

### Java, Scala and Kotlin

```bash
docker run -v $(pwd):/sources --env JVM_VERSION=17 \
  ghcr.io/scip-code/scip-java:latest scip-java index
```

The README recommends the Docker image as the easiest route, at some cost in download size
and speed, because the image ships Java 17, 21 and 25. Coursier is the other supported
route:

```bash
coursier bootstrap --standalone -o scip-java org.scip-code:scip-java:STABLE_VERSION \
  --main org.scip_code.scip_java.ScipJava
```

Kotlin support is less mature than Java. The separate [`sourcegraph/scip-kotlin`](https://github.com/sourcegraph/scip-kotlin) was
archived on 2 Jul 2026 and its work folded into `scip-java`.

### Go

```bash
go install github.com/scip-code/scip-go/cmd/scip-go@latest
scip-go
```

Needs a `go.mod`. Standard library navigation needs `--go-version` and an extra upload
step rather than coming for free.

### Rust

`rust-analyzer` emits SCIP itself, so there is no separate indexer to install:

```bash
rust-analyzer scip path/to/project --output index.scip
```

Dependencies are only covered if their sources are present, so `cargo vendor` first if you
want them. There is also an `--exclude-vendored-libraries` flag for the opposite case.

### C, C++ and CUDA

Prebuilt binaries only, for x86_64 Linux (glibc 2.16 or newer) and arm64 macOS. There is
no Windows binary. Run it from the project root against a compilation database:

```bash
scip-clang --compdb-path=compile_commands.json
```

Indexing CUDA additionally needs `clang` on the `PATH`. The project describes itself as
beta.

### Ruby

Built on Sorbet, and self-describes as experimental. How much it can tell you depends on
how thoroughly the codebase has adopted Sorbet's `# typed:` sigils, so quality varies far
more between repositories than it does for the other indexers here.

### PHP

```bash
composer require --dev davidrjenni/scip-php
vendor/bin/scip-php
```

Must be run from the project root, with `composer.json`, `composer.lock`, an up-to-date
autoloader and the dependencies installed in `vendor/`.

### Dart

```bash
dart pub global activate scip_dart
cd path/to/project && dart pub get
dart pub global run scip_dart ./
```

### C# and Visual Basic

```bash
dotnet tool install --global scip-dotnet
scip-dotnet index
```

Sourcegraph's own .NET indexer, and the closest thing to a competitor vela has. It does
not index Razor views or Blazor components, because `ScipProjectIndexer` iterates
`project.Documents`, which is the set of files the compiler reads from disk; Razor reaches
the compilation as source-generated documents instead.

We have sent the fix upstream:
[sourcegraph/scip-dotnet#117](https://github.com/sourcegraph/scip-dotnet/pull/117). See
[the upstream write-up](upstream/scip-dotnet-razor.md).

### SQL

**There is no SCIP indexer for SQL that we could find.** SQL is also absent from
Sourcegraph's own table of indexers, which lists Go, TypeScript and JavaScript, C and C++,
Java, Scala, Kotlin, Rust, Python, Ruby and C#, and from the list in the protocol
repository's README. If a repository holds SQL, no `.scip` exists to import for it, and
`vela index` will say so by name when a `vela.json` is present: the language census reports
what a repository is written in that no job covers.

## The protocol

[`scip-code/scip`](https://github.com/scip-code/scip), Apache 2.0, v0.9.0 (29 Jun 2026). The repository holds the protobuf
schema (`scip.proto`), bindings for Go, Rust, TypeScript, Haskell, Java and Kotlin, and the
`scip` CLI.

The CLI is worth having when you are debugging an import:

| Command | What it does |
|---|---|
| `scip lint` | flag potential issues with an index |
| `scip print` | print an index for debugging |
| `scip snapshot` | generate snapshot files for golden testing |
| `scip stats` | statistics about an index |
| `scip test` | validate an index against test files |
| `scip expt-convert` | experimental: convert an index to a SQLite database |

### Governance

On 25 March 2026 Sourcegraph published "The future of SCIP", announcing a decision to
"transition it from a Sourcegraph-owned project to an independent one with an open
governance structure". The inaugural Core Steering Committee is Catherine Gasnier of Meta,
Jamy Timmermans of Uber and Michal Kielbowicz of Sourcegraph. Protocol changes now go
through a "SCIP Enhancement Proposals (SEP) process": a proposal is filed as an issue,
debated publicly, and explicitly marked Accepted, Deferred or Rejected by the committee
before implementation work starts.

This matters to anyone betting on the format. The risk with SCIP was always that a
commercially distracted Sourcegraph would let it rot. That risk is now much reduced.

LSIF is SCIP's superseded predecessor. New work should target SCIP.

### Other consumers

vela is not the only thing reading `.scip`. Sourcegraph itself, Mozilla's Searchfox
(`mozsearch/mozsearch` carries a `scip-indexer` and a `scip-analyze.sh`) and Meta's Glean
(which documents SCIP-based Python and .NET indexing, and carries a `scip.angle` schema)
all consume the format, and `rust-analyzer` emits it. A format with several independent
consumers is a format that keeps working.

## Worth knowing about: scip-io

[`GlitterKill/scip-io`](https://github.com/GlitterKill/scip-io) orchestrates several
indexers over one repository and merges the output into a single index. MIT licensed. That
is exactly the job vela deliberately does not do: vela consumes merged output rather than
running other people's indexers.

It is young and small (7 stars as of 30 July 2026), so treat this as a pointer rather than
a recommendation. If it matures it is the natural front end for vela's `import` verb.

## How to re-check this page

Everything above is checkable from a terminal in about a minute.

```bash
# Latest release and last commit for one indexer
gh api repos/scip-code/scip-java --jq '"\(.full_name) pushed=\(.pushed_at)"'
gh api repos/scip-code/scip-java/releases/latest --jq '"\(.tag_name) \(.published_at)"'

# Which org a repository really lives in (the API follows the redirect)
gh api repos/sourcegraph/scip-java --jq .full_name

# npm-released indexers have no GitHub release to read
npm view @sourcegraph/scip-python version

# The protocol's own list of indexers
gh api repos/scip-code/scip/contents/README.md --jq .content | base64 -d
```

If a row here has gone stale, correct it and move the "last checked" date. A catalogue
that quietly ages is worse than no catalogue, because people trust it.
