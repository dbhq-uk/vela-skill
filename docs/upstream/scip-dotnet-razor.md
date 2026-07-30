# The Razor change we owe scip-dotnet

Sourcegraph's `scip-dotnet` cannot see Razor. vela can. This is the patch that
gives the capability back to the indexer everybody else already uses, written
against their code in their style, verified on their test suite.

**Status: a complete, working, verified patch. Not yet proposed upstream.** The
operator opens the pull request; nothing here has been pushed anywhere.

| | |
|---|---|
| Upstream | <https://github.com/sourcegraph/scip-dotnet> |
| Base commit | `4788446` (`feat: add .slnx solution format support (#112)`), tip of `main` on 2026-07-30 |
| Upstream licence | Apache 2.0 |
| Local branch | `razor-source-generated-documents`, commit `1209766`, in a throwaway clone at `/tmp/scip-dotnet` (not durable; the diff below is the durable copy) |
| Diff size | 29 files, 603 insertions, 17 deletions. 149 of those insertions are indexer and test code; the rest is a snapshot fixture and its three generated outputs |
| Existing snapshot tests | pass unchanged, byte for byte, on net8.0, net9.0 and net10.0 |
| Effect on `dotnet new webapp` | 0 `.cshtml` documents becomes 6 |
| Effect on `dotnet new blazor` | 0 `.razor` documents becomes 11 |

There is a closed issue asking for exactly this: [#61 "Support for Razer
templates"](https://github.com/sourcegraph/scip-dotnet/issues/61), opened
2024-05-13, closed as *not planned* on 2026-01-03. The maintainer's answer at the
time was "We'll be happy to review a PR adding this feature." That comment also
says "Blazor should be supported", which the measurements below show is not the
case: a scaffolded Blazor app produces zero `.razor` documents today.

## The defect

`ScipDotnet/ScipProjectIndexer.cs`, line 110:

```csharp
foreach (var document in project.Documents)
```

`Project.Documents` is the set of files the compiler reads off disk. Razor views
and Blazor components are not that. The Razor source generator turns each
`.cshtml` and `.razor` file into C# and hands it to the compilation directly, so
those documents live behind `Project.GetSourceGeneratedDocumentsAsync` and the
loop above never reaches them. Nothing downstream is at fault: the walkers, the
symbol construction and the SCIP emission all work on Razor content once it is
handed to them.

The only occurrence of the word "Razor" anywhere in the repository is the
protobuf enum constant `Razor = 62` in the generated `Scip.cs`, which the indexer
never emits.

## Why enumerating the generated documents is not, on its own, the fix

This is the part that makes the change two pieces rather than one. Add the
generated documents to that loop and you get an index that is worse than useless
in three separate ways.

**The path does not exist.** `IndexDocument` computes

```csharp
RelativePath = Path.GetRelativePath(options.WorkingDirectory.FullName, document.FilePath)
```

and a source generated document's `FilePath` is synthesised from the generator's
identity. On a scaffolded Razor Pages app it is, verbatim:

```
/tmp/razordemo/RazorDemo/obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Pages_Index_cshtml.g.cs
```

No such file is on disk. It only appears if the project sets
`EmitCompilerGeneratedFiles`, which no normal project does. Every occurrence in
the index would point at a file the user cannot open, and the SCIP specification
requires `Document.relative_path` to "point to a regular file".

**The filter throws them away again.** Line 111 gates each document on
`options.Matcher.Match(options.WorkingDirectory.FullName, document.FilePath)`.
The default include pattern is `**`, so `obj/` paths do pass today, but anybody
who has ever written `--exclude '**/obj/**'` (which is the sane thing to do)
would silently lose all Razor coverage while keeping all the other coverage. A
fix whose correctness depends on the user not excluding build output is not a
fix.

**One generated file is not one source file.** The C# the Razor generator emits
is a mixture of boilerplate that belongs to nobody and short regions that belong
to the `.cshtml`, marked with `#line` directives. Attributing the whole tree to
the view puts occurrences at line numbers that mean nothing. Worse, the
directives copied out of `_ViewImports.cshtml` appear in the generated file of
*every* view that inherits them, so a naive mapping reports the same
`@using` occurrence seven times in a seven-page app.

Roslyn resolves all of that if you ask it correctly. `SyntaxTree.GetLineMappings`
tells you which original files a generated tree speaks for, and
`Location.GetMappedLineSpan` tells you where a given occurrence really lives.
`scip-dotnet` already calls `GetMappedLineSpan` in `LocationToRange`, so the line
and column numbers were always going to be right; what was missing was the
document those numbers belong to.

## The change

Three edits, in their two indexer files plus the test harness.

**1. Enumerate the source generated documents.**
`ScipProjectIndexer.IndexSourceGeneratedDocuments` runs after the existing
`project.Documents` loop, per project. For each generated tree it asks
`OriginalFilePaths` which real files the tree carries `#line` directives for,
keeps only paths that exist on disk, and creates one `Scip.Document` per original
file. The `Matcher` is applied to the original path, not the `obj/` path, so
`--include` and `--exclude` behave the way a user expects.

**2. Attribute occurrences to the file they were written in.**
`ScipDocumentIndexer` takes an optional `originalFilePath`. When it is set,
`VisitOccurrence` records an occurrence only if `location.GetMappedLineSpan().Path`
matches, which drops the generated boilerplate and keeps the developer's code.
The ranges then land on the `.cshtml` or `.razor` because
`LocationToRange` was already mapping them.

**3. Do not report the same thing twice.** Because `_ViewImports.cshtml` and
`_Imports.razor` are folded into every view that inherits them, several generated
trees legitimately map to the same original file. `RemoveDuplicates` collapses
occurrences that are identical in symbol, roles and range, and `SymbolInformation`
entries that are identical in symbol. On a scaffolded app this is the difference
between `_ViewImports.cshtml` having one occurrence and having seven.

The refactor of `IndexDocument` into `IndexDocument` plus `WalkDocument` is
mechanical: the walking half needed to be callable against a `Scip.Document` that
already exists, so that several generated trees can contribute to one view.

## The diff

Indexer and test harness, 149 insertions:

```diff
diff --git a/ScipDotnet.Tests/SnapshotTests.cs b/ScipDotnet.Tests/SnapshotTests.cs
index fc66e41..43f8630 100644
--- a/ScipDotnet.Tests/SnapshotTests.cs
+++ b/ScipDotnet.Tests/SnapshotTests.cs
@@ -47,7 +47,7 @@ public class SnapshotTests
                 RecursivelyListFiles(outputDirectory, absoluteOutputPaths);
                 foreach (var absolutePath in absoluteOutputPaths)
                 {
-                    if (!absolutePath.EndsWith(".cs"))
+                    if (!IsSnapshotFile(absolutePath))
                     {
                         continue;
                     }
@@ -79,6 +79,13 @@ public class SnapshotTests
         }
     }
 
+    /// <summary>
+    /// Razor views (.cshtml) and Blazor components (.razor) are never compiled from disk, the
+    /// Razor source generator feeds them to the compiler, so they have their own snapshots.
+    /// </summary>
+    private static bool IsSnapshotFile(string path) =>
+        path.EndsWith(".cs") || path.EndsWith(".cshtml") || path.EndsWith(".razor");
+
     private static void RecursivelyListFiles(string path, List<string> result)
     {
         if (!Directory.Exists(path)) return;
diff --git a/ScipDotnet/ScipDocumentIndexer.cs b/ScipDotnet/ScipDocumentIndexer.cs
index 57a6e6d..e1ee02d 100644
--- a/ScipDotnet/ScipDocumentIndexer.cs
+++ b/ScipDotnet/ScipDocumentIndexer.cs
@@ -16,6 +16,7 @@ public class ScipDocumentIndexer
     private readonly Dictionary<ISymbol, ScipSymbol> _globals;
     private readonly Dictionary<ISymbol, ScipSymbol> _locals = new(SymbolEqualityComparer.Default);
     private readonly string _markdownCodeFenceLanguage;
+    private readonly string? _originalFilePath;
 
     // Custom formatting options to render symbol documentation. Feel free to tweak these parameters.
     // The options were derived by multiple rounds of experimentation with the goal of striking a
@@ -56,14 +57,22 @@ public class ScipDocumentIndexer
                               SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
     );
 
+    /// <param name="originalFilePath">
+    /// When non-null, only record the occurrences that <code>#line</code> directives attribute to
+    /// this file. Source generated documents such as the C# that the Razor generator emits for a
+    /// .cshtml file are a mixture of generated boilerplate and code the developer wrote, and only
+    /// the latter belongs in the index.
+    /// </param>
     public ScipDocumentIndexer(
         Document doc,
         IndexCommandOptions options,
-        Dictionary<ISymbol, ScipSymbol> globals)
+        Dictionary<ISymbol, ScipSymbol> globals,
+        string? originalFilePath = null)
     {
         _doc = doc;
         _options = options;
         _globals = globals;
+        _originalFilePath = originalFilePath;
         _markdownCodeFenceLanguage = _doc.Language == "C#" ? "cs" : "vb";
     }
 
@@ -220,6 +229,12 @@ public class ScipDocumentIndexer
             return;
         }
 
+        if (_originalFilePath != null &&
+            !string.Equals(location.GetMappedLineSpan().Path, _originalFilePath, StringComparison.Ordinal))
+        {
+            return;
+        }
+
         var symbolRole = 0;
         if (isDefinition)
         {
diff --git a/ScipDotnet/ScipProjectIndexer.cs b/ScipDotnet/ScipProjectIndexer.cs
index 1f1debf..fc6eff8 100644
--- a/ScipDotnet/ScipProjectIndexer.cs
+++ b/ScipDotnet/ScipProjectIndexer.cs
@@ -120,9 +120,106 @@ public class ScipProjectIndexer
                         document.FilePath);
                 }
             }
+
+            foreach (var document in await IndexSourceGeneratedDocuments(project, options, globals))
+            {
+                yield return document;
+            }
+        }
+    }
+
+    /// <summary>
+    /// Indexes the documents that the compiler synthesizes instead of reading from disk.
+    /// Razor views (.cshtml) and Blazor components (.razor) enter the compilation this way,
+    /// through the Razor source generator, so <code>project.Documents</code> never sees them.
+    ///
+    /// The generated C# lives under <code>obj/</code> and usually does not exist on disk at all,
+    /// so reporting its path would produce an index full of files nobody can open. Instead we
+    /// follow the <code>#line</code> directives that the generator emits, group the occurrences
+    /// by the original file each one came from and report that file. Occurrences that map to
+    /// generated code rather than to a file the developer wrote are dropped.
+    /// </summary>
+    private async Task<IEnumerable<Scip.Document>> IndexSourceGeneratedDocuments(
+        Project project,
+        IndexCommandOptions options,
+        Dictionary<ISymbol, ScipSymbol> globals)
+    {
+        var documentsByOriginalPath = new Dictionary<string, Scip.Document>();
+        var generatedDocuments = await project.GetSourceGeneratedDocumentsAsync();
+        options.Logger.LogDebug($"Found {generatedDocuments.Count()} source generated documents in {project.FilePath}");
+        foreach (var document in generatedDocuments)
+        {
+            var tree = await document.GetSyntaxTreeAsync();
+            if (tree == null)
+            {
+                continue;
+            }
+
+            foreach (var originalPath in OriginalFilePaths(tree))
+            {
+                if (!options.Matcher.Match(options.WorkingDirectory.FullName, originalPath).HasMatches)
+                {
+                    options.Logger.LogDebug(
+                        "Excluded file path '{FilePath}' because it did not match the provided --include and --exclude arguments",
+                        originalPath);
+                    continue;
+                }
+
+                if (!documentsByOriginalPath.TryGetValue(originalPath, out var doc))
+                {
+                    doc = new Scip.Document
+                    {
+                        Language = project.Language,
+                        RelativePath = Path.GetRelativePath(options.WorkingDirectory.FullName, originalPath)
+                    };
+                    documentsByOriginalPath.Add(originalPath, doc);
+                }
+
+                await WalkDocument(doc, document, options, globals, project.Language, originalPath);
+            }
         }
+
+        foreach (var doc in documentsByOriginalPath.Values)
+        {
+            RemoveDuplicates(doc);
+        }
+
+        return documentsByOriginalPath.Values;
+    }
+
+    /// <summary>
+    /// Removes the occurrences and symbols that we recorded more than once because several
+    /// generated files attribute the same region of the same original file to themselves.
+    /// </summary>
+    private static void RemoveDuplicates(Scip.Document doc)
+    {
+        var seenOccurrences = new HashSet<string>();
+        var occurrences = doc.Occurrences.Where(occurrence => seenOccurrences.Add(OccurrenceKey(occurrence))).ToList();
+        doc.Occurrences.Clear();
+        doc.Occurrences.AddRange(occurrences);
+
+        var seenSymbols = new HashSet<string>();
+        var symbols = doc.Symbols.Where(symbol => seenSymbols.Add(symbol.Symbol)).ToList();
+        doc.Symbols.Clear();
+        doc.Symbols.AddRange(symbols);
     }
 
+    private static string OccurrenceKey(Scip.Occurrence occurrence) =>
+        $"{occurrence.Symbol} {occurrence.SymbolRoles} {string.Join(",", occurrence.Range)}";
+
+    /// <summary>
+    /// Returns the files that a generated syntax tree attributes its contents to via
+    /// <code>#line</code> directives. A single generated Razor file can point at more than one
+    /// original file because directives from <code>_ViewImports.cshtml</code> are copied into
+    /// every view that inherits them.
+    /// </summary>
+    private static IEnumerable<string> OriginalFilePaths(SyntaxTree tree) =>
+        tree.GetLineMappings()
+            .Where(mapping => !mapping.IsHidden && mapping.MappedSpan.HasMappedPath)
+            .Select(mapping => mapping.MappedSpan.Path)
+            .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
+            .Distinct();
+
     private async Task<Scip.Document> IndexDocument(Document document,
                                                     IndexCommandOptions options,
                                                     Dictionary<ISymbol, ScipSymbol> globals,
@@ -135,29 +232,42 @@ public class ScipProjectIndexer
                 ? null
                 : Path.GetRelativePath(options.WorkingDirectory.FullName, document.FilePath)
         };
+        await WalkDocument(doc, document, options, globals, language, originalFilePath: null);
+        return doc;
+    }
+
+    /// <summary>
+    /// Walks <paramref name="document"/> and adds what it finds to <paramref name="doc"/>. When
+    /// <paramref name="originalFilePath"/> is non-null only the occurrences that <code>#line</code>
+    /// directives attribute to that file are recorded.
+    /// </summary>
+    private async Task WalkDocument(Scip.Document doc,
+                                    Document document,
+                                    IndexCommandOptions options,
+                                    Dictionary<ISymbol, ScipSymbol> globals,
+                                    string language,
+                                    string? originalFilePath)
+    {
         var semanticModel = await document.GetSemanticModelAsync();
         if (semanticModel == null)
         {
             Logger.LogWarning(
                 "Skipping document {DocumentFilePath} because document.GetSemanticModelAsync() returned null",
                 document.FilePath);
+            return;
         }
-        else
+
+        var symbolFormatter = new ScipDocumentIndexer(doc, options, globals, originalFilePath);
+        var root = await document.GetSyntaxRootAsync();
+        if (language == "C#")
         {
-            var symbolFormatter = new ScipDocumentIndexer(doc, options, globals);
-            var root = await document.GetSyntaxRootAsync();
-            if (language == "C#")
-            {
-                var walker = new ScipCSharpSyntaxWalker(symbolFormatter, semanticModel);
-                walker.Visit(root);
-            }
-            else if (language == "Visual Basic")
-            {
-                var walker = new ScipVisualBasicSyntaxWalker(symbolFormatter, semanticModel);
-                walker.Visit(root);
-            }
+            var walker = new ScipCSharpSyntaxWalker(symbolFormatter, semanticModel);
+            walker.Visit(root);
+        }
+        else if (language == "Visual Basic")
+        {
+            var walker = new ScipVisualBasicSyntaxWalker(symbolFormatter, semanticModel);
+            walker.Visit(root);
         }
-
-        return doc;
     }
 }
\ No newline at end of file
```

The snapshot fixture, a Razor Pages app with one Blazor component, added under
`snapshots/input/razor/` in the same shape as their existing `syntax` fixture:

```diff
diff --git a/snapshots/input/razor/RazorApp/Components/Counter.razor b/snapshots/input/razor/RazorApp/Components/Counter.razor
new file mode 100644
index 0000000..dc7f2c3
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Components/Counter.razor
@@ -0,0 +1,12 @@
+<p>Current count: @CurrentCount</p>
+
+<button @onclick="Increment">Click me</button>
+
+@code {
+    private int CurrentCount { get; set; }
+
+    private void Increment()
+    {
+        CurrentCount++;
+    }
+}
diff --git a/snapshots/input/razor/RazorApp/Components/_Imports.razor b/snapshots/input/razor/RazorApp/Components/_Imports.razor
new file mode 100644
index 0000000..66ebfa5
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Components/_Imports.razor
@@ -0,0 +1 @@
+@using Microsoft.AspNetCore.Components.Web
diff --git a/snapshots/input/razor/RazorApp/Pages/Index.cshtml b/snapshots/input/razor/RazorApp/Pages/Index.cshtml
new file mode 100644
index 0000000..838740b
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Pages/Index.cshtml
@@ -0,0 +1,14 @@
+@page
+@model IndexModel
+@{
+    ViewData["Title"] = Model.Greeting;
+}
+
+<h1>@Model.Greeting</h1>
+<p>@Model.Shout("world")</p>
+
+@functions {
+    private static string Loud(string text) => text.ToUpperInvariant();
+}
+
+<p>@Loud(Model.Greeting)</p>
diff --git a/snapshots/input/razor/RazorApp/Pages/Index.cshtml.cs b/snapshots/input/razor/RazorApp/Pages/Index.cshtml.cs
new file mode 100644
index 0000000..68c1d0b
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Pages/Index.cshtml.cs
@@ -0,0 +1,15 @@
+using Microsoft.AspNetCore.Mvc.RazorPages;
+
+namespace RazorApp.Pages;
+
+public class IndexModel : PageModel
+{
+    public string Greeting { get; set; } = "Hello";
+
+    public string Shout(string name) => $"{Greeting}, {name}!";
+
+    public void OnGet()
+    {
+        Greeting = "Welcome";
+    }
+}
diff --git a/snapshots/input/razor/RazorApp/Pages/_ViewImports.cshtml b/snapshots/input/razor/RazorApp/Pages/_ViewImports.cshtml
new file mode 100644
index 0000000..818ab4a
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Pages/_ViewImports.cshtml
@@ -0,0 +1,2 @@
+@using RazorApp.Pages
+@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
diff --git a/snapshots/input/razor/RazorApp/Program.cs b/snapshots/input/razor/RazorApp/Program.cs
new file mode 100644
index 0000000..a0c36ea
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/Program.cs
@@ -0,0 +1,5 @@
+var builder = WebApplication.CreateBuilder(args);
+builder.Services.AddRazorPages();
+var app = builder.Build();
+app.MapRazorPages();
+app.Run();
diff --git a/snapshots/input/razor/RazorApp/RazorApp.csproj b/snapshots/input/razor/RazorApp/RazorApp.csproj
new file mode 100644
index 0000000..834a1f1
--- /dev/null
+++ b/snapshots/input/razor/RazorApp/RazorApp.csproj
@@ -0,0 +1,9 @@
+<Project Sdk="Microsoft.NET.Sdk.Web">
+
+  <PropertyGroup>
+    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
+    <ImplicitUsings>enable</ImplicitUsings>
+    <Nullable>enable</Nullable>
+  </PropertyGroup>
+
+</Project>
diff --git a/snapshots/input/razor/razor.sln b/snapshots/input/razor/razor.sln
new file mode 100644
index 0000000..6473699
--- /dev/null
+++ b/snapshots/input/razor/razor.sln
@@ -0,0 +1,34 @@
+﻿
+Microsoft Visual Studio Solution File, Format Version 12.00
+# Visual Studio Version 17
+VisualStudioVersion = 17.0.31903.59
+MinimumVisualStudioVersion = 10.0.40219.1
+Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RazorApp", "RazorApp\RazorApp.csproj", "{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}"
+EndProject
+Global
+	GlobalSection(SolutionConfigurationPlatforms) = preSolution
+		Debug|Any CPU = Debug|Any CPU
+		Debug|x64 = Debug|x64
+		Debug|x86 = Debug|x86
+		Release|Any CPU = Release|Any CPU
+		Release|x64 = Release|x64
+		Release|x86 = Release|x86
+	EndGlobalSection
+	GlobalSection(ProjectConfigurationPlatforms) = postSolution
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|Any CPU.Build.0 = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|x64.ActiveCfg = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|x64.Build.0 = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|x86.ActiveCfg = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Debug|x86.Build.0 = Debug|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|Any CPU.ActiveCfg = Release|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|Any CPU.Build.0 = Release|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|x64.ActiveCfg = Release|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|x64.Build.0 = Release|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|x86.ActiveCfg = Release|Any CPU
+		{2E0B95A8-D543-4F39-8618-4E1BE8AE024B}.Release|x86.Build.0 = Release|Any CPU
+	EndGlobalSection
+	GlobalSection(SolutionProperties) = preSolution
+		HideSolutionNode = FALSE
+	EndGlobalSection
+EndGlobal
```

The eighteen expected-output files under `snapshots/output-net8.0/razor/`,
`snapshots/output-net9.0/razor/` and `snapshots/output-net10.0/razor/` are
generated, not hand written, and are not reproduced here. Recreate them with:

```sh
SCIP_UPDATE_SNAPSHOTS=true dotnet test -p:TargetFrameworks=net10.0
SCIP_UPDATE_SNAPSHOTS=true dotnet test -p:TargetFrameworks=net9.0
SCIP_UPDATE_SNAPSHOTS=true dotnet test -p:TargetFrameworks=net8.0
```

This is what one of them looks like, `snapshots/output-net10.0/razor/RazorApp/Pages/Index.cshtml`,
and it is the whole point of the exercise. Every annotation below is a symbol a
developer can now jump to from inside a view:

```
  @page
  @model IndexModel
//       ^^^^^^^^^^ reference scip-dotnet nuget . . Pages/IndexModel#
  @{
      ViewData["Title"] = Model.Greeting;
//    ^^^^^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#ViewData.
//                        ^^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Model.
//                              ^^^^^^^^ reference scip-dotnet nuget . . Pages/IndexModel#Greeting.
  }

  <h1>@Model.Greeting</h1>
//     ^^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Model.
//           ^^^^^^^^ reference scip-dotnet nuget . . Pages/IndexModel#Greeting.
  <p>@Model.Shout("world")</p>
//    ^^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Model.
//          ^^^^^ reference scip-dotnet nuget . . Pages/IndexModel#Shout().

  @functions {
      private static string Loud(string text) => text.ToUpperInvariant();
//                          ^^^^ definition scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Loud().
//                               documentation ```cs\nprivate static string Pages_Index.Loud(string text)\n```
//                                      ^^^^ definition scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Loud().(text)
//                                           documentation ```cs\nstring text\n```
//                                               ^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Loud().(text)
//                                                    ^^^^^^^^^^^^^^^^ reference scip-dotnet nuget System.Runtime 10.0.0.0 System/String#ToUpperInvariant().
  }

  <p>@Loud(Model.Greeting)</p>
//    ^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Loud().
//         ^^^^^ reference scip-dotnet nuget . . AspNetCoreGeneratedDocument/Pages_Index#Model.
//               ^^^^^^^^ reference scip-dotnet nuget . . Pages/IndexModel#Greeting.
```

## Measurements

All on .NET SDK 10.0.101, Linux, indexing with `--framework net10.0`. "Before" is
upstream `main` at `4788446`, "after" is the same binary with the patch applied,
run against the same scaffolded directory.

**`dotnet new webapp`, 7 `.cshtml` files on disk:**

| | Before | After |
|---|---|---|
| Documents in index | 8 | 14 |
| `.cshtml` documents | **0** | **6** |
| Occurrences | 170 | 187 |
| Occurrences in `.cshtml` | 0 | 17 |
| Document paths that do not exist on disk | 0 | 0 |
| Wall clock | 8.6s | 8.6s |

**`dotnet new blazor`, 11 `.razor` files on disk:**

| | Before | After |
|---|---|---|
| Documents in index | 6 | 17 |
| `.razor` documents | **0** | **11** |
| Occurrences | 141 | 294 |
| Occurrences in `.razor` | 0 | 153 |

**The second half of the fix, demonstrated.** Re-run the webapp index with
`--exclude '**/obj/**'`, which is what anybody indexing a real repository does:

| | After the patch, with `--exclude '**/obj/**'` |
|---|---|
| Documents in index | 10 |
| `.cshtml` documents | 6 |
| `.g.cs` documents | 0 |

The Razor coverage survives the exclusion because the pattern is matched against
`Pages/Index.cshtml`, not against the generated path under `obj/`. Enumerating
generated documents without the `#line` mapping would have produced six documents
here too, all of them dead links, and zero of them under this exclusion.

## Test evidence

Their snapshot suite, run per framework exactly as their CI does:

| Command | Result |
|---|---|
| `dotnet test -p:TargetFrameworks=net10.0` | 3 passed, 0 failed |
| `dotnet test -p:TargetFrameworks=net9.0` | 3 passed, 0 failed |
| `dotnet test -p:TargetFrameworks=net8.0` | 3 passed, 0 failed |
| `dotnet format --verify-no-changes` | clean |

Two of those three tests are the pre-existing `syntax` and `syntax-slnx`
fixtures; the third is the new `razor` one. The existing expected outputs were
regenerated under `SCIP_UPDATE_SNAPSHOTS=true` on all three frameworks and
`git status` reported no modification to any of them, which is the check that
matters: the patch changes nothing about how ordinary C# and Visual Basic are
indexed.

One change to the harness was needed. `SnapshotTests.Snapshot` only compared
expected-output files whose name ends in `.cs`, so a `.cshtml` or `.razor`
snapshot would have been written and then never asserted on. `IsSnapshotFile`
widens that to the two Razor extensions. Note in passing that `.vb` outputs are
still generated and still not compared, which is a pre-existing gap this patch
deliberately leaves alone.

Reproducing net8.0 and net9.0 locally needs those SDKs installed alongside the
.NET 10 one, because `MSBuildLocator.RegisterDefaults` fails with "No instances of
MSBuild could be detected" when the running framework is older than the only
installed SDK. That is a property of their `Program.cs` and not of this patch;
their CI installs 8.x, 9.x and 10.x for the same reason.

## How to reproduce

```sh
git clone https://github.com/sourcegraph/scip-dotnet /tmp/scip-dotnet
cd /tmp/scip-dotnet
git checkout 4788446
# save the two diff blocks above into one file and apply it
git apply razor.patch

# their tests, all three frameworks
dotnet test -p:TargetFrameworks=net10.0
dotnet test -p:TargetFrameworks=net9.0
dotnet test -p:TargetFrameworks=net8.0

# before and after on a real Razor app
mkdir /tmp/razordemo && cd /tmp/razordemo
dotnet new webapp -n RazorDemo -o RazorDemo
dotnet new sln --format sln -n RazorDemo
dotnet sln RazorDemo.sln add RazorDemo/RazorDemo.csproj
cd /tmp/scip-dotnet
dotnet run --project ScipDotnet --framework net10.0 -- index --working-directory /tmp/razordemo
```

Count the `.cshtml` documents in the resulting `/tmp/razordemo/index.scip` with
`scip print`, or with any protobuf reader over `Index.documents[].relative_path`.

## What a reviewer will ask

**Why 6 `.cshtml` documents and not 7?** The seventh,
`Pages/Shared/_ValidationScriptsPartial.cshtml`, contains no C# at all: its
generated file has no `#line` directive because there is nothing in the view to
map. There is no occurrence to attribute to it, so no document is emitted. vela
reports 7 for the same app because it emits a document per generated tree
regardless; that difference is cosmetic and this patch takes the more
conservative option, which is to emit a document only where there is something in
it.

**`@model` is not mapped on older Razor language versions.** The net8.0 snapshot
of `Index.cshtml` has no occurrence for the `IndexModel` in `@model IndexModel`,
while the net9.0 and net10.0 ones do. The Razor language version follows the
target framework and the older generator does not emit a `#line` for the model
type. This is visible in the fixture precisely because the expected outputs are
per framework, which is what that directory layout is for.

**What does it cost?** Nothing measurable on these projects: 8.6s before and
8.6s after on the Razor Pages app. `GetSourceGeneratedDocumentsAsync` forces the
generators to run, but MSBuild's design-time build has usually run them already.
On a project with no source generators the call returns an empty list and the new
code path does nothing.

**Overlapping occurrences in markup.** An expression like `@CurrentCount` in a
component produces three occurrences, because the generator maps its own
`__builder.AddContent(...)` call onto the same span of the `.razor`. That is
faithful to what the generator says. It is noisier than ideal but it is not
wrong, and filtering it would mean second-guessing the generator's own mapping.

**Visual Basic.** Untouched. The new path runs for any project language but VB
projects have no Razor, so it finds nothing.

## Licensing

`scip-dotnet` is Apache 2.0. vela is MIT. Two things follow.

Nothing was copied. The patch was written from scratch against `scip-dotnet`'s
own types, naming and control flow. What was carried over from vela is the
approach, which is a pair of Roslyn API calls that Microsoft documents:
`Project.GetSourceGeneratedDocumentsAsync` and `SyntaxTree.GetLineMappings` with
`Location.GetMappedLineSpan`. The equivalent vela code is
`src/Vela/Harvest/DocumentEnumerator.cs` and `src/Vela/Harvest/RazorMapper.cs`;
compare them with the diff above and they share no expression, no structure and
no naming. That is deliberate.

The contribution goes out under Apache 2.0. Section 5 of the Apache licence makes
any contribution submitted for inclusion in the work licensed under the same
terms, absent an explicit statement otherwise. There is no CLA bot and no
`CONTRIBUTING.md` in the repository; the only licence note is `NOTICE`, which
records that parts of the command-line and MSBuild-loading code derive from
`tcz717/LsifDotnet`, also Apache 2.0.

Nothing flows the other way. No Apache 2.0 code is coming back into vela, so
vela's MIT licence is unaffected and no NOTICE obligation attaches to it.
Contributing a capability upstream does not make vela a derivative of the thing
it was contributed to.

## Pull request description, ready to paste

---

**Title:** Index Razor views and Blazor components

Fixes #61.

### The problem

`ScipProjectIndexer` iterates `project.Documents`, which is the set of files the
compiler reads from disk. Razor views (`.cshtml`) and Blazor components
(`.razor`) never reach the compiler that way. The Razor source generator turns
them into C# and hands them straight to the compilation, so they live behind
`Project.GetSourceGeneratedDocumentsAsync` and the indexer has never seen them.

Measured on a freshly scaffolded app with `scip-dotnet` at `4788446`:

| | `dotnet new webapp` | `dotnet new blazor` |
|---|---|---|
| Razor files on disk | 7 `.cshtml` | 11 `.razor` |
| Documents in `index.scip` | 0 | 0 |

(#61 was closed with "Blazor should be supported" - the second column is the
check on that.)

### Why the obvious fix is not enough

Adding the source generated documents to the existing loop produces an index full
of paths like

```
obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Pages_Index_cshtml.g.cs
```

which do not exist on disk unless `EmitCompilerGeneratedFiles` is set, are
excluded the moment anyone passes `--exclude '**/obj/**'`, and mix generated
boilerplate in with the developer's code.

### What this does

1. `IndexSourceGeneratedDocuments` enumerates the generated documents per project
   and, for each one, uses `SyntaxTree.GetLineMappings` to find which real files
   its `#line` directives point at. One SCIP `Document` is created per original
   file, and `--include` / `--exclude` are matched against that path.
2. `ScipDocumentIndexer` takes an optional `originalFilePath` and records only
   the occurrences whose `GetMappedLineSpan().Path` matches it, so generated
   boilerplate is dropped and the developer's code keeps the line and column
   numbers it has in the `.cshtml`. `LocationToRange` already called
   `GetMappedLineSpan`, so the ranges were always correct once the document was.
3. `RemoveDuplicates` collapses identical occurrences, which is needed because
   `_ViewImports.cshtml` and `_Imports.razor` are folded into the generated file
   of every view that inherits them.

Result on the same two apps: 6 `.cshtml` documents with 17 occurrences, and 11
`.razor` documents with 153 occurrences. (The seventh `.cshtml`,
`_ValidationScriptsPartial.cshtml`, contains no C#, so there is nothing to index
in it.) No measurable change in indexing time.

### Tests

A new snapshot fixture, `snapshots/input/razor`, covering a Razor Page with
`@model`, `@functions` and inline expressions, a `_ViewImports.cshtml`, a Blazor
component with `@code`, and an `_Imports.razor`, with expected output for
net8.0, net9.0 and net10.0.

`SnapshotTests` previously only compared expected-output files ending in `.cs`,
so Razor snapshots would have been written and never asserted on; `IsSnapshotFile`
widens that to `.cshtml` and `.razor`.

All existing snapshots are byte-identical after the change on all three
frameworks, and `dotnet format --verify-no-changes` is clean.

---

## What is not here

- Nothing has been pushed and no pull request has been opened.
- The patch has only been exercised on Linux. The upstream CI matrix also covers
  Windows and macOS, where path comparison in `VisitOccurrence` uses
  `StringComparison.Ordinal` against a path Roslyn produced from a `#line`
  directive Roslyn also produced. Both sides come from the same string, so they
  should match, but that is reasoning rather than a measurement.
- No Razor Class Library, no MVC `Views/` layout and no `.vbproj` with Razor
  (which cannot exist) were tested. The Razor Pages and Blazor layouts were.
