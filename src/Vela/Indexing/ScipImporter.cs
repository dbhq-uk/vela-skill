using System.Globalization;
using System.Text;
using Google.Protobuf;
using Microsoft.Data.Sqlite;

namespace Vela.Indexing;

/// <summary>
/// What one import actually did, so the caller can say it out loud.
///
/// Every number here except <see cref="Documents"/> and <see cref="Occurrences"/> counts
/// something the reader would otherwise never learn: Constraint 3 is about an incomplete
/// index looking like a complete one, and an import has four separate ways to be less
/// than the file it read.
/// </summary>
/// <param name="Problems">
/// The ones that mean code is missing from the index: a document whose file lies outside
/// the tree vela is indexing, and a document whose path is already taken. Both degrade
/// the index and both name the path, because a count with nothing to check it against is
/// the thing external_document was added to fix.
/// </param>
/// <param name="UnnamedOccurrences">
/// Occurrences the source index gave no symbol at all. They are stored, so the totals
/// agree with the file that was read, and they carry no name, because there is none.
/// </param>
/// <param name="UnconvertedDocuments">
/// Documents whose offsets are counted in something other than UTF-16 code units and
/// whose text could not be found, so the numbers were stored as they arrived. Exact on
/// any line of pure ASCII and wrong on any line that is not.
/// </param>
/// <param name="UnspecifiedEncodingDocuments">
/// Documents that declared no position encoding at all, so what unit their character
/// offsets are counted in is not stated anywhere in the file.
///
/// scip.proto, of UnspecifiedPositionEncoding = 0: "Default value. This value should not
/// be used by new SCIP indexers so that a consumer can process the SCIP index without
/// ambiguity." Real indexers use it anyway - scip-typescript 0.4.0 leaves the field unset
/// on every document it writes, and four such documents are live in the index vela was
/// developed against. <b>vela reads them as UTF-16 code units</b>, because that is what
/// every other row in the database already means and because the unit is chosen by the
/// indexer's implementation language, which the file does not state either. That
/// assumption is right for an indexer implemented in JVM/.NET or JavaScript/TypeScript
/// and silently wrong for one implemented in Go, Rust, C++ or Python that omits the
/// field. So it is counted and said out loud rather than assumed away: an ambiguous index
/// has to look ambiguous.
/// </param>
/// <param name="UnparsedMonikers">
/// Monikers that do not fit the grammar in scip.proto, so no dotted display name could
/// be derived and the moniker itself stands in as the name.
/// </param>
public sealed record ImportReport(
    string Tool,
    int Documents,
    int Occurrences,
    int UnnamedOccurrences,
    int UnconvertedDocuments,
    int UnspecifiedEncodingDocuments,
    int UnparsedMonikers,
    IReadOnlyList<string> Problems)
{
    public bool Degraded => Problems.Count > 0;
}

/// <summary>
/// Reads a .scip index produced by any language's indexer into the database vela
/// queries, so refs, def, outline and impact answer across languages.
///
/// The parsing is nearly free: a .scip file is a serialised <c>Scip.Index</c>, the schema
/// is vendored at src/Vela/Scip/scip.proto and Google.Protobuf is already referenced.
/// Everything below is the four things that are not free.
///
/// <b>1. Positions are counted in three different units.</b> scip.proto tells an indexer
/// to choose by implementation language: "For an indexer implemented in JVM/.NET language
/// or JavaScript/TypeScript, use UTF16CodeUnitOffsetFromLineStart. For an indexer
/// implemented in Python, use UTF32CodeUnitOffsetFromLineStart. For an indexer
/// implemented in Go, Rust or C++, use UTF8ByteOffsetFromLineStart." A merged index
/// legitimately holds all three, and vela's occurrence table has one meaning for one
/// column. <b>vela's canonical encoding is UTF-16 code units</b>, because that is what
/// Roslyn produces, what the emitter already declares on every document it writes, and
/// what every row in every index vela has ever built already means. So a UTF-8 or UTF-32
/// document is converted on the way in, and what it declared is kept in
/// document.position_encoding so the conversion is auditable.
///
/// <b>2. Local symbols are document-scoped.</b> scip.proto: local symbols "MUST only be
/// used for entities which are local to a Document, and cannot be accessed from outside
/// the Document". The ids are counters, so `local 1` from a Python file and `local 1`
/// from a TypeScript file are unrelated - four documents of a real scip-typescript index
/// over ScentVerdict.Mobile each carry a `local 1`. vela keys occurrences by display name
/// globally, so an imported local's display name is namespaced by its document.
///
/// <b>3. Project roots differ.</b> Every relative_path is relative to that index's own
/// Metadata.project_root, and vela's paths are relative to <see cref="ProjectRoot"/>. The
/// two are rarely the same directory: scip-typescript run over a mobile app inside a
/// repository roots itself at the app. Paths are rebased, and one that cannot be is
/// recorded rather than dropped. An index that declares no project_root at all, or a
/// relative one, is refused outright: see <see cref="Writer.Begin"/>.
///
/// <b>4. A moniker is not a name.</b> See <see cref="MonikerName"/>, which is the heart
/// of this.
/// </summary>
public static class ScipImporter
{
    /// <summary>
    /// Imports a .scip file, reading it one document at a time.
    ///
    /// scip.proto: "An Index message payload can have a large memory footprint and it's
    /// therefore recommended to emit and consume an Index payload one field value at a
    /// time. To permit streaming consumption of an Index payload, the `metadata` field
    /// must appear at the start of the stream and must only appear once in the stream."
    /// So this walks the wire format itself rather than calling Index.Parser.ParseFrom,
    /// which would hold every document of a 60MB index in memory at once. One Document
    /// is materialised at a time and released once its rows are written.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is not a readable SCIP index, its metadata does not come first, or its
    /// project_root is not an absolute path. The whole import runs in one transaction, so
    /// nothing at all is written: a .scip that cannot be parsed, or cannot be placed,
    /// leaves the index exactly as it was rather than half-imported.
    /// </exception>
    public static ImportReport ImportFile(SqliteConnection db, string scipPath, string projectRoot)
    {
        using var stream = File.OpenRead(scipPath);
        return ImportStream(db, stream, projectRoot);
    }

    /// <summary>Imports an index already in memory. The streaming path is the one a
    /// file goes through; this is for an index that was built rather than read.</summary>
    public static ImportReport Import(SqliteConnection db, Scip.Index index, string projectRoot)
    {
        using var writer = new Writer(db, projectRoot);
        writer.Begin(index.Metadata);
        foreach (var document in index.Documents) writer.Add(document);
        return writer.Commit();
    }

    private static ImportReport ImportStream(SqliteConnection db, Stream stream, string projectRoot)
    {
        using var writer = new Writer(db, projectRoot);

        try
        {
            var input = new CodedInputStream(stream);
            var started = false;

            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case MetadataField:
                    {
                        var metadata = new Scip.Metadata();
                        input.ReadMessage(metadata);
                        writer.Begin(metadata);
                        started = true;
                        break;
                    }

                    case DocumentsField:
                    {
                        // The spec guarantees metadata arrives first, and without it
                        // there is no project_root to rebase against, so a stream that
                        // breaks the guarantee is rejected rather than half-read.
                        if (!started)
                        {
                            throw new InvalidDataException(
                                "This .scip index has a document before its metadata, so there is no "
                                + "project root to resolve its paths against. Nothing was imported.");
                        }

                        var document = new Scip.Document();
                        input.ReadMessage(document);
                        writer.Add(document);
                        break;
                    }

                    // external_symbols, and any field a later version of the schema
                    // adds. Skipped by wire type, which is what makes this forwards
                    // compatible rather than brittle.
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            if (!started)
            {
                throw new InvalidDataException(
                    "This .scip index carries no metadata, so it cannot be read. Nothing was imported.");
            }
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "This file is not a readable SCIP index: " + ex.Message
                + ". Nothing was imported, so the index is exactly as it was.", ex);
        }

        return writer.Commit();
    }

    private const int MetadataField = 1;
    private const int DocumentsField = 2;

    /// <summary>
    /// One import in progress: the transaction, the prepared statements, and the running
    /// tallies. Disposed without <see cref="Commit"/> it rolls back, which is what makes
    /// a failure part way through leave nothing behind.
    /// </summary>
    private sealed class Writer : IDisposable
    {
        private readonly SqliteConnection db;
        private readonly string projectRoot;
        private readonly SqliteTransaction tx;
        private readonly SqliteCommand insertDoc;
        private readonly SqliteCommand insertOcc;
        private readonly SqliteCommand insertFts;
        private readonly HashSet<string> seenSymbols = new(StringComparer.Ordinal);
        private readonly HashSet<string> takenPaths = new(StringComparer.Ordinal);
        private readonly List<string> problems = new();

        private string foreignRoot = "";
        private string tool = "";
        private int documents;
        private int occurrences;
        private int unnamed;
        private int unconverted;
        private int unspecifiedEncoding;
        private int unparsed;
        private bool committed;

        public Writer(SqliteConnection db, string projectRoot)
        {
            this.db = db;
            this.projectRoot = Path.GetFullPath(projectRoot);
            tx = db.BeginTransaction();

            insertDoc = db.CreateCommand();
            insertDoc.Transaction = tx;
            insertDoc.CommandText =
                "INSERT INTO document(relative_path, language, generated, position_encoding) "
                + "VALUES ($p, $l, $g, $e) RETURNING id";
            insertDoc.Parameters.Add("$p", SqliteType.Text);
            insertDoc.Parameters.Add("$l", SqliteType.Text);
            insertDoc.Parameters.Add("$g", SqliteType.Integer);
            insertDoc.Parameters.Add("$e", SqliteType.Integer);

            insertOcc = db.CreateCommand();
            insertOcc.Transaction = tx;
            insertOcc.CommandText = """
                INSERT INTO occurrence(document_id, symbol, scip_symbol, is_definition, start_line, start_char, enc_end_line, enc_end_char)
                VALUES ($d, $s, $scip, $def, $sl, $sc, $el, $ec)
                """;
            foreach (var name in new[] { "$d", "$def", "$sl", "$sc", "$el", "$ec" })
                insertOcc.Parameters.Add(name, SqliteType.Integer);
            insertOcc.Parameters.Add("$s", SqliteType.Text);
            insertOcc.Parameters.Add("$scip", SqliteType.Text);

            insertFts = db.CreateCommand();
            insertFts.Transaction = tx;
            insertFts.CommandText = "INSERT INTO symbol_fts(symbol) VALUES ($s)";
            insertFts.Parameters.Add("$s", SqliteType.Text);

            // Every path already in the database, so a collision with the C# index this
            // is being added beside is caught here rather than as a raw SqliteException
            // from the relative_path UNIQUE constraint half way through.
            using var existing = db.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText = "SELECT relative_path FROM document";
            using var reader = existing.ExecuteReader();
            while (reader.Read()) takenPaths.Add(reader.GetString(0));
        }

        /// <summary>
        /// Reads the metadata, and refuses an index whose project_root is not an
        /// absolute path.
        ///
        /// scip.proto: project_root is a "URI-encoded absolute path to the root
        /// directory of this index. All documents in this index must appear in a
        /// subdirectory of this root directory." It is NOT marked required - unlike
        /// Document.relative_path, which the same file marks "(Required)" - so an index
        /// can really arrive with it empty or relative, and one that does cannot be
        /// placed. <see cref="Rebase"/> used to do Path.GetFullPath(Path.Combine(root,
        /// relativePath)), which for an empty or relative root resolves against the
        /// PROCESS WORKING DIRECTORY: the same .scip file imported from two different
        /// directories produced different paths, or a clean import from one and a
        /// degraded one from the other. vela is deterministic only (Constraint 1), so
        /// this is a refusal by name rather than a guess. There is nothing to guess
        /// from: the paths in the file are relative to a directory the file does not
        /// name.
        /// </summary>
        /// <exception cref="InvalidDataException">project_root is empty, relative, or
        /// not a file URI naming an absolute path on this machine.</exception>
        public void Begin(Scip.Metadata? metadata)
        {
            var declared = metadata?.ProjectRoot ?? "";
            foreignRoot = RootOf(declared);

            if (!Path.IsPathRooted(foreignRoot))
            {
                var what = declared.Length == 0
                    ? "declares no project_root"
                    : $"declares project_root '{declared}', which is not an absolute path on this machine";

                throw new InvalidDataException(
                    $"This .scip index {what}. Every relative_path in it is relative to that directory, "
                    + "so without it vela cannot say where any of this index's documents are. scip.proto "
                    + "asks for a \"URI-encoded absolute path to the root directory of this index\"; "
                    + "resolving a relative one against the current directory instead would make the same "
                    + "file import differently depending on where vela was run from. Nothing was "
                    + "imported. Re-run the indexer, which normally writes an absolute file: URI here.");
            }

            tool = metadata?.ToolInfo?.Name ?? "";
        }

        public void Add(Scip.Document document)
        {
            var path = Rebase(document.RelativePath);
            if (path is null)
            {
                // Constraint 3: not dropped in silence. The file is outside the tree
                // this index covers, so it cannot have a relative_path at all -
                // scip.proto forbids '..' in one - and its code is genuinely absent.
                problems.Add(OutsideProjectRootPrefix
                             + Path.Combine(foreignRoot, document.RelativePath).Replace('\\', '/'));
                return;
            }

            if (!takenPaths.Add(path))
            {
                problems.Add(DuplicateDocumentPrefix + path);
                return;
            }

            var lines = LinesOf(document, path);
            if (NeedsConversion(document.PositionEncoding) && lines is null) unconverted++;

            // Not a conversion failure and not a gap: an ambiguity. Nothing in the file
            // says what unit these offsets are counted in, they are read as UTF-16
            // because that is what every other row in this database means, and the count
            // is reported so that reading is a stated assumption rather than a silent one.
            if (document.PositionEncoding == Scip.PositionEncoding.UnspecifiedPositionEncoding)
                unspecifiedEncoding++;

            insertDoc.Parameters["$p"].Value = path;
            insertDoc.Parameters["$l"].Value = LanguageOf(document, path);
            insertDoc.Parameters["$g"].Value = IsGenerated(document) ? 1 : 0;
            insertDoc.Parameters["$e"].Value = (int)document.PositionEncoding;
            var documentId = Convert.ToInt64(insertDoc.ExecuteScalar());
            documents++;

            // Only a local needs it, and building it costs a pass over the document's
            // symbols, so it is built once and only when there is a local to name.
            Dictionary<string, Scip.SymbolInformation>? described = null;

            foreach (var occurrence in document.Occurrences)
            {
                var moniker = occurrence.Symbol;

                string display;
                if (moniker.Length == 0)
                {
                    // The contract for an occurrence the index gave no symbol: stored,
                    // so the totals agree with the file that was read, and unnamed,
                    // because there is no name. No query that matches on a name can
                    // ever return it, and the count is reported.
                    display = "";
                    unnamed++;
                }
                else if (moniker.StartsWith(LocalPrefix, StringComparison.Ordinal))
                {
                    described ??= document.Symbols
                        .GroupBy(s => s.Symbol, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                    display = LocalName(path, moniker, described);
                }
                else
                {
                    display = MonikerName.For(moniker);
                    if (ReferenceEquals(display, moniker) || display == moniker) unparsed++;
                }

                var range = RangeOf(occurrence);
                var enclosing = EnclosingEndOf(occurrence);

                insertOcc.Parameters["$d"].Value = documentId;
                insertOcc.Parameters["$s"].Value = display;
                insertOcc.Parameters["$scip"].Value = moniker;
                insertOcc.Parameters["$def"].Value =
                    (occurrence.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0 ? 1 : 0;
                insertOcc.Parameters["$sl"].Value = range.Line;
                insertOcc.Parameters["$sc"].Value =
                    Normalise(lines, document.PositionEncoding, range.Line, range.Character);
                insertOcc.Parameters["$el"].Value = enclosing is null ? DBNull.Value : enclosing.Value.Line;
                insertOcc.Parameters["$ec"].Value = enclosing is null
                    ? DBNull.Value
                    : Normalise(lines, document.PositionEncoding, enclosing.Value.Line, enclosing.Value.Character);
                insertOcc.ExecuteNonQuery();
                occurrences++;

                // An empty name in the full-text index answers `find` with a blank line,
                // which is worse than answering nothing.
                if (display.Length > 0 && seenSymbols.Add(display))
                {
                    insertFts.Parameters["$s"].Value = display;
                    insertFts.ExecuteNonQuery();
                }
            }
        }

        public ImportReport Commit()
        {
            tx.Commit();
            committed = true;
            return new ImportReport(
                tool, documents, occurrences, unnamed, unconverted, unspecifiedEncoding, unparsed, problems);
        }

        public void Dispose()
        {
            if (!committed) tx.Rollback();
            insertDoc.Dispose();
            insertOcc.Dispose();
            insertFts.Dispose();
            tx.Dispose();
        }

        /// <summary>
        /// The path a foreign document takes in this index, or null when its file lies
        /// outside the tree vela is indexing.
        /// </summary>
        private string? Rebase(string relativePath)
        {
            var absolute = Path.GetFullPath(Path.Combine(foreignRoot, relativePath));
            var rebased = Path.GetRelativePath(projectRoot, absolute);

            // On Windows a different volume yields the original absolute path back.
            if (Path.IsPathRooted(rebased)) return null;

            rebased = rebased.Replace('\\', '/');
            return rebased == ".." || rebased.StartsWith("../", StringComparison.Ordinal) ? null : rebased;
        }

        /// <summary>
        /// The document's text, split into lines, or null when there is none to be had.
        /// Only ever asked for when the offsets actually need converting.
        ///
        /// scip.proto, of Document.text: "Indexers are not expected to include the text
        /// by default. It's preferable that clients read the text contents from the file
        /// system by resolving the absolute path from joining `Index.metadata.project_root`
        /// and `Document.relative_path`." So both, in that order.
        /// </summary>
        private string[]? LinesOf(Scip.Document document, string rebasedPath)
        {
            if (!NeedsConversion(document.PositionEncoding)) return null;
            if (document.Text.Length > 0) return SplitLines(document.Text);

            var onDisk = Path.Combine(projectRoot, rebasedPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                return File.Exists(onDisk) ? SplitLines(File.ReadAllText(onDisk)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private int Normalise(string[]? lines, Scip.PositionEncoding encoding, int line, int character)
        {
            if (!NeedsConversion(encoding) || lines is null) return character;
            return line >= 0 && line < lines.Length ? ToUtf16(lines[line], character, encoding) : character;
        }
    }

    private const string LocalPrefix = "local ";

    /// <summary>A document outside the root vela is indexing. Its code is absent, so
    /// this degrades the index, and it uses the same prefix the emitter uses for the
    /// same fact so both reach the health record through one list.</summary>
    private const string OutsideProjectRootPrefix = "outside-project-root: ";

    /// <summary>A path the index already holds. relative_path is UNIQUE, so one of the
    /// two documents cannot be stored, and which one is arbitrary - hence a report
    /// rather than a silent overwrite.</summary>
    private const string DuplicateDocumentPrefix = "duplicate-document: ";

    /// <summary>
    /// The display name of a local: the document it lives in, then what it is called.
    ///
    /// The document is what makes two documents' `local 1` two symbols, and it stays in
    /// the name whatever else is known about the local. An indexer that fills in
    /// display_name and leaves enclosing_symbol empty would otherwise store a bare
    /// `count`, which is one name for every count in the repository - hazard 2 again,
    /// from the other end. It costs nothing to keep: vela matches a trailing run of
    /// dotted segments, so `refs count` reaches this row no matter how long the prefix in
    /// front of it is.
    ///
    /// Where the index describes the local, its name is built the way vela names a C#
    /// local: the enclosing symbol, then the short name. scip.proto gives
    /// SymbolInformation.enclosing_symbol for exactly this - "in the situation that you
    /// wish to include a local symbol in the hierarchy, then you can set the value of
    /// this field to reference the 'parent' or 'owner' of this local symbol" - and vela's
    /// own emitter fills it in. scip-typescript 0.4.0 fills in neither it nor
    /// display_name on any of the 769 locals it emitted for ScentVerdict.Mobile, so the
    /// fallback is the id: `local 1` with the space taken out, which is the only
    /// character of a local moniker that is not an identifier character. That is the
    /// whole of the fix here. The name used to be the path, a '#' and the moniker -
    /// src/ScentVerdict.Mobile/src/composables/useApi.ts#local 2 - and a '#' is not a
    /// dot, so no pattern anyone would type ever selected it: `local 2` failed on the
    /// '#' before it, and it looked nothing like the rest of the index besides.
    /// </summary>
    private static string LocalName(
        string documentPath, string moniker, Dictionary<string, Scip.SymbolInformation> described)
    {
        var document = DottedPath(documentPath);

        if (!described.TryGetValue(moniker, out var information) || information.DisplayName.Length == 0)
            return document + ".local" + moniker[LocalPrefix.Length..];

        // A moniker that does not parse stands in for itself, spaces and all, and this
        // is a name rather than a moniker, so it is the display name alone in that case.
        var enclosingName = MonikerName.For(information.EnclosingSymbol);
        var enclosing = enclosingName == information.EnclosingSymbol ? "" : enclosingName + ".";

        return document + "." + enclosing + MonikerName.OneSegment(information.DisplayName);
    }

    /// <summary>
    /// A document's path as dotted segments, one segment per path component.
    ///
    /// A path is not a dotted name and turning it into one is not a matter of replacing
    /// the slashes: src/ScentVerdict.Mobile/src/composables/useApi.ts done that way ends
    /// in a segment called `ts`, which then answers `refs ts` for every local in every
    /// TypeScript file, and carries a segment called `Mobile` that answers for every
    /// local in the app. So one component is one segment, by the same two rules a
    /// descriptor gets: the file extension comes off the last component, and any dot
    /// still left in any component becomes an underscore.
    /// </summary>
    private static string DottedPath(string path)
    {
        var parts = path.Split('/');
        parts[^1] = SourceFile.WithoutExtension(parts[^1]);

        for (var i = 0; i < parts.Length; i++) parts[i] = MonikerName.OneSegment(parts[i]);

        return string.Join('.', parts);
    }

    /// <summary>
    /// The language the index declared, or the one the extension implies when it
    /// declared none. scip-typescript 0.4.0 leaves Document.language empty on every
    /// document, and an index where nothing has a language cannot be told apart from one
    /// where the harvest collapsed.
    /// </summary>
    private static string LanguageOf(Scip.Document document, string path) =>
        document.Language.Length > 0 ? document.Language : SourceFile.LanguageOf(path);

    /// <summary>
    /// Where an occurrence starts.
    ///
    /// scip.proto: "New producers SHOULD set `typed_range` and SHOULD NOT set the
    /// deprecated `range` field", and "When both encodings are present on the same
    /// Occurrence, `typed_range` takes precedence over `range`". Reading only the
    /// deprecated field would put every occurrence of a modern index at line 0,
    /// character 0, which looks like a successful import and is uniformly wrong.
    /// </summary>
    private static (int Line, int Character) RangeOf(Scip.Occurrence occurrence) =>
        occurrence.TypedRangeCase switch
        {
            Scip.Occurrence.TypedRangeOneofCase.SingleLineRange =>
                (occurrence.SingleLineRange.Line, occurrence.SingleLineRange.StartCharacter),
            Scip.Occurrence.TypedRangeOneofCase.MultiLineRange =>
                (occurrence.MultiLineRange.StartLine, occurrence.MultiLineRange.StartCharacter),
            _ => (occurrence.Range.Count > 0 ? occurrence.Range[0] : 0,
                  occurrence.Range.Count > 1 ? occurrence.Range[1] : 0)
        };

    /// <summary>
    /// Where the enclosing range ends, which is the only part of it vela stores, or null
    /// when the occurrence carries none. A three-element deprecated range is
    /// [startLine, startCharacter, endCharacter] and ends on its own start line.
    /// </summary>
    private static (int Line, int Character)? EnclosingEndOf(Scip.Occurrence occurrence)
    {
        switch (occurrence.TypedEnclosingRangeCase)
        {
            case Scip.Occurrence.TypedEnclosingRangeOneofCase.SingleLineEnclosingRange:
                var single = occurrence.SingleLineEnclosingRange;
                return (single.Line, single.EndCharacter);

            case Scip.Occurrence.TypedEnclosingRangeOneofCase.MultiLineEnclosingRange:
                var multi = occurrence.MultiLineEnclosingRange;
                return (multi.EndLine, multi.EndCharacter);
        }

        var range = occurrence.EnclosingRange;
        return range.Count switch
        {
            >= 4 => (range[2], range[3]),
            3 => (range[0], range[2]),
            _ => null
        };
    }

    /// <summary>
    /// Whether this document is one refs and impact must suppress: compiled but not on
    /// disk, so its paths cannot be opened.
    ///
    /// scip.proto has the fact as a per-occurrence role - "Generated: Is the symbol in
    /// generated code?" - and vela has it as a per-document column, so the two are joined
    /// here the same way the emitter joins them: a document is generated when it holds
    /// occurrences and every one of them is. One occurrence that is not is one position a
    /// reader can open, and an openable document must never be suppressed.
    ///
    /// A document with no occurrences at all is not generated. An empty Razor view is
    /// exactly that, and it must stay listed rather than disappear from a default answer.
    /// </summary>
    private static bool IsGenerated(Scip.Document document) =>
        document.Occurrences.Count > 0
        && document.Occurrences.All(o => (o.SymbolRoles & (int)Scip.SymbolRole.Generated) != 0);

    private static bool NeedsConversion(Scip.PositionEncoding encoding) =>
        encoding is Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart
                 or Scip.PositionEncoding.Utf32CodeUnitOffsetFromLineStart;

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// One line's offset, converted from the encoding its indexer counted in to UTF-16
    /// code units.
    ///
    /// The worked example is scip.proto's own, for the string "🚀 Woo": the offset of
    /// 'W' is 5 in UTF-8 bytes, 3 in UTF-16 code units and 2 in UTF-32 code points. All
    /// three name one character, and after this all three are 3.
    ///
    /// An offset landing inside a character rounds forward to the next character
    /// boundary, and one running past the end of the line keeps whatever it had past the
    /// end, so a range that overruns is shortened rather than moved somewhere else.
    /// </summary>
    private static int ToUtf16(string line, int offset, Scip.PositionEncoding encoding)
    {
        if (offset <= 0) return offset;

        var utf8 = encoding == Scip.PositionEncoding.Utf8CodeUnitOffsetFromLineStart;
        var counted = 0;
        var i = 0;

        while (i < line.Length)
        {
            if (counted >= offset) return i;

            int width;
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                counted += utf8 ? 4 : 1;
                width = 2;
            }
            else
            {
                var c = line[i];
                counted += utf8 ? c < 0x80 ? 1 : c < 0x800 ? 2 : 3 : 1;
                width = 1;
            }

            i += width;
        }

        return line.Length + Math.Max(0, offset - counted);
    }

    /// <summary>
    /// The directory an index's paths are relative to. scip.proto calls project_root a
    /// "URI-encoded absolute path", which every indexer writes as a file: URI, but an
    /// index that puts a bare path there is readable and there is no reason to refuse it.
    ///
    /// Anything else - empty, relative, or a URI that is not a file - comes back as it
    /// arrived and is refused by <see cref="Writer.Begin"/>, which is the one place that
    /// decision is made.
    /// </summary>
    private static string RootOf(string? projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return "";

        return Uri.TryCreate(projectRoot, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : projectRoot;
    }
}

/// <summary>
/// The dotted display name a SCIP moniker describes. This is what makes an imported index
/// usable at all.
///
/// vela queries on occurrence.symbol, a dotted name - ScentVerdict.Data.Perfume.Status -
/// and the whole-dotted-segment rule in <see cref="Query.QueryHelper"/> depends on that
/// shape. A foreign index has no such name anywhere in it. scip-typescript hands over
///
///     scip-typescript npm scentverdict-mobile 0.0.1 src/composables/`useApi.ts`/useAsyncData().
///
/// and its SymbolInformation.display_name is empty on every symbol it emits. So the name
/// has to be derived from the moniker, and the derivation is:
///
///   <b>Drop the scheme and the package. Take each descriptor's name, with its escaping
///   removed and its suffix, disambiguator and brackets discarded. Join them with dots,
///   and let no descriptor contribute more than one segment.</b>
///
/// The grammar is the one in the comment above `message Symbol` in scip.proto:
///
///     symbol     ::= scheme ' ' manager ' ' package-name ' ' version ' ' descriptor+
///                  | 'local ' local-id
///     namespace  ::= name '/'      type           ::= name '#'
///     term       ::= name '.'      meta           ::= name ':'
///     macro      ::= name '!'      method         ::= name '(' disambiguator? ').'
///     parameter  ::= '(' name ')'  type-parameter ::= '[' name ']'
///
/// Why the descriptor names and nothing else. scip.proto: "The list of descriptors for a
/// symbol should together form a fully qualified name for the symbol." That is the same
/// sentence that describes what vela's display name is for, so the two grammars are
/// saying the same thing in different punctuation, and the translation is punctuation.
/// The package is dropped because vela's names never carried an assembly, and because a
/// scip-dotnet index defaults every source-declared symbol to the placeholder package
/// `. .` unless --allow-global-symbol-definitions is passed: a name built from it would
/// differ between two indexes of the same code.
///
/// <b>Why one descriptor may never become two segments.</b> scip.proto says the
/// descriptors "together form a fully qualified name", so a descriptor is one name, and
/// vela matches a whole dotted segment at a time, so a dot inside one is a lie in both
/// directions. Measured on this very index before it was fixed: scip-typescript names
/// the module `useApi.ts`, the naive join stored src.composables.useApi.ts, and so
/// `refs useApi` answered nothing at all while `refs ts` answered fifteen occurrences
/// spread across every TypeScript module in the index. The name a developer types
/// answered nothing; the name nobody means answered everything. Two rules keep it from
/// happening: <see cref="SourceFile.WithoutExtension"/> takes the file extension off a
/// module's name, and <see cref="OneSegment"/> makes sure any dot still left cannot open
/// a segment. Both are documented where they are, and the invariant they exist for is
/// asserted over every symbol of a real captured index rather than over examples.
///
/// Three consequences worth knowing before relying on it.
///
/// <b>A method loses its signature.</b> `Publish(+1).` becomes `Publish`, so two
/// overloads share one display name where vela's own C# names tell them apart by
/// parameter list. Every occurrence of both is still returned by `refs Publish`, which is
/// the query anyone actually types; what is lost is the ambiguity block's ability to
/// separate them.
///
/// <b>A module loses its file extension, and only a module.</b> `useApi.ts/` is a
/// namespace descriptor naming a file, so the name is useApi and the qualified form a
/// reader types is `useApi.useAsyncData`. A type, term or method descriptor keeps every
/// character of its name, extension-shaped or not: a class really called Wrapper.ts is
/// named after code rather than after a file, and shortening it would be inventing.
///
/// <b>A .NET generic loses its arity, deliberately.</b> vela and scip-dotnet both spell a
/// type descriptor with the CLI metadata name, so ILogger&lt;T&gt; is ``ILogger`1``. A
/// display name of ``ILogger`1`` is one no reader would type and `refs ILogger` would
/// miss it - 563 occurrences on the real solution. So a trailing backtick-and-digits is
/// removed from a TYPE descriptor's name and from nothing else. It merges Task and
/// Task&lt;T&gt; into one display name, which is the same set `refs Task` already returns
/// from a directly loaded index, because the query strips type arguments before matching.
/// </summary>
public static class MonikerName
{
    /// <summary>
    /// The display name for a moniker, or the moniker itself when it does not parse.
    ///
    /// The fallback is deliberate and is what <see cref="ScipLoader"/> already did for a
    /// foreign symbol: their symbol is the only name it has, and a name invented from
    /// half a parse would be worse than an ugly one that is true. `local N` is returned
    /// unchanged too - it names nothing on its own, and namespacing it is
    /// <see cref="ScipImporter"/>'s job because only that knows the document.
    /// </summary>
    public static string For(string moniker)
    {
        if (moniker.Length == 0 || moniker.StartsWith("local ", StringComparison.Ordinal)) return moniker;

        var at = 0;

        // scheme, manager, package name, version. A space inside any of them is escaped
        // by doubling it, so this cannot be a split on ' '.
        for (var part = 0; part < 4; part++)
            if (!SkipPart(moniker, ref at)) return moniker;

        var name = new StringBuilder();
        var descriptors = 0;

        while (at < moniker.Length)
        {
            if (!ReadDescriptor(moniker, ref at, out var descriptor)) return moniker;

            if (descriptors > 0) name.Append('.');
            name.Append(OneSegment(descriptor));
            descriptors++;
        }

        // The grammar requires at least one descriptor after the package.
        return descriptors == 0 ? moniker : name.ToString();
    }

    /// <summary>
    /// Steps past one space-terminated part of the package prefix. scip.proto, of the
    /// scheme, the manager, the package name and the version alike: "any UTF-8, escape
    /// spaces with double space", so a doubled space is a literal space inside the part
    /// and only a single one ends it.
    /// </summary>
    private static bool SkipPart(string moniker, ref int at)
    {
        while (at < moniker.Length)
        {
            if (moniker[at] != ' ')
            {
                at++;
                continue;
            }

            if (at + 1 < moniker.Length && moniker[at + 1] == ' ')
            {
                at += 2;
                continue;
            }

            at++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads one descriptor and yields the name in it, or returns false when what is
    /// there is not a descriptor. Every suffix in the grammar is handled, including the
    /// two that bracket their name rather than following it.
    /// </summary>
    private static bool ReadDescriptor(string moniker, ref int at, out string descriptor)
    {
        descriptor = "";

        switch (moniker[at])
        {
            // type-parameter ::= '[' name ']'
            case '[':
                at++;
                if (!ReadName(moniker, ref at, out descriptor)) return false;
                return Expect(moniker, ref at, ']');

            // parameter ::= '(' name ')'
            case '(':
                at++;
                if (!ReadName(moniker, ref at, out descriptor)) return false;
                return Expect(moniker, ref at, ')');
        }

        if (!ReadName(moniker, ref at, out descriptor)) return false;
        if (at >= moniker.Length) return false;

        switch (moniker[at])
        {
            // namespace ::= name '/'. This is the descriptor an indexer uses for a
            // module, and a module's name is a file name in every language that has no
            // separate namespace of its own, so the file extension comes off here.
            case '/':
                at++;
                descriptor = SourceFile.WithoutExtension(descriptor);
                return true;

            // meta and macro. A term's '.' is the same shape.
            case '.' or ':' or '!':
                at++;
                return true;

            case '#':
                at++;
                descriptor = WithoutGenericArity(descriptor);
                return true;

            // method ::= name '(' disambiguator? ').'. The disambiguator is what tells
            // two overloads apart in the moniker and there is no room for it in a dotted
            // name, so it is read and discarded.
            case '(':
                at++;
                while (at < moniker.Length && moniker[at] != ')') at++;
                return Expect(moniker, ref at, ')') && Expect(moniker, ref at, '.');

            default:
                return false;
        }
    }

    /// <summary>
    /// A name, plain or backtick-escaped. scip.proto: an escaped identifier is
    /// "'`' (escaped-character)+ '`'" and escaped characters are "any UTF-8, escape
    /// backticks with double backtick", so a doubled backtick inside one is a literal
    /// backtick and a single one closes it.
    /// </summary>
    private static bool ReadName(string moniker, ref int at, out string name)
    {
        name = "";

        if (at < moniker.Length && moniker[at] == '`')
        {
            at++;
            var escaped = new StringBuilder();

            while (at < moniker.Length)
            {
                if (moniker[at] != '`')
                {
                    escaped.Append(moniker[at++]);
                    continue;
                }

                if (at + 1 < moniker.Length && moniker[at + 1] == '`')
                {
                    escaped.Append('`');
                    at += 2;
                    continue;
                }

                at++;
                name = escaped.ToString();
                return name.Length > 0;
            }

            // Ran off the end with the identifier still open.
            return false;
        }

        var start = at;
        while (at < moniker.Length && IsIdentifierCharacter(moniker[at])) at++;

        name = moniker[start..at];
        return name.Length > 0;
    }

    private static bool Expect(string moniker, ref int at, char c)
    {
        if (at >= moniker.Length || moniker[at] != c) return false;
        at++;
        return true;
    }

    /// <summary>
    /// One descriptor name, as one segment: every dot left in it replaced by an
    /// underscore.
    ///
    /// This is the second half of "a descriptor is one segment", and it is what makes
    /// the first half - <see cref="SourceFile.WithoutExtension"/> - safe to keep narrow.
    /// A dot can be inside a descriptor name for reasons no list of extensions covers:
    /// lib.es5.d.ts is a module named lib.es5 once its extension is off, Card.vue is a
    /// component whose extension is not a language vela knows, and a backtick-escaped
    /// identifier may contain any UTF-8 at all. Every one of those must contribute one
    /// segment, because vela matches a whole dotted segment at a time and a name that
    /// splits is wrong in both directions at once: the segment a reader would type is no
    /// longer there to be matched, and a segment nobody means - ts - becomes a catch-all
    /// that answers for every module in the index.
    ///
    /// <b>Replaced rather than escaped.</b> An escape would have to be a character
    /// vela's names have never carried, so the reader would have to know it and type it
    /// through a shell that has its own opinion about backslashes, for a name they never
    /// asked to be complicated. An underscore is a character every name in the index
    /// already carries: it is an identifier character to <see cref="Query.QueryHelper"/>,
    /// so lib_es5 behaves exactly like any other segment under the matching rule, the
    /// substring prescan and the full-text index alike, and it is typeable and safe
    /// wherever it lands, including as the first character of a name where '-' would be
    /// read as a command-line option.
    ///
    /// <b>What it costs.</b> A descriptor really named lib_es5 and one named lib.es5
    /// would arrive at one display name, and `refs` on it would answer for both. Nothing
    /// is lost from the index: occurrence.scip_symbol holds the moniker exactly as the
    /// index wrote it, so which is which is still there to be read, and an export still
    /// puts back what came in.
    /// </summary>
    internal static string OneSegment(string name) => name.Replace('.', '_');

    /// <summary>
    /// scip.proto: identifier-character ::= '_' | '+' | '-' | '$' | ASCII letter or
    /// digit. ASCII exactly as written, which is the same reading
    /// <see cref="Harvest.ScipMoniker"/> escapes by, so a name one of them escapes is a
    /// name this one unescapes.
    /// </summary>
    private static bool IsIdentifierCharacter(char c) =>
        c is '_' or '+' or '-' or '$'
        || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>
    /// A .NET type descriptor's name without the CLI's generic arity suffix: ILogger`1
    /// becomes ILogger. Applied to type descriptors only, and only when a backtick is
    /// followed by digits to the end of the name, which is the exact shape the CLI
    /// specifies and which no ordinary identifier in any language has.
    /// </summary>
    private static string WithoutGenericArity(string name)
    {
        var tick = name.LastIndexOf('`');
        if (tick <= 0 || tick == name.Length - 1) return name;

        for (var i = tick + 1; i < name.Length; i++)
            if (!char.IsAsciiDigit(name[i])) return name;

        return name[..tick];
    }
}

/// <summary>
/// The source files vela recognises by name: which extensions there are, and what
/// language each one means.
///
/// One table, because the two questions asked of it are one question. A document whose
/// index declared no language is given one from its extension, and a module descriptor
/// that names a file has that extension taken off its name; a set of extensions
/// recognised by the first and not the second would mean vela calling something a
/// TypeScript file and, three lines later, treating .ts as part of a module's name.
///
/// The longest match wins, which is what makes the declaration extensions work:
/// reactivity.d.ts is a module named reactivity, not one named reactivity.d, and a real
/// index of a TypeScript project is mostly .d.ts. Matching is case-insensitive, because
/// Index.CSHTML is a Razor view on the file systems that allow it.
/// </summary>
internal static class SourceFile
{
    private static readonly (string Extension, string Language)[] Kinds =
    [
        (".cs", "csharp"), (".vb", "vb"), (".cshtml", "razor"), (".razor", "razor"),
        (".d.ts", "typescript"), (".d.mts", "typescript"), (".d.cts", "typescript"),
        (".ts", "typescript"), (".tsx", "typescript"), (".mts", "typescript"), (".cts", "typescript"),
        (".js", "javascript"), (".jsx", "javascript"), (".mjs", "javascript"), (".cjs", "javascript"),
        (".py", "python"), (".pyi", "python"), (".go", "go"), (".rs", "rust"),
        (".java", "java"), (".kt", "kotlin"), (".kts", "kotlin"), (".scala", "scala"),
        (".rb", "ruby"), (".php", "php"), (".dart", "dart"),
        (".c", "c"), (".h", "c"),
        (".cpp", "cpp"), (".cc", "cpp"), (".cxx", "cpp"), (".hpp", "cpp"), (".hh", "cpp")
    ];

    /// <summary>The language a path's extension implies, or "unknown".</summary>
    public static string LanguageOf(string path) => LongestMatch(path, minimumStem: 0)?.Language ?? "unknown";

    /// <summary>
    /// A file's name without its extension, or the name unchanged when it does not end
    /// in one vela recognises.
    ///
    /// A stem of at least one character is required, so a name that is nothing but an
    /// extension - a TypeScript project really can hold a module called .ts - keeps it
    /// rather than becoming empty and contributing no segment at all.
    /// </summary>
    public static string WithoutExtension(string name)
    {
        var kind = LongestMatch(name, minimumStem: 1);
        return kind is null ? name : name[..^kind.Value.Extension.Length];
    }

    private static (string Extension, string Language)? LongestMatch(string name, int minimumStem)
    {
        (string Extension, string Language)? best = null;

        foreach (var kind in Kinds)
        {
            if (name.Length - kind.Extension.Length < minimumStem) continue;
            if (!name.EndsWith(kind.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || kind.Extension.Length > best.Value.Extension.Length) best = kind;
        }

        return best;
    }
}
