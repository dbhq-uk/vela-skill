# Razor went missing on .NET SDK 10.0.400

On 9 August 2026 CI was green. On 17 August it was not, on all three runners, with four
failures and no change to vela. The machine and the GitHub runners had picked up .NET SDK
**10.0.400**, and on that SDK vela indexed **zero** Razor views and **zero** Blazor
components. On 10.0.101 it indexed 7 of 7 and 11 of 11.

That is the one capability vela exists for. This page is what broke, how it was proved,
what was done about it, and what a user should expect.

| | |
|---|---|
| Broke on | .NET SDK 10.0.400 (Razor compiler `10.400.26.38015`) |
| Last good | .NET SDK 10.0.101 (Razor compiler `10.0.25.57005`) |
| Failing tests | `DocumentEnumeratorTests.EnumerateAsync_IncludesOneGeneratedDocumentPerCshtml`, `DocumentEnumeratorTests.EnumerateAsync_IncludesBlazorComponents`, `ScipEmitterTests.EmitAsync_ProducesADocumentForEveryRazorView`, `RazorMapperTests.MapToOriginal_OnGeneratedRazorDocument_ReturnsTheCshtmlPath` |
| Root cause | Roslyn will not load a source generator built against a newer compiler than the host, and does not say so |
| Upstream | [dotnet/roslyn#84137](https://github.com/dotnet/roslyn/issues/84137), and before it [#84221](https://github.com/dotnet/roslyn/issues/84221) and [#77255](https://github.com/dotnet/roslyn/issues/77255) |
| Fixed in vela by | `6674f1e` (the pin) and `5573a4b` (the check that makes a recurrence loud) |
| Fix verified on | SDK 10.0.400, 442 passed, 0 failed, 3 skipped |

## What actually happens

Razor views and Blazor components are not files the compiler reads. They reach the
compilation as source-generated documents, produced by a generator that lives inside the
.NET SDK at
`Sdks/Microsoft.NET.Sdk.Razor/source-generators/Microsoft.CodeAnalysis.Razor.Compiler.dll`.
vela hosts its own Roslyn and asks the workspace for those documents. Roslyn has a rule
about which generators it will load:

> Analyzer assembly cannot be used because it references a newer version of the compiler
> than the currently running version.
>
> - `WRN_AnalyzerReferencesNewerCompiler`, in `Microsoft.CodeAnalysis.CSharp.dll`

The SDK raised the compiler its Razor generator is built against, and vela's did not
follow:

| SDK | Razor compiler | built against `Microsoft.CodeAnalysis` |
|---|---|---|
| 10.0.101 | `10.0.0.0` | **5.0.0.0** |
| 10.0.400 | `10.4.0.0` | **5.9.0.0** |

vela hosted `5.6.0.0`. It cleared the first bar and not the second, so Roslyn refused the
Razor generator - and refused it **silently**. `AnalyzerFileReference` raises
`AnalyzerLoadFailed` with `FailureErrorCode.ReferencesNewerCompiler` and returns zero
generators. `MSBuildWorkspace` raises no `WorkspaceDiagnostic` for it. So every project
compiled, every query answered, `vela index` reported a healthy index, and no `.cshtml` or
`.razor` file was in it.

## The evidence

A console app referencing the versions vela pins, opened against a scaffolded Razor Pages
solution, with the SDK varied by `global.json`.

```
SDK 10.0.101
  AnalyzerReferences:  20
  SourceGenerated:     8      <- Pages_Index_cshtml.g.cs, ...  7 of them Razor
  razor dll:           .../sdk/10.0.101/.../Microsoft.CodeAnalysis.Razor.Compiler.dll
  razor ver:           10.0.25.57005
  GetGenerators("C#")  -> 1

SDK 10.0.400
  AnalyzerReferences:  19
  SourceGenerated:     1      <- PublicTopLevelProgram.Generated.g.cs.  0 Razor
  razor dll:           .../sdk/10.0.400/.../Microsoft.CodeAnalysis.Razor.Compiler.dll
  razor ver:           10.400.26.38015
  GetGenerators("C#")  -> 0
  LOAD FAIL:           ReferencesNewerCompiler
```

Read out of the assemblies themselves, which is where Roslyn reads it:

```
.../sdk/10.0.101/.../Microsoft.CodeAnalysis.Razor.Compiler.dll
   def: Microsoft.CodeAnalysis.Razor.Compiler 10.0.0.0
   ref: Microsoft.CodeAnalysis 5.0.0.0
.../sdk/10.0.400/.../Microsoft.CodeAnalysis.Razor.Compiler.dll
   def: Microsoft.CodeAnalysis.Razor.Compiler 10.4.0.0
   ref: Microsoft.CodeAnalysis 5.9.0.0
```

Three things this rules out, each of which looked like the answer first:

- **Not the `.cshtml` files.** All 7 are `AdditionalDocuments` on both SDKs, with their
  `TargetPath` and `CssScope` metadata intact.
- **Not the MSBuild engine.** Registering MSBuild from `sdk/10.0.101` while the project
  still resolves the 10.0.400 SDK gives the broken result. What matters is which SDK
  supplies the generator, not which one evaluates the project.
- **Not a missing property.** No Razor opt-in flag differs between the two. The generator
  was never asked to run, because it was never loaded.

The last one is the trap. `MSBuildLocator.RegisterDefaults()` and the project's own SDK
resolution are two separate choices, and a `global.json` beside the runner moves only the
first. Only a `global.json` beside the *solution* changes the Razor DLL, and the Razor DLL
is the variable.

## What was done

**Raise the compiler vela hosts to the one the SDK's generator wants**
(`Microsoft.CodeAnalysis.*` `5.6.0` to `5.9.0-1.26379.115`, commit `6674f1e`). That exact
build is the one SDK 10.0.400 was itself assembled from. The rule is one-directional - a
newer host loading an older generator is fine - so this keeps working on 10.0.101 and
earlier, which is verified.

It is a prerelease, and off the .NET team's own feed rather than nuget.org, because
nuget.org's newest stable `Microsoft.CodeAnalysis` is `5.6.0`. That is not a workaround;
it is what the Roslyn team themselves recommend on
[#84137](https://github.com/dotnet/roslyn/issues/84137):

> Why do you need these to be on nuget.org? [...] if you look into SDK 10.0.301 for
> example, you can see it contains roslyn 5.6.0-2.26270.133 (yes, it's a pre-release
> version, but also it's just the number of the roslyn build that was shipped in that SDK
> version, so I guess you could consider it "stable") so you could just pick that.

`NuGet.config` adds that feed with `<clear />` first and package source mapping confining
it to the Roslyn packages alone, so no other dependency can be served from it. Both the
file and the `.csproj` say to take it back out when 5.9.0 reaches nuget.org.

**Make a recurrence loud** (commit `5573a4b`). The pin fixes today. It does not fix the
shape of the problem, which is that the Razor generator is not a dependency vela chooses:
it is whatever the user's SDK contains, and every feature band may raise the floor again.
vela will lose this race again.

So a project that hands the compiler `.cshtml` or `.razor` files and gets no generated
document back for any of them now records a `razor-not-generated:` note against itself.
That prefix is in `ProblemPrefixes`, so the index is marked degraded and queries against
it exit 3. The note names the project, how many views were lost, and which compiler the
generator wanted against the one vela hosts:

```
razor-not-generated: project 'App' compiles 7 Razor view(s), and none of them reached
this index. The Razor generator in '...' is built against Microsoft.CodeAnalysis 5.9.0.0
and vela hosts 5.6.0.0. Roslyn refuses to load a generator built against a newer compiler
than the host, so it loaded none. vela needs a build that hosts Microsoft.CodeAnalysis
5.9.0.0 or later, or an SDK no newer than the one vela was built for. No .cshtml or
.razor symbol in this project is searchable.
```

The version comparison is read out of the generator DLL's metadata rather than caught
from the `AnalyzerLoadFailed` event, because the reference caches its answer: the event
fires on the first load attempt and the harvest has already used it up.

Nothing is said about a project with no views, or one whose views came through, so the
note cannot decay into noise.

**Assert the floor directly.**
`RazorGeneratorTests.HostedCompiler_IsAtLeastTheOneTheSdksRazorGeneratorWasBuiltAgainst`
compares the two versions and fails with the version to raise the pin to. Next time this
happens, one test says what to do instead of four tests reporting zero.

A `global.json` pinning a working SDK was considered and rejected. It would have made CI
green while leaving every user on 10.0.400 with a silently Razor-blind index, which is
the failure, not the fix.

## What a user on 10.0.400 should expect

Razor and Blazor indexing works, from `6674f1e` onwards. Nothing to configure.

On a version of vela **before** that fix, on SDK 10.0.400 or later: the index builds, it
reports itself healthy, every query answers, and no `.cshtml` or `.razor` file is in it.
There is no error and no warning. `vela index --stats` is the only place it shows, as
`razor views : 0`. Upgrade.

From `5573a4b` onwards the same condition is reported at index time and raises the exit
code, so a future SDK cannot repeat this quietly.

If you build vela from source you need `NuGet.config`, which is in the repository root.
A build that cannot reach the `dotnet-tools` feed will fail to restore rather than
silently fall back.

## Is this an upstream bug

Two of them, and the first is already known.

**One: the packages lag the SDK.** The SDK ships a Roslyn that nuget.org does not have, so
any tool hosting Roslyn against a user's SDK is broken from the SDK's release until the
matching packages are published by hand. This has now happened at least three times -
[#77255](https://github.com/dotnet/roslyn/issues/77255) (SDK 9.0.200),
[#84137](https://github.com/dotnet/roslyn/issues/84137) and
[#84221](https://github.com/dotnet/roslyn/issues/84221) (SDK 10.0.300/301, closed by
publishing 5.6.0 manually), and now again with 5.9 and SDK 10.0.400. Roslyn's own answer
on #84137: "nuget.org gets the packages through manual effort of our infra team, whereas
the azure feed gets the packages automatically [...] it seems like a bug that it's
currently behind."

Nothing new to file. The right move is to add 5.9 to the existing thread, and the draft is
below.

**Two: the failure is silent through `MSBuildWorkspace`.** This one is not filed. `csc`
reports `WRN_AnalyzerReferencesNewerCompiler` when it declines a generator.
`MSBuildWorkspace` reports nothing: no `WorkspaceDiagnostic`, no entry in
`Workspace.WorkspaceFailed`, and `GetSourceGeneratedDocumentsAsync` simply returns a
compilation missing everything that generator would have produced. A host has to know to
subscribe to `AnalyzerFileReference.AnalyzerLoadFailed` on each reference, before anything
touches it, to find out. That is a real defect and it is the reason this took a week to
notice rather than a minute. Draft below.

Neither has been filed. The operator files them.

## Draft: comment on dotnet/roslyn#84137

> This is happening again with SDK 10.0.400.
>
> `Microsoft.CodeAnalysis.Razor.Compiler.dll` in
> `sdk/10.0.400/Sdks/Microsoft.NET.Sdk.Razor/source-generators/` references
> `Microsoft.CodeAnalysis 5.9.0.0`. nuget.org's newest stable `Microsoft.CodeAnalysis` is
> still `5.6.0`, published when #84221 was closed. So a tool that uses `MSBuildWorkspace`
> and restores from nuget.org gets `ReferencesNewerCompiler`, no Razor source generator,
> and a compilation with no `.cshtml` or `.razor` types in it - with nothing raised to say
> so.
>
> The dnceng `dotnet-tools` feed has `5.9.0-1.26379.115`, which is the build SDK 10.0.400
> ships, and pinning to it fixes it completely. That works, and thank you for pointing at
> it on this issue - but it is the third feature band in a row where the manual publish to
> nuget.org has lagged the SDK, and each time every Roslyn-hosting tool is quietly broken
> on the newest SDK in the interval. Is automatic publishing of the SDK's Roslyn build to
> nuget.org on the roadmap?

## Draft: new issue for dotnet/roslyn

> **Title:** `MSBuildWorkspace` reports nothing when a source generator is rejected as
> `ReferencesNewerCompiler`
>
> **Version used:** `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.6.0, .NET SDK 10.0.400
>
> When `AnalyzerFileReference` declines to load a generator because it was built against a
> newer compiler than the host, `csc` reports it: `WRN_AnalyzerReferencesNewerCompiler`,
> "Analyzer assembly cannot be used because it references a newer version of the compiler
> than the currently running version."
>
> Through `MSBuildWorkspace` there is no equivalent. `GetGenerators` returns an empty
> collection, `GetSourceGeneratedDocumentsAsync` returns a compilation missing everything
> the generator would have produced, and nothing is raised: no `WorkspaceDiagnostic`, no
> `Workspace.WorkspaceFailed` event, no diagnostic on the compilation.
>
> **Steps to reproduce**
>
> 1. Console app referencing `Microsoft.CodeAnalysis.CSharp.Workspaces` and
>    `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.6.0, plus `Microsoft.Build.Locator`.
> 2. `dotnet new webapp` into a solution, on SDK 10.0.400 (whose Razor generator is built
>    against `Microsoft.CodeAnalysis 5.9.0.0`).
> 3. `MSBuildLocator.RegisterDefaults()`, `MSBuildWorkspace.Create()`, subscribe to
>    `WorkspaceFailed`, `OpenSolutionAsync`, then
>    `project.GetSourceGeneratedDocumentsAsync()`.
>
> **Expected:** something, anywhere, saying a generator was rejected and which one.
>
> **Actual:** one generated document instead of eight. All seven `.cshtml` files are
> present as `AdditionalDocuments`, the Razor compiler is present in `AnalyzerReferences`,
> `WorkspaceFailed` never fires, and the compilation has no diagnostic. The only way to
> discover it is to subscribe to `AnalyzerFileReference.AnalyzerLoadFailed` on each
> reference *before* anything causes the reference to load, since it caches and the event
> fires once.
>
> This is a difficult failure to notice from the outside. A code indexer built on
> `MSBuildWorkspace` produced complete-looking output with every Razor view missing from
> it, across an SDK upgrade, with nothing in any log. Surfacing the load failure as a
> `WorkspaceDiagnostic` would make it a minute's work instead of a week's.

## Reproducing any of this

The probe is a console app referencing exactly what vela pins, pointed at a scaffolded
webapp solution. The SDK that supplies the Razor generator is the one selected by a
`global.json` **beside the solution**; a `global.json` beside the probe only moves
`MSBuildLocator`, which is not the variable and is what made this look like an MSBuild
problem for the first several hours.
