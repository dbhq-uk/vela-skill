using System.Text;
using System.Text.RegularExpressions;

namespace Vela.Config;

/// <summary>
/// Which paths are not source code, decided by gitignore's rules.
///
/// The rules are gitignore's on purpose, and the reason is that a developer already knows
/// them. Inventing a friendlier dialect would mean every pattern in a `vela.json` had to
/// be learned separately from the `.gitignore` sitting beside it, and the first surprise
/// would be silent: a file somebody believed they had re-included, quietly missing from
/// the index. So this follows git, including the one rule people trip over -
/// <b>"it is not possible to re-include a file if a parent directory of that file is
/// excluded"</b> - rather than smoothing it away.
///
/// What that buys, precisely:
/// <list type="bullet">
/// <item>A pattern with no slash matches a file NAME at any depth: <c>*.min.js</c>.</item>
/// <item>A pattern with a slash is anchored to the root: <c>src/Web/wwwroot/app/**</c>.</item>
/// <item><c>**/</c> at the start means any depth, including none.</item>
/// <item><c>*</c> and <c>?</c> never cross a <c>/</c>; <c>**</c> is the only thing that does.</item>
/// <item>A trailing <c>/</c> means a directory and everything under it.</item>
/// <item><c>!</c> takes an earlier pattern back, subject to the parent-directory rule above.</item>
/// <item>The LAST matching pattern decides.</item>
/// <item>Matching is case-sensitive, as git's is by default, and <c>\</c> in a path is
/// read as <c>/</c> so a Windows path is the same path.</item>
/// </list>
///
/// One consequence is worth stating because it is a cost rather than a rule: a pattern of
/// the form <c>**/node_modules/**</c> excludes the files inside that directory without
/// excluding the directory itself, exactly as git reads it, so the walk still has to
/// descend into it and reject each file. Writing the directory instead -
/// <c>**/node_modules/</c> - lets <see cref="ExcludesDirectory"/> prune the whole subtree
/// unread, which is why every entry in <see cref="VelaConfig.DefaultExcludes"/> that names
/// a directory is written that way. Both forms exclude the same files.
/// </summary>
public sealed class PathFilter
{
    private readonly Pattern[] _patterns;

    private PathFilter(Pattern[] patterns) => _patterns = patterns;

    /// <summary>An empty filter, which excludes nothing.</summary>
    public static PathFilter Nothing { get; } = new(Array.Empty<Pattern>());

    public static PathFilter For(IEnumerable<string> patterns)
    {
        var parsed = new List<Pattern>();

        foreach (var raw in patterns)
        {
            var pattern = Parse(raw);
            if (pattern is not null) parsed.Add(pattern.Value);
        }

        return new PathFilter(parsed.ToArray());
    }

    /// <summary>How many patterns this filter holds, which is how a caller can say the
    /// list it was given was empty rather than merely matching nothing.</summary>
    public int PatternCount => _patterns.Length;

    /// <summary>
    /// Whether a file at this path, relative to the root the patterns are anchored to, is
    /// excluded. Every ancestor directory is tested first: that is what enforces git's
    /// parent-directory rule, and it is why a negation on the file cannot save it.
    /// </summary>
    public bool Excludes(string relativePath)
    {
        var path = Normalise(relativePath);
        if (path.Length == 0) return false;

        return AnyAncestorExcluded(path) || Verdict(path, isDirectory: false);
    }

    /// <summary>
    /// Whether a directory is excluded, and so whether a walk can stop there. Asked
    /// separately from <see cref="Excludes"/> because a trailing-slash pattern matches a
    /// directory and not a file of the same name.
    /// </summary>
    public bool ExcludesDirectory(string relativeDirectory)
    {
        var path = Normalise(relativeDirectory);
        if (path.Length == 0) return false;

        return AnyAncestorExcluded(path) || Verdict(path, isDirectory: true);
    }

    private bool AnyAncestorExcluded(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            if (Verdict(path[..slash], isDirectory: true)) return true;
        }

        return false;
    }

    /// <summary>
    /// Every pattern is tried and the last one that matched decides, because that is what
    /// git does and it is the only rule under which appending to a list can take something
    /// back. Returning on the first match would make a config's own patterns unable to
    /// override a default.
    /// </summary>
    private bool Verdict(string path, bool isDirectory)
    {
        var excluded = false;

        foreach (var pattern in _patterns)
        {
            if (pattern.DirectoryOnly && !isDirectory) continue;
            if (!pattern.Regex.IsMatch(path)) continue;
            excluded = !pattern.Negated;
        }

        return excluded;
    }

    private static string Normalise(string path)
    {
        var text = path.Replace('\\', '/').Trim();
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        return text.Trim('/');
    }

    private readonly record struct Pattern(Regex Regex, bool Negated, bool DirectoryOnly);

    /// <summary>
    /// One gitignore line as a regex, or null for a line that says nothing: blank, or a
    /// comment.
    /// </summary>
    private static Pattern? Parse(string raw)
    {
        var text = (raw ?? "").Trim().Replace('\\', '/');
        if (text.Length == 0 || text[0] == '#') return null;

        var negated = text[0] == '!';
        if (negated) text = text[1..];

        var directoryOnly = text.EndsWith('/');

        // A leading slash anchors the pattern to the root and is not itself part of it. A
        // slash anywhere else also anchors, which is git's rule and the reason
        // `wwwroot/js/*.js` cannot match a `wwwroot/js` nested three directories down.
        var anchored = text.StartsWith('/') || text.TrimEnd('/').Contains('/');
        text = text.Trim('/');
        if (text.Length == 0) return null;

        var regex = new StringBuilder("^");

        // An unanchored pattern matches the file name at any depth, which is expressed by
        // giving it the `**/` that git gives it implicitly.
        if (!anchored) regex.Append("(?:[^/]+/)*");

        var components = text.Split('/');
        for (var i = 0; i < components.Length; i++)
        {
            var last = i == components.Length - 1;

            if (components[i] == "**")
            {
                // Trailing `**` is "everything inside", and it is the one place a pattern
                // crosses directory boundaries without limit. Elsewhere it is zero or more
                // whole directory names, so `a/**/z` matches `a/z` as well as `a/b/c/z`.
                regex.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }

            AppendComponent(regex, components[i]);
            if (!last) regex.Append('/');
        }

        regex.Append('$');

        return new Pattern(new Regex(regex.ToString(), RegexOptions.CultureInvariant), negated, directoryOnly);
    }

    private static void AppendComponent(StringBuilder regex, string component)
    {
        for (var i = 0; i < component.Length; i++)
        {
            var c = component[i];

            switch (c)
            {
                case '*':
                    regex.Append("[^/]*");
                    break;

                case '?':
                    regex.Append("[^/]");
                    break;

                case '[':
                    var close = component.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        // An unclosed class is a literal bracket rather than an error: the
                        // pattern still has to mean something, and refusing the whole
                        // config over one bracket would be worse than matching it.
                        regex.Append(Regex.Escape("["));
                        break;
                    }

                    var body = component[(i + 1)..close];
                    if (body.StartsWith('!')) body = '^' + body[1..];
                    regex.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                    i = close;
                    break;

                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }
}
