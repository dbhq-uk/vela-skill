using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Vela.Harvest;

/// <summary>
/// Whether the Razor source generator actually ran, and if it did not, why.
///
/// This exists because of how the Razor half of vela failed on .NET SDK 10.0.400: it
/// failed SILENTLY. Roslyn will not load a source generator that was built against a
/// newer compiler than the host, and its refusal is not an exception and not a
/// diagnostic. AnalyzerFileReference raises AnalyzerLoadFailed with
/// ReferencesNewerCompiler and hands back zero generators, so
/// GetSourceGeneratedDocumentsAsync simply returns without any Razor in it. Every
/// project compiles, every query answers, the index reports itself healthy, and no
/// .cshtml or .razor file is in it. That is precisely the shape of failure Constraint 3
/// exists to forbid.
///
/// The condition is not a one-off either, and that is why this is a permanent check
/// rather than a note in a changelog. The Razor generator is not a dependency vela
/// chooses: it is a DLL inside whichever .NET SDK evaluates the project. Every SDK
/// feature band may raise the compiler it was built against, and each time it does it
/// raises the floor on the Roslyn vela has to host. vela will lose that race again. It
/// must say so when it does.
/// </summary>
public static class RazorSourceGenerator
{
    /// <summary>
    /// The prefix that marks the note as a reason code from the repository is missing.
    /// It matches the prefix Program treats as a problem, so this note degrades the
    /// index rather than decorating it.
    /// </summary>
    public const string NotePrefix = "razor-not-generated:";

    /// <summary>
    /// Views a project hands the compiler as additional files, which is the only place
    /// they exist: a .cshtml is never a Document, so project.Documents cannot see one
    /// and neither can a count of files on disk that the compiler may not have been
    /// given.
    /// </summary>
    public static int ViewCount(Project project) =>
        project.AdditionalDocuments.Count(IsView);

    private static bool IsView(TextDocument document)
    {
        var path = document.FilePath ?? document.Name;
        return path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a generated document is one of the Razor generator's, by the hint name
    /// Roslyn gives it. The generator names every file after the view it came from, so
    /// the extension survives into the hint name - as Pages_Index_cshtml.g.cs on the
    /// compiler that shipped in SDK 10.0.101 and as Pages/Index_cshtml.g.cs on the one
    /// in 10.0.400. The separator changed and the extension did not, so the extension is
    /// what is matched.
    /// </summary>
    public static bool IsGeneratedView(string hintName) =>
        hintName.Contains("cshtml", StringComparison.OrdinalIgnoreCase)
        || hintName.Contains("razor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sentence to record against a project whose views did not reach the
    /// compilation, and null when there is nothing wrong.
    ///
    /// Nothing is said about a project that has no views, because there is nothing for
    /// the generator to have produced. Nothing is said when views did come through. The
    /// note fires only on the combination that cannot be right: the compiler was handed
    /// views and gave back no generated document for any of them.
    /// </summary>
    /// <param name="generatedViews">
    /// How many generated documents this project's harvest actually produced for views.
    /// Passed in rather than recomputed, so the number in the note is the number the
    /// index was built from and not a second opinion about it.
    /// </param>
    public static string? Diagnose(Project project, int generatedViews)
    {
        if (generatedViews > 0) return null;

        var views = ViewCount(project);
        if (views == 0) return null;

        var cause = Cause(project);
        return $"{NotePrefix} project '{project.Name}' compiles {views} Razor view(s), and none "
             + $"of them reached this index. {cause} No .cshtml or .razor symbol in this project "
             + "is searchable.";
    }

    /// <summary>
    /// Why the generator produced nothing, said as specifically as the evidence allows.
    ///
    /// The version comparison is read out of the DLL's metadata rather than caught from
    /// AnalyzerFileReference's AnalyzerLoadFailed event, because by the time anything
    /// here can ask, the harvest has already loaded the reference once and the reference
    /// caches its answer: the event fires on the first attempt and never again. The
    /// metadata is still there to read, and it is what Roslyn compared in the first
    /// place, so reading it gives the same verdict at any point in the run.
    /// </summary>
    private static string Cause(Project project)
    {
        var razor = project.AnalyzerReferences.FirstOrDefault(
            reference => IsRazorCompiler(reference.FullPath));

        if (razor?.FullPath is not string path)
        {
            return "The project has no Razor source generator among its analyzer "
                 + "references, so nothing was there to generate them.";
        }

        var host = typeof(SyntaxTree).Assembly.GetName().Version;
        var required = CompilerReferencedBy(path);

        if (required is not null && host is not null && required > host)
        {
            return $"The Razor generator in '{path}' is built against Microsoft.CodeAnalysis "
                 + $"{required} and vela hosts {host}. Roslyn refuses to load a generator built "
                 + "against a newer compiler than the host, so it loaded none. vela needs a "
                 + "build that hosts Microsoft.CodeAnalysis "
                 + $"{required} or later, or an SDK no newer than the one vela was built for.";
        }

        return $"The Razor generator at '{path}' loaded but generated nothing for them.";
    }

    private static bool IsRazorCompiler(string? path) =>
        path is not null && Path.GetFileName(path)
            .Equals("Microsoft.CodeAnalysis.Razor.Compiler.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Microsoft.CodeAnalysis version an assembly was compiled against, or null if
    /// the file cannot be read as one. Unreadable is not an error worth raising here:
    /// this runs only on a path that is already going to produce a note, and a vaguer
    /// note is better than an exception thrown out of the middle of an index.
    /// </summary>
    private static Version? CompilerReferencedBy(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();

            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.GetString(reference.Name) == "Microsoft.CodeAnalysis")
                    return reference.Version;
            }
        }
        catch (Exception e) when (e is IOException or BadImageFormatException
                                       or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
