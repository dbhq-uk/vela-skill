# Consuming SCIP, extending scip-dotnet, and configuring vela for a polyglot repository

**Research report, 29 July 2026**
Mode: deep. Prepared for a decision about vela's architecture.

---

## Executive Summary

Three questions were put: can vela consume `.scip` files from other indexers and so serve every language SCIP supports; can `scip-dotnet` be extended to index Razor; and does a config file selecting languages make sense. The answers are yes, yes, and yes, but the reasoning that matters is in the detail, and two findings change the shape of the plan.

The first is that SCIP is a materially safer bet than it was six months ago. On 25 March 2026 Sourcegraph moved SCIP out of its own ownership into independent governance with a Core Steering Committee drawn from Meta, Uber and Sourcegraph, and an RFC process [1]. The risk that mattered most, that a commercially distracted Sourcegraph would quietly let the format rot, is now substantially retired.

The second is that `scip-dotnet` is Razor-blind for exactly the reason the design notes claimed, and this report verifies it from source rather than inference. `ScipProjectIndexer.cs:110` reads `foreach (var document in project.Documents)` [2]. The only occurrence of the word "Razor" anywhere in the repository is a generated protobuf enum constant, `Razor = 62`, which the indexer never emits [2]. The hand-written codebase is about 1,163 lines excluding generated protobuf, which makes it small enough to contribute to comfortably.

The recommendation is: **do not fork.** Contribute the Razor change upstream, port `ScipSymbol.cs` into vela to fix its one known interoperability defect, and build the `.scip` importer. Forking buys a symbol-moniker implementation that is 66 lines and Apache-2.0 licensed, at the cost of inheriting 33 open issues including "C# indexing doesn't work properly" [3]. vela's own harvest is better tested today than the thing it would be adopting.

On the polyglot question, an honest count of ScentVerdict tempers expectations: 5,695 of its 5,775 Python files are vendored `venv`/`site-packages`. The first-party reality is 1,866 C# files at 375,608 lines, 307 Razor views at 58,788 lines, then 151 JavaScript, 80 Python, 30 TypeScript, 40 SQL and 3 Java. Multi-language support is worth having, and it is not where the value of this repository lives.

---

## Introduction: scope and method

The question was decomposed into four angles: the health and membership of the SCIP indexer ecosystem; the SCIP schema as seen by a consumer merging several indexes; the feasibility of a Razor extension to `scip-dotnet`; and prior art in polyglot config design. Four retrieval agents ran in parallel, returning structured evidence rather than prose. The two load-bearing claims, SCIP's governance change and the existence of a polyglot orchestrator, were re-verified directly. The single most decision-relevant claim, that `scip-dotnet` iterates on-disk documents only, was verified by cloning the repository and reading the code, because the retrieval agent could not fetch raw source and said so.

Local analysis of `/home/devops/scentverdict` and of vela's own source was done directly.

Assumption stated up front: "which languages does ScentVerdict use" is taken to mean first-party code, not vendored dependencies. That distinction turns out to carry most of the weight of the answer.

---

## Main Analysis

### Finding 1: SCIP's governance risk has been retired, and the ecosystem is alive

The most important thing to know before building on SCIP is whether it will still be maintained in three years. Six months ago the honest answer was uncertain. It is now considerably better.

Sourcegraph published "The future of SCIP" on 25 March 2026, announcing a decision to "transition it from a Sourcegraph-owned project to an independent one with an open governance structure" [1]. The inaugural Core Steering Committee is Catherine Gasnier of Meta, Jamy Timmermans of Uber and Michal Kielbowicz of Sourcegraph, with a "SCIP Enhancement Proposals (SEP) process" for protocol changes [1]. Sourcegraph states it "remains a deeply committed and active member of the community; we will continue to use and invest heavily in SCIP" [1].

A committee containing Meta and Uber matters more than the wording. Meta's Glean consumes SCIP for Go, Java, Rust and TypeScript [4], and Meta's own engineering blog reports SCIP is "8x smaller, and can be processed 3x faster in comparison with LSIF" [4]. Mozilla's Searchfox indexes Firefox's Java and Kotlin through `scip-java` [5]. These are organisations with a structural interest in the format continuing.

The indexers themselves show recent activity. `scip-java` reached v0.13.1 on 2 July 2026 and has moved to a `scip-code` organisation with a Kotlin CLI rewrite [6]. `scip-go` was updated on 21 July 2026 [7]. `rust-analyzer` merged a SCIP improvement on 4 July 2026 [8]. `scip-clang` covers C, C++ and CUDA on Clang 21 [9]. The official site lists indexers spanning C#, Visual Basic, C++, C, Dart, Go, Java, Scala, Kotlin, PHP, Python, Ruby, Rust and TypeScript/JavaScript [10].

Two caveats worth carrying. `scip-python` has no published GitHub releases despite active development [11], so pinning a version means pinning a commit. And a successor protocol called LIP claims to address "SCIP's need for full repository re-indexing, which can take 30-90 minutes on large repositories" [12]. That claim is single-sourced from a project documentation site and should be treated as a weak signal, not a reason to hesitate, but the underlying complaint about full re-indexing is real and is the same one vela has deferred.

**Implication.** Building vela as a SCIP consumer is betting on a format with institutional backing beyond its originator. That is the right bet.

---

### Finding 2: scip-dotnet is Razor-blind, verified from source, and the fix is larger than one line

The design notes assert that `scip-dotnet` misses Razor because of a single loop. Cloning the repository at commit `4788446` (27 May 2026) confirms it exactly. `ScipProjectIndexer.cs` line 109 logs `Found {project.Documents.Count()} documents`, and line 110 is:

```csharp
foreach (var document in project.Documents)
```

`project.Documents` returns on-disk files only. Razor views and Blazor components reach the compilation as source-generated documents, retrieved by the separate `GetSourceGeneratedDocumentsAsync` API [13]. A grep of the entire hand-written codebase for `razor`, `cshtml`, `sourcegenerat` or `SyntaxTree` returns exactly one hit, and it is in the generated protobuf file: `[pbr::OriginalName("Razor")] Razor = 62` [2]. In other words the SCIP format has a Razor language constant and `scip-dotnet` never emits it. There are no open issues or pull requests in that repository mentioning Razor, Blazor, cshtml or source generators [3].

The extension looks deceptively easy. `IndexDocument` takes a Roslyn `Document`, and `SourceGeneratedDocument` derives from `Document`, so concatenating the two sequences would compile and would produce documents. That is the trap. Two things break:

First, the path filter. Line 111 gates every document on `options.Matcher.Match(options.WorkingDirectory.FullName, document.FilePath)`. A source-generated document's `FilePath` points under `obj/`, so a user's `--exclude` patterns, and any sane default, would drop them again.

Second, and more seriously, `RelativePath` is computed as `Path.GetRelativePath(options.WorkingDirectory.FullName, document.FilePath)`. For a generated Razor document that yields a path like `obj/Debug/net10.0/.../Pages_Index_cshtml.g.cs`. That file **does not exist on disk** unless `EmitCompilerGeneratedFiles` is set. An index full of paths nobody can open is not a fix; it is the same defect wearing a different hat.

So the genuine upstream contribution is two pieces, not one: enumerate source-generated documents, **and** map their positions back through `#line` directives to the originating `.cshtml` or `.razor`. Those are precisely the two things vela has already built and covered with tests, which is a strong argument for being the one to contribute them.

Practical facts for a contribution. The repository is Apache 2.0 [14]. Last commit 24 July 2026, latest release v0.2.14 on 5 May 2026 updating to .NET SDK 10, so it is actively maintained [3]. No CLA is documented; a Code of Conduct and a `Development.md` are referenced [15]. Visual Basic arrived in v0.2.0 via a separate `ScipVisualBasicSyntaxWalker`, mirroring the C# walker [16] and the VB work vela recently did.

---

### Finding 3: fork versus contribute, and the 66 lines worth taking either way

The attraction of forking is that `scip-dotnet` already emits proper SCIP symbol monikers, which vela does not. That is vela's one acknowledged interoperability defect, named in its own implementation plan's self-review.

Reading the code deflates the argument. The entire moniker implementation is `ScipSymbol.cs`, 66 lines, a clean rendering of the descriptor grammar [2]:

```csharp
SymbolDescriptor.Types.Suffix.Package => EscapedName(desc) + '/',
SymbolDescriptor.Types.Suffix.Type    => EscapedName(desc) + '#',
SymbolDescriptor.Types.Suffix.Term    => EscapedName(desc) + '.',
SymbolDescriptor.Types.Suffix.Method  => EscapedName(desc) + '(' + (desc.Disambiguator ?? "") + ").",
```

with packages formatted as `"scip-dotnet nuget " + name + " " + version + " "`. This matches the grammar in the schema, where `<symbol> ::= <scheme> ' ' <package> ' ' (<descriptor>)+ | 'local ' <local-id>` [17]. vela's equivalent, `SymbolIdentity.For`, is a single method with a clean seam, so porting is a contained change rather than a rewrite. Apache 2.0 permits it with attribution.

Against forking: the repository carries 33 open issues, including "#62 C# indexing doesn't work properly" and "#85 Symbol doesn't contain full namespace name" [3]. Adopting the fork means adopting those. Meanwhile vela's harvest currently passes 89 tests including exact-count coverage assertions for Razor and Blazor. Replacing a tested component with an untested-by-you one, to gain 66 lines you could copy, is a poor trade.

**Recommendation: contribute, do not fork.** Send the source-generated-document enumeration plus `#line` mapping upstream as one focused pull request. Port the moniker construction into vela separately. If upstream declines or stalls, nothing is lost, because vela keeps its own harvest either way.

---

### Finding 4: the .scip importer is small, and three hazards are not

The importer is genuinely cheap. A `.scip` file is a serialised `Index` protobuf message. vela already vendors the full schema, already references `Google.Protobuf` 3.35.1, and `ScipLoader.Load` already takes a `Scip.Index`. Parsing a foreign index and handing it to the existing loader is a small amount of code. The design notes' claim that vela "consumes indexes produced by anyone" is close to true; it simply has not been wired up.

Three hazards are real and none is visible from the happy path.

**Position encoding is a correctness bug waiting to happen.** The schema instructs indexers to choose an encoding by implementation language: "For an indexer implemented in JVM/.NET language or JavaScript/TypeScript, use UTF16CodeUnitOffsetFromLineStart. For an indexer implemented in Python, use UTF32CodeUnitOffsetFromLineStart. For an indexer implemented in Go, Rust or C++, use UTF8ByteOffsetFromLineStart" [17]. So a merged index legitimately contains documents whose `character` values are counted in three different units. vela's `document` table stores `id`, `relative_path`, `language` and `generated`, and no encoding column. Merge a `scip-python` index today and every column number on a line containing a non-ASCII character is silently wrong. The fix is to store `position_encoding` per document at import and normalise on read.

**Local symbols collide.** SCIP represents document-scoped entities as `local <id>`, and the schema states they "MUST only be used for entities which are local to a Document, and cannot be accessed from outside the Document" [17]. Those identifiers are counters, so `local 1` from a Python file and `local 1` from a TypeScript file are unrelated. vela keys occurrences by symbol string globally. Importing without namespacing locals per document would merge unrelated variables, which is the same class of defect vela just fixed for C# locals.

**Project roots differ.** Each index carries its own `project_root`, and "All documents in this index must appear in a subdirectory of this root directory" [17]. Indexes produced by different tools over different subtrees will have different roots, so `relative_path` values are not comparable until rebased onto a common root.

Two pieces of good news. SCIP has a `Generated = 0x10` symbol role, described as "Is the symbol in generated code?" [17]. vela's recently added `generated` column should map onto it in both directions, which makes the generated-code handling standards-based rather than a local invention. And the format is designed for streaming: "An `Index` message payload can have a large memory footprint and it's therefore recommended to emit and consume an `Index` payload one field value at a time" [17], with `metadata` required at the start and appearing once [17]. For context on scale, SCIP files "can exceed 60MB" for large repositories [18].

Worth knowing about the neighbours. The `scip` CLI has no merge command; documented commands are lint, print, snapshot, stats, test and `expt-convert` [19]. That last one already converts SCIP into SQLite, but it stores "occurrences opaquely as a blob to prevent the DB size from growing very quickly" [19], which means it cannot answer the queries vela answers. That is a useful confirmation that vela's loader is doing something the reference tooling does not.

A polyglot orchestrator also already exists. `scip-io` is "a polyglot SCIP index orchestrator written in Rust" that detects languages, installs indexer binaries, runs them and merges the results, claiming deterministic byte-identical output [20]. It covers 11 languages across 9 indexers and is MIT licensed [20]. It has 7 stars, so it is nascent rather than established, and betting a product on it would be premature. But it is the right shape, and it argues that vela should consume merged `.scip` rather than reimplement orchestration.

---

### Finding 5: the polyglot payoff on ScentVerdict is real but smaller than it looks

A naive file count of `/home/devops/scentverdict` reports 5,775 Python files against 1,866 C#, which would suggest Python is the dominant language. It is an artefact. 5,695 of those Python files live in `venv` or `site-packages`. The first-party picture:

| Language | Files | Lines | Indexer available |
|---|---|---|---|
| C# | 1,866 | 375,608 | vela (or scip-dotnet) |
| Razor (`.cshtml`) | 307 | 58,788 | **vela only** |
| JavaScript | 151 | - | scip-typescript |
| Python | 80 | - | scip-python |
| SQL | 40 | - | **none exists** |
| TypeScript | 30 | - | scip-typescript |
| Java | 3 | - | scip-java |

The C# and Razor figures match the design notes' measurements exactly, which is a good independent check on both. The JavaScript and TypeScript are concentrated in two places: 96 files in `src/ScentVerdict.Mobile`, a Capacitor app, and 81 in `src/ScentVerdict.Web`. The Python is entirely tooling under `tools/` and `scripts/`, not product code. No SCIP indexer exists for SQL, so those 40 files stay grep-only.

So multi-language support would add meaningful coverage of the mobile app and the web front-end scripts, plus incidental coverage of build tooling. That is worth having. It is not comparable in value to the 375,608 lines of C# and 58,788 lines of Razor that vela already indexes and that nothing else does.

One incidental relief: `ScentVerdict.sln` sits at the git root, so the `project_root` concern deferred during implementation, where a solution below the repository root strands files outside the index, does not fire on this repository.

---

### Finding 6: the config file, and what the vendored-Python count teaches about defaults

A config file makes sense, and the strongest argument for it is the 5,695 vendored Python files. Any tool that scans by extension without opinionated default excludes will spend most of its time indexing dependencies and will report a language profile that is simply false. Defaults are the feature; the config is how you override them.

Prior art points in a consistent direction.

Sourcegraph's auto-indexing configuration is JSON with an `index_jobs` array, one entry per language or module, each naming an indexer image, its arguments and a root [21]:

```json
{"index_jobs": [{"indexer": "sourcegraph/scip-go:v0.1@sha256:39c1495...",
                 "indexer_args": ["scip-go", "-q"], "root": "dev/sg"}]}
```

That shape, a list of jobs rather than a flat language list, is the right one for vela, because "which language" and "which indexer produced it" are separate facts, and a repository can need two jobs for one language at different roots.

Universal ctags offers the cumulative-selection idea, `--languages=+C,+Java,+Python`, where `+` adds and `-` removes from a default set [22]. ripgrep pairs a type system with globs, `--type-add web:*.{html,css,js,jsx,ts,tsx}` alongside `--glob=!node_modules/*` [23]. Both are worth stealing: a sensible default set that users adjust, rather than a list they must write from scratch.

On glob semantics, git's rules are the ones developers already know, and the pitfall is documented there: "An optional prefix `!` negates the pattern... However, it is not possible to re-include a file if a parent directory of that file is excluded" [24]. Whatever vela does, it should either follow that rule or document loudly that it does not.

On format, the .NET ecosystem has a clear convention and it is JSON. `global.json` configures the SDK [25]; the local tools manifest is `dotnet-tools.json` under a `.config` directory [26]. EditorConfig is the INI-style exception and is scoped to editor behaviour, using glob sections like `[*.cs]` and `[*.vb]` [27]. A .NET developer will expect JSON in a named file, and `vela.json` at the solution root, or `.config/vela.json` following the tools-manifest precedent, will surprise nobody.

A sketch consistent with all of the above, and with vela's constraints:

```json
{
  "version": 1,
  "solution": "ScentVerdict.sln",
  "jobs": [
    { "language": "csharp", "indexer": "vela", "root": "." },
    { "language": "typescript", "indexer": "scip-typescript",
      "root": "src/ScentVerdict.Mobile", "index": "index.scip" },
    { "language": "python", "indexer": "scip-python", "root": "tools" }
  ],
  "exclude": ["**/obj/**", "**/bin/**", "**/node_modules/**",
              "**/site-packages/**", "**/venv/**", "**/*.min.js"]
}
```

Two design points follow from vela's own constraints rather than from prior art. Constraint 3 says an incomplete index must never look like a complete one, so a configured job that fails to run has to mark the index degraded, exactly as a failed project load already does; a config file must not become a quiet way to lose a language. And Constraint 1 says deterministic only, so language selection must be explicit or defaulted, never inferred by sampling file contents.

---

## Synthesis and Recommendations

The two sides of this reinforce each other, and the ordering matters.

**Do first, because everything else depends on it: replace `SymbolIdentity.For` with real SCIP monikers,** porting the approach from `ScipSymbol.cs` under Apache 2.0 attribution. It is 66 lines of reference implementation against a single clean seam in vela. Until this is done, vela's output cannot interoperate with anything, an imported foreign index cannot be linked to vela's own symbols, and any upstream contribution is blocked. This is the keystone.

**Do second: the `.scip` importer,** with the three hazards handled explicitly. Store `position_encoding` per document and normalise on read. Namespace `local N` symbols by document. Rebase `relative_path` onto a common root. Map SCIP's `Generated` role onto vela's `generated` column in both directions. The parsing itself is trivial because the schema is already vendored and the loader already takes an `Index`.

**Do third: the config file,** as a jobs array in JSON at the solution root, with opinionated default excludes, cumulative language selection, and a failed job marking the index degraded.

**Do fourth, and separately: the upstream contribution to `scip-dotnet`,** as source-generated enumeration plus `#line` mapping in one pull request. It is genuinely valuable to the ecosystem, the repository is active and permissively licensed, and there is no competing work. But it is not on vela's critical path, and treating it as such would be a mistake.

**Do not fork `scip-dotnet`.** The thing worth having from it is 66 lines. The thing that comes with it is 33 open issues and a harvest less well tested than the one vela already has.

On testing against ScentVerdict, the sequence should be: index the C# and Razor with vela first, because that is 434,000 lines and the claim the tool is built on; then add TypeScript for the mobile app once the importer exists. Expect the polyglot addition to be a modest broadening rather than a transformation.

---

## Limitations and caveats

The `scip-dotnet` analysis is from a shallow clone at commit `4788446`, dated 27 May 2026, while the repository's last push was 24 July 2026; a small amount of drift is possible, though not in the architecture described. The assertion that adding source-generated documents "would compile" is reasoned from Roslyn's type hierarchy and has not been built and run, so it is a strong expectation rather than a demonstrated fact.

The LIP successor-protocol claim rests on a single project documentation site and is reported here at low confidence [12]; it should not influence the decision either way without corroboration. `scip-io` was assessed from its README only, and at 7 stars it has no track record.

No indexer was actually run in the course of this research, so no real symbol string from `scip-python` or `scip-typescript` was inspected. The cross-language merge hazards are derived from the specification rather than observed in practice. Before committing to the importer design, running two indexers over a small polyglot fixture and reading the raw output would be cheap and would either confirm or correct the position-encoding and local-symbol analysis.

The ScentVerdict figures are file and line counts, not a judgement about which code matters. Forty SQL files may be more important to that product than 151 JavaScript files.

---

## Bibliography

[1] Sourcegraph, "The future of SCIP", 25 March 2026. https://sourcegraph.com/blog/the-future-of-scip
[2] sourcegraph/scip-dotnet source, cloned at commit 4788446, 27 May 2026. https://github.com/sourcegraph/scip-dotnet
[3] scip-dotnet repository metadata and issues, GitHub API, July 2026. https://github.com/sourcegraph/scip-dotnet/issues
[4] Meta Engineering, "Indexing code at scale with Glean". https://engineering.fb.com/2024/12/19/developer-tools/glean-open-source-code-indexing/
[5] mozsearch pull request 667, "Java and Kotlin support via scip-java". https://github.com/mozsearch/mozsearch/pull/667
[6] scip-java releases, scip-code organisation. https://github.com/scip-code/scip-java/releases
[7] scip-code/scip-go repository. https://github.com/scip-code/scip-go
[8] rust-analyzer pull request 22595, 4 July 2026. https://github.com/rust-lang/rust-analyzer/pull/22595
[9] sourcegraph/scip-clang repository. https://github.com/sourcegraph/scip-clang
[10] SCIP Code Intelligence Protocol, official site. https://scip-code.org/
[11] sourcegraph/scip-python releases. https://github.com/sourcegraph/scip-python/releases
[12] LIP protocol specification (single-sourced, low confidence). https://lip-sigma.vercel.app/docs/spec
[13] Roslyn issue 71581, source-generated documents API. https://github.com/dotnet/roslyn/issues/71581
[14] scip-dotnet LICENSE, Apache 2.0. https://github.com/sourcegraph/scip-dotnet/blob/main/LICENSE
[15] scip-dotnet README and contribution guidance. https://github.com/sourcegraph/scip-dotnet
[16] scip-dotnet v0.2.0 release notes, Visual Basic support. https://github.com/sourcegraph/scip-dotnet/releases/tag/v0.2.0
[17] scip.proto, SCIP schema specification comments (vendored copy verified locally at src/Vela/Scip/scip.proto). https://raw.githubusercontent.com/sourcegraph/scip/main/scip.proto
[18] SCIP design document. https://github.com/scip-code/scip/blob/main/docs/DESIGN.md
[19] SCIP CLI documentation. https://github.com/sourcegraph/scip/blob/main/docs/CLI.md
[20] GlitterKill/scip-io, polyglot SCIP index orchestrator. https://github.com/GlitterKill/scip-io
[21] Sourcegraph auto-indexing configuration reference. https://sourcegraph.com/docs/code_navigation/references/auto_indexing_configuration
[22] Universal Ctags manual. https://docs.ctags.io/en/latest/man/ctags.1.html
[23] ripgrep configuration file documentation. https://iepathos.github.io/ripgrep/configuration-file/
[24] git gitignore documentation, glob semantics. https://git-scm.com/docs/gitignore/2.1.4
[25] Microsoft Learn, global.json overview. https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
[26] Microsoft Learn, dotnet tool install and the local manifest. https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install
[27] Visual Studio EditorConfig documentation. https://learn.microsoft.com/en-us/visualstudio/ide/create-portable-custom-editor-options

---

## Methodology appendix

Run directory: `/home/devops/vela-skill/docs/research/SCIP_Multilanguage_Research_20260729/`. Output base resolved from the git root of the vela repository.

Four parallel retrieval agents on a cheaper model returned structured evidence for: the SCIP ecosystem and its maintenance status; the SCIP schema from a consumer's point of view; `scip-dotnet` feasibility; and config-file prior art. Twelve sources were registered to `sources.jsonl`.

Direct verification beyond the agents: the SCIP governance blog post and the `scip-io` README were re-fetched and read; `sourcegraph/scip-dotnet` was cloned and its source read directly, because the retrieval agent reported it could not fetch raw source and its conclusions on that point were therefore inferential; the schema quotations on position encoding, symbol grammar and the `Generated` role were checked against the copy vendored in vela at `src/Vela/Scip/scip.proto`; and the ScentVerdict language profile was measured locally, which is where the vendored-Python artefact was found.

Built-in web search was unavailable throughout this run because of a harness error, so all search-based retrieval went through subagents. That is a coverage limitation worth noting: query formulation was delegated rather than iterated directly.
