# Who else does what vela does

*standard · 4 angles · 28 logged sources · star counts and licences re-verified against the GitHub API on 3 September 2026 · 1 finding runs against the premise of the tool*

## Executive summary

The category vela sits in did not exist in this shape a year ago and is now
crowded. The useful split is not vela against one rival, but three families of
tool that answer "where is this used" in ways that are not equally true.

**Syntactic graph builders** parse with tree-sitter and match by name. They are
winning distribution by a wide margin and they are open about their accuracy
ceiling.

**Language-server bridges** drive a real compiler through LSP. They are
compiler-exact, they are multi-language, and the largest of them is far larger
than anything else in this report.

**Precise indexers** run the compiler and emit an index. This is vela's family,
and it is the smallest and least adopted of the three.

Two findings matter more than the rest. First, **vela is not the only
compiler-exact option for C# any more**: there are at least a dozen Roslyn MCP
servers [10][11][12], plus an Anthropic-verified C# plugin with 43,741 installs
in the exact channel vela distributes through [14]. Second, **Razor genuinely
does remain uncovered by agent tooling** [22][23], but for a more specific reason
than "nobody supports it", and the honest version of that claim is narrower than
the docs currently imply.

One finding cuts against the premise of building an index at all, and it is in
Finding 6 rather than buried.

## Introduction

vela is a .NET global tool that uses Roslyn to build a compiler-exact SCIP and
SQLite index of C#, Visual Basic, Razor Pages, MVC views and Blazor components,
distributed as an agent skill. The question this run answers is who else serves
the same need for an AI coding agent - finding definitions, references, callers
and change impact - and where that leaves vela's claim to be different.

Four angles were worked: MCP servers offering semantic navigation; compiler-
accurate C# tooling specifically; tree-sitter graph builders; and whether
anything at all resolves references inside Razor.

## Comparison matrix

| Tool | Resolution | Scale | Languages | C# engine | Razor | Licence |
|---|---|---|---|---|---|---|
| **vela** | Roslyn, compiler-exact | not published, no registry listing [14] | C#, VB, Razor, Blazor | Roslyn `MSBuildWorkspace` | yes, via source-generated documents | MIT |
| Serena | LSP, compiler-exact | 28,783★ [1][2] | 40+ | Microsoft Roslyn language server | [unknown] | MIT |
| CodeGraph | tree-sitter, name matching | 69,439★ [3][4] | 38 | tree-sitter grammar | no, roadmap request only | MIT |
| Graphify | AST, deterministic | 114,322★ [27] | [unknown] | [unknown] | [unknown] | Apache-2.0 |
| GitNexus | AST, in-browser | 46,964★ [28] | [unknown] | [unknown] | [unknown] | NOASSERTION |
| repowise | tree-sitter | 6,314★ [5] | 19 | tree-sitter grammar | [unknown] | AGPL-3.0 |
| CodeGraphContext | tree-sitter, optional SCIP | 4,154★ [6] | [unknown] | tree-sitter, or a SCIP indexer | [unknown] | MIT |
| mcp-language-server | LSP, compiler-exact | 1,586★ [8] | any with an LSP | whichever server you point it at | [unknown] | BSD-3-Clause |
| csharp-ls | Roslyn, compiler-exact | 985★ [13] | C# only | Roslyn | [unknown] | MIT |
| codanna | syntactic, inferred | 734★ [7] | 15 | [unknown] | [unknown] | Apache-2.0 |
| roslyn-codelens-mcp | Roslyn, compiler-exact | 48★ [11] | C# only | Roslyn | [unknown] | MIT |
| scip-dotnet | Roslyn, compiler-exact | 33★ [16] | C#, VB | Roslyn | no, the gap vela exists for | Apache-2.0 |
| SharpLensMcp | Roslyn, compiler-exact | 32★ [10] | C# only | Roslyn | [unknown] | MIT |
| WarpGrep | none, grep and read | [unknown] [19] | any | none | n/a, reads raw files | proprietary |

Star counts are from the GitHub REST API on 3 September 2026. Treat the three
largest as reported rather than corroborated: Graphify reaching 114,322 stars in
five months [27] and GitNexus 46,964 [28] are both implausible growth curves, and
no independent audit of star provenance was found.

## Findings

## Finding 1: Compiler-exact C# tooling for agents is crowded and fragmented

**Confidence: Moderate** - a dozen project pages read directly, but the whole
enumeration comes from one angle, so nothing independent corroborates it.

The premise that vela occupies an empty niche is out of date. Roslyn-backed MCP
servers for C# now include SharpLensMcp, offering "92 AI-optimized tools for
.NET/C# semantic code analysis, navigation, refactoring, and code generation
using Microsoft Roslyn" [10]; roslyn-codelens-mcp, giving "AI agents deep
semantic understanding of .NET codebases - type hierarchies, call graphs, DI
registrations, diagnostics, refactoring" [11]; and MadQ/RoslynMcp, offering "real
Roslyn compiler semantics: type resolution, cross-file references, semantic
rename, diagnostics, and 43 MCP tools" [12]. Several more exist at comparable
scale. Microsoft has moved in the same direction inside its own IDE rather than
as a standalone server: `find_symbol` "exposes rich, language-specific symbol
information to Copilot Agent Mode" [24].

The fragmentation is the finding. Every one of them is under sixty stars, they
duplicate each other closely, and no consolidation or de facto winner was found.
That is a market with no incumbent rather than a market with no entrants.

## Finding 2: Serena is the serious semantic competitor, and it is very large

**Confidence: Strong** - the project's own README and the source file naming its
C# engine, plus API-verified metadata.

Serena has 28,783 stars, an MIT licence, and was pushed the day of this research.
It "incorporates a powerful abstraction layer for the integration of language
servers that implement the language server protocol (LSP)" [1], covering over 40
programming languages.

For C# it uses Microsoft's own Roslyn language server, per its source: "CSharp
Language Server using Roslyn Language Server (Official Roslyn-based LSP server
from NuGet.org)" [2]. That makes it compiler-exact for C#, not an approximation.
It is the one tool in this report that is both semantically honest and widely
adopted, and it is the competitor worth taking seriously rather than CodeGraph.

The same pattern repeats at smaller scale: mcp-language-server gives clients
"semantic tools like get definition, references, rename, and diagnostics" [8],
and agent-lsp exposes a `blast_radius` call returning "all exports + all callers"
where "without orchestration: 20+ sequential LSP calls" would be needed [9].
Both reach compiler accuracy by driving a language server rather than building
an index.

The index-building route is not absent, but it is commercial: Sourcegraph's own
MCP server offers "go-to-definition and find-all-references across repositories,
powered by precise code indexing (SCIP, an open standard for code
intelligence)" [17]. That is vela's family, sold rather than self-hosted.

## Finding 3: There is already a verified C# plugin in vela's own distribution channel

**Confidence: Moderate** - one registry page, which carries no date, and no
independent corroboration of the install figure.

An Anthropic-verified Claude Code plugin provides "C# language server integration
for Claude Code, providing rich code intelligence for C# projects", reporting
43,741 installs [14]. It is backed by csharp-ls [13], itself Roslyn-based.

vela ships as a skill or plugin into the same place. This is the most direct
competitive fact in the report, and it was not visible from inside the project.

## Finding 4: Razor is genuinely uncovered by agent tooling, for a specific reason

**Confidence: Moderate** - strong on the absence in indexers and agent tools,
weaker on IDEs, and one contradiction was found and not resolved.

The claim holds where vela makes it, but the shape matters:

- **GitHub's own code navigation** lists C# among its supported languages and
  does not list Razor [22].
- **CodeGraph** lists C# with the `.cs` extension as "Full support" in its README
  language table [3]. Razor and Blazor appear only in issue #648, whose title is
  "Tracking: language support **requests** (post-1.0 roadmap)" [4]. This
  contradiction was not resolved and no test of CodeGraph on a `.cshtml` file was
  run.
- **tree-sitter's Razor grammar** is real but marked `unstable`, with a query set
  of highlights, folds and injections and no `locals` query [23]. Locals is the
  query that carries scope and binding, so the grammar supports colouring rather
  than reference resolution.
- **scip-dotnet** describes itself as an indexer "for the C# and Visual basic
  programming languages" [16], with no mention of Razor.

The complication, and it is a real one: Microsoft's own `roslyn-language-server`
package describes itself as "A Language Server Protocol (LSP) implementation for
C# **and Razor** powered by Roslyn" [15]. Anything driving that server, Serena
included, may inherit some Razor capability.

Against that, finding references *into* Razor is a long-standing open defect
rather than a shipped feature. dotnet/vscode-csharp#7590 is titled "'Find
references' does not show refs in .cshtml/razor files" [20], and dotnet/razor#9369
reports results pointing at `razor__virtual.cs` instead of real locations [21].

**The defensible claim is narrower than "nothing else indexes Razor".** It is
that no standalone indexer or agent tool demonstrably resolves references into
`.cshtml` and `.razor`, and that the compiler-side support Microsoft advertises
has known open defects in exactly this operation.

## Finding 5: The syntactic camp admits its own accuracy ceiling

**Confidence: Strong** - a self-reported figure stated against the project's own
interest, corroborated by two other projects on the same angle.

repowise publishes: "of the call edges we draw, about fifteen percent are wrong,
and on `seastar` CodeGraph grades better than we do" [5].

Sourcebot is equally direct that its navigation is "search-based, meaning it uses
the same code search engine and query language to **estimate** a symbol's
references and definitions" [18].

CodeGraphContext goes further and treats the ceiling as a reason to reach for
this ecosystem: with `SCIP_INDEXER=true`, "some languages use external SCIP
indexers for more accurate calls and inheritance than Tree-sitter heuristics
alone" [6]. That is a tree-sitter tool reaching for precise indexes when accuracy
matters, which is the clearest external validation of vela's premise found here.

An academic evaluation of the same approach reports "83% answer quality versus
92% for a file-exploration agent, at ten times fewer tokens and 2.1 times fewer
tool calls" [25] - a real efficiency win bought with a real accuracy loss.

## Finding 6: Against the premise, indexing may not be what wins

**Confidence: Moderate** - two independent strands, neither reproduced here.

WarpGrep ships with "No embeddings, no indexing" [19], working instead by a
dedicated LLM call that runs grep and file-read operations and reasons about
relevance. If a pure grep-and-reason loop competes on end-task benchmarks, the
precision of an index is not obviously the binding constraint.

Second, several Roslyn MCP projects warn in their own READMEs that agents default
to grep and ignore MCP tools unless explicitly instructed via `CLAUDE.md` or
`AGENTS.md`; one ships a `PreToolUse` hook to force compliance [12]. A correct
tool the agent does not call is worth nothing, which makes vela's skill-based
distribution, where usage instructions travel with the tool, a genuine advantage
over shipping a bare MCP server.

The independent framing comes from a June 2026 survey: "MCP only defines how an
agent calls tools. It does not decide whether a find references result came from
a real language server, a Tree-sitter approximation, a stale embedding index or a
vendor knowledge graph" [26].

## Synthesis

vela's differentiation is real but narrower than the project's own docs imply,
and it is narrowing in a specific direction.

**What is not differentiating any more:** being Roslyn-backed. A dozen MCP
servers do that [10][11][12], Serena does it at 28,783 stars [1][2], and there is
a verified plugin with 43,741 installs doing it in vela's own channel [14].

**What remains genuinely differentiating:** Razor and Blazor via source-generated
documents. No competitor was found that demonstrably resolves references into
`.cshtml` or `.razor`; the tree-sitter grammar cannot do it in principle [23]; and
Microsoft's own stack has the capability open as a defect [20][21]. This is a
real moat, and it is a narrow one.

**What is differentiating and undersold:** determinism, and the distribution
model. Every syntactic competitor either publishes an error rate [5] or declines
to measure one, while vela's matching is exact by construction. And shipping as a
skill puts usage instructions in front of the agent, which Finding 6 suggests is
the actual failure mode for competitors.

## Limitations

- **No head-to-head benchmark exists.** No third-party comparison of find-
  references accuracy across semantic and syntactic tools was found. Every
  accuracy figure here is self-published by the project claiming it, except [25].
- **vela's own position is unmeasured.** vela is not published to any registry,
  so it has no install count to set against the 43,741 above [14]. That absence
  is itself the comparison.
- **The CodeGraph Razor contradiction is unresolved.** Its roadmap issue lists
  Razor [4]; its README language table does not [3]. Nothing was run against a
  `.cshtml` file to settle it.
- **Three star counts are implausible** [27][28] and could not be corroborated.
- **Serena's Razor behaviour was not established** in either direction, and it is
  the single most decision-relevant unknown here, given it drives the Roslyn
  language server that advertises Razor support [15].

## Recommendations

1. **Stop leading with "compiler-exact".** It is table stakes now. Lead with
   Razor and Blazor, which is the claim that survives contact with this field.
2. **Settle the Serena question.** Point Serena at a Blazor app and see whether
   it resolves a reference into a `.razor` file. That single test decides whether
   vela's moat is Razor or nothing, and it is a morning's work.
3. **Soften the docs where they overreach.** The defensible sentence is that no
   standalone indexer or agent tool demonstrably resolves references into Razor,
   not that nothing indexes Razor at all.
4. **Treat distribution as the real gap.** The competitive facts that hurt are
   both distribution facts: 28,783 stars and 43,741 installs against an unlisted
   tool.

## Bibliography

[1] [oraios/serena](https://github.com/oraios/serena), README, pushed 2026-09-03.
[2] [Serena C# language server module](https://github.com/oraios/serena/blob/main/src/solidlsp/language_servers/csharp_language_server.py), 2026-09-03.
[3] [colbymchenry/codegraph](https://github.com/colbymchenry/codegraph), README, pushed 2026-08-31.
[4] [codegraph issue #648, language support requests](https://github.com/colbymchenry/codegraph/issues/648), 2026-06-02.
[5] [repowise-dev/repowise](https://github.com/repowise-dev/repowise), README benchmark section, pushed 2026-09-03.
[6] [CodeGraphContext](https://github.com/codegraphcontext/codegraphcontext), README, pushed 2026-09-02.
[7] [bartolli/codanna](https://github.com/bartolli/codanna), README, pushed 2026-08-29.
[8] [isaacphi/mcp-language-server](https://github.com/isaacphi/mcp-language-server), README, pushed 2026-03-01.
[9] [blackwell-systems/agent-lsp](https://github.com/blackwell-systems/agent-lsp), README, pushed 2026-09-03.
[10] [SharpLensMcp](https://github.com/pzalutski-pixel/sharplens-mcp), README, pushed 2026-08-16.
[11] [roslyn-codelens-mcp](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp), README, pushed 2026-09-03.
[12] [MadQ/RoslynMcp](https://github.com/MadQ/RoslynMcp), README, pushed 2026-09-03.
[13] [razzmatazz/csharp-language-server (csharp-ls)](https://github.com/razzmatazz/csharp-language-server), pushed 2026-08-31.
[14] [C# LSP plugin, Anthropic Verified](https://claude.com/plugins/csharp-lsp), no date on page.
[15] [roslyn-language-server on NuGet](https://www.nuget.org/packages/roslyn-language-server.linux-arm64/), published 2026-08-27.
[16] [sourcegraph/scip-dotnet](https://github.com/sourcegraph/scip-dotnet), pushed 2026-08-31.
[17] [Sourcegraph MCP Server](https://sourcegraph.com/mcp), no date on page.
[18] [Sourcebot code navigation](https://docs.sourcebot.dev/docs/features/code-navigation), no date on page.
[19] [WarpGrep, Morph documentation](https://docs.morphllm.com/sdk/components/warp-grep), no date on page.
[20] [dotnet/vscode-csharp#7590](https://github.com/dotnet/vscode-csharp/issues/7590), 2024-09-21.
[21] [dotnet/razor#9369](https://github.com/dotnet/razor/issues/9369), no date on page.
[22] [github/code-navigation](https://github.com/github/code-navigation), no date on page.
[23] [nvim-treesitter supported languages](https://github.com/nvim-treesitter/nvim-treesitter/blob/main/SUPPORTED_LANGUAGES.md), no date on page.
[24] [Visual Studio find_symbol announcement](https://devblogs.microsoft.com/visualstudio/unlock-language-specific-rich-symbol-context-using-new-find_symbol-tool/), 2026-02-11.
[25] [Codebase-Memory, arXiv 2603.27277](https://arxiv.org/html/2603.27277v1), 2026-03-28.
[26] [Code Intelligence and Code-Graph Indexing for AI Agents](https://anthonywest.co.uk/research/code-intelligence-indexing-2026-openai), 2026-06-03.
[27] [Graphify-Labs/graphify](https://github.com/Graphify-Labs/graphify), GitHub API, 2026-09-03.
[28] [abhigyanpatwari/GitNexus](https://github.com/abhigyanpatwari/GitNexus), GitHub API, 2026-09-03.
