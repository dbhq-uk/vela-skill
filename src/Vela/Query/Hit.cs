namespace Vela.Query;

/// <summary>
/// One occurrence of a symbol in one file. Line and Character are stored exactly as
/// Roslyn produced them, which means both are ZERO-based. Rendering is the only place
/// that converts to the one-based numbers an editor shows.
///
/// IsGenerated says the path names source-generated code that was compiled but never
/// written to disk, so the reader cannot open it. It travels with the hit rather than
/// being inferred from the path, because a path is not evidence of how a document was
/// produced.
/// </summary>
public record Hit(
    string RelativePath, int Line, int Character, string Symbol, bool IsDefinition,
    bool IsGenerated = false);
