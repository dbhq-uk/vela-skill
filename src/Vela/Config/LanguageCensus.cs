using System.IO.Enumeration;
using Vela.Indexing;

namespace Vela.Config;

public sealed record LanguageCount(string Language, int Files);

/// <summary>
/// What languages a repository is actually written in, once the vendored code and the build
/// output are taken out.
/// </summary>
/// <param name="PrunedDirectories">
/// How many directories the exclude list kept the walk out of altogether. It is reported
/// because it is the difference between a walk that costs nothing and one that reads 5,695
/// vendored Python files, and because a suspiciously large number is worth somebody looking
/// at their exclude list.
/// </param>
/// <param name="ExcludedFiles">
/// How many files were reached and then rejected by a pattern. A pattern of the form
/// <c>dir/**</c> excludes files without excluding the directory, so these are the ones the
/// walk had to open the directory to reject.
/// </param>
public sealed record Census(
    IReadOnlyList<LanguageCount> Languages,
    int PrunedDirectories,
    int ExcludedFiles)
{
    public int Count(string language) =>
        Languages.FirstOrDefault(l => l.Language == language)?.Files ?? 0;

    /// <summary>
    /// The languages in the repository that no job claims, most numerous first.
    ///
    /// This is the census's whole reason to exist. It is not a fault - vela cannot index SQL
    /// and never claimed to - but it is the one honest answer to "what is not in this
    /// index", and a reader who can see "python 80" rather than "python 5,775" can tell at a
    /// glance whether the excludes are doing their job.
    /// </summary>
    public IReadOnlyList<LanguageCount> NotCoveredBy(IReadOnlyList<IndexJob> jobs)
    {
        var covered = new HashSet<string>(jobs.Select(j => j.Language), StringComparer.Ordinal);

        // One compilation, two languages: a vela job naming either covers both, because
        // that is what the index will actually hold.
        if (covered.Contains("csharp") || covered.Contains("razor"))
        {
            covered.Add("csharp");
            covered.Add("razor");
        }

        return Languages.Where(l => !covered.Contains(l.Language)).ToList();
    }
}

/// <summary>
/// Counts source files by language under a root, obeying an exclude list.
///
/// Language comes from the file extension and from nothing else. Constraint 1 forbids
/// sampling a file's contents to guess what it is: a census that read bytes would give a
/// different answer on a different day, and an index whose contents depend on a heuristic is
/// not one anybody can reason about. An extension vela has no language for is not counted
/// at all rather than counted as something.
///
/// The extension table is <see cref="Vela.Indexing.SourceFile"/>, which the importer already
/// uses to name a document's language, so a repository cannot be told it holds a language
/// the importer would then call something else.
/// </summary>
public static class LanguageCensus
{
    public static Census Take(string root, PathFilter filter)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var pruned = 0;
        var excluded = 0;

        var full = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(full);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<Entry> entries;
            try
            {
                entries = EnumerateEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A directory that cannot be listed holds nothing this can count. It is not
                // reported here because the census is informational and never degrades an
                // index: the freshness walk in Staleness is where an unreadable directory
                // means something, and it does report it.
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(full, entry.Path).Replace('\\', '/');

                if (entry.IsDirectory)
                {
                    if (filter.ExcludesDirectory(relative)) { pruned++; continue; }
                    pending.Push(entry.Path);
                    continue;
                }

                if (filter.Excludes(relative)) { excluded++; continue; }

                var language = SourceFile.LanguageOf(entry.Name);
                if (language == "unknown") continue;

                counts[language] = counts.GetValueOrDefault(language) + 1;
            }
        }

        // Most numerous first, ties broken on the name ordinally, so the same tree renders
        // the same sentence on every run and every machine (Constraint 1).
        var languages = counts
            .Select(pair => new LanguageCount(pair.Key, pair.Value))
            .OrderByDescending(l => l.Files)
            .ThenBy(l => l.Language, StringComparer.Ordinal)
            .ToList();

        return new Census(languages, pruned, excluded);
    }

    private readonly record struct Entry(string Path, bool IsDirectory, string Name);

    /// <summary>
    /// One directory's entries, each read once, as <see cref="Staleness"/> does it and for
    /// the same reason: <see cref="FileSystemEntry.IsDirectory"/> comes from the listing the
    /// operating system already returned rather than from a second stat per file.
    /// AttributesToSkip is zero so a dot-prefixed directory is walked, because the exclude
    /// list is the only filter here and a silent second one is what Constraint 3 forbids.
    /// </summary>
    private static IEnumerable<Entry> EnumerateEntries(string directory) =>
        new FileSystemEnumerable<Entry>(
            directory,
            (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.IsDirectory, e.FileName.ToString()),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            });
}
