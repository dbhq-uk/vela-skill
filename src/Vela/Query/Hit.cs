namespace Vela.Query;

/// <summary>
/// One occurrence of a symbol in one file. Line and Character are stored exactly as
/// Roslyn produced them, which means both are ZERO-based. Rendering is the only place
/// that converts to the one-based numbers an editor shows.
/// </summary>
public record Hit(string RelativePath, int Line, int Character, string Symbol, bool IsDefinition);
