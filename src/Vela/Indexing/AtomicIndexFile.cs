namespace Vela.Indexing;

/// <summary>
/// The finished index could not be moved over the one it replaces.
///
/// It has its own type because it is the one write failure in a rebuild where nothing at
/// all went wrong with the work: the new index is complete, healthy and sitting on disk,
/// and only the last rename would not happen. On Windows that is what another process
/// holding the index open looks like, and "close whatever has it open" is advice no other
/// write failure wants - telling somebody to free disk space when the disk is not full
/// sends them to the wrong place.
///
/// It derives from <see cref="IOException"/> because that is what it is, and because a
/// caller that only wants to know "the index could not be written" then needs no new type
/// in its catch list to keep working.
/// </summary>
public sealed class IndexCommitException : IOException
{
    public IndexCommitException(string destination, Exception inner)
        : base(inner.Message, inner) => Destination = destination;

    /// <summary>The index that is still there, unchanged, because the move did not happen.</summary>
    public string Destination { get; }
}

/// <summary>
/// A rebuild that either replaces the index or leaves it exactly as it was.
///
/// `vela index` used to delete the database and then write a new one, so the window
/// between those two things was a window in which the user had no index at all. Anything
/// landing in it - Ctrl-C, an OOM kill, a full disk, a project that throws halfway through
/// the harvest - left them with nothing, where a moment earlier they had a working index.
/// On a solution where a full build takes minutes that is a bad trade for a moment's
/// inattention, and the person who pays it is the one who was not watching.
///
/// So the new index is built into a file beside the old one and moved over it when it is
/// finished and healthy. The move is a rename within one directory, which every filesystem
/// vela runs on makes atomic: a reader sees the old file or the new one and never a
/// half-written one.
///
/// <b>What this does and does not promise.</b> It promises that a failed or interrupted
/// rebuild leaves the previous index byte-identical, and that the build file does not
/// survive the run that made it. It does not promise anything about two rebuilds of one
/// solution running at once, which was never supported: both write one database and the
/// loser's work is discarded, whichever file it was written into.
///
/// On Windows a rename over a file another process has open fails rather than succeeding
/// invisibly. That is the right failure: the command reports it, and the index the other
/// process is reading is still the index on disk.
/// </summary>
public sealed class AtomicIndexFile : IDisposable
{
    private bool _committed;

    private AtomicIndexFile(string destination)
    {
        Destination = destination;
        Path = IndexPaths.TemporaryFor(destination);
    }

    /// <summary>The file to build into. Nothing else may read it.</summary>
    public string Path { get; }

    /// <summary>Where it goes when it is finished.</summary>
    public string Destination { get; }

    /// <summary>
    /// An empty build file beside the index, for a rebuild that starts from nothing.
    ///
    /// Anything already at that path is removed first. A build file that outlived its run
    /// is what a killed process leaves behind, it is evidence of nothing, and refusing to
    /// index because of one would turn a crash into a permanent failure.
    /// </summary>
    public static AtomicIndexFile Beside(string destination)
    {
        var file = new AtomicIndexFile(destination);
        Remove(file.Path);
        return file;
    }

    /// <summary>
    /// A build file seeded with the index that is already there, for a rebuild that keeps
    /// some of it.
    ///
    /// This is what `--incremental` needs. That mode reads the previous index to decide
    /// which projects it can honestly skip and then keeps those projects' rows, so it
    /// cannot start from an empty file - and it must not edit the live one, because a
    /// half-applied incremental rebuild is an index describing code that never existed
    /// together. The copy is taken AFTER the plan has been read from the original, so the
    /// two describe the same database.
    /// </summary>
    public static AtomicIndexFile CopyOf(string destination)
    {
        var file = new AtomicIndexFile(destination);
        Remove(file.Path);
        File.Copy(destination, file.Path);
        return file;
    }

    /// <summary>
    /// Puts the finished index in place. Called only when the build is complete and the
    /// connection to it is closed: a move with the database still open leaves the writer
    /// holding a file that is no longer where it was.
    ///
    /// <b>Only the .db moves, and that is correct only while vela sets no `journal_mode`.</b>
    /// Nothing in this codebase does - the only PRAGMA it issues is `user_version`, in
    /// <see cref="Schema"/> - so SQLite uses its default rollback journal, and a database
    /// closed cleanly deletes its own `-journal` before the connection returns. By the time
    /// this runs there is no sidecar beside the build file to move, and a `-wal` or `-shm`
    /// cannot exist because no connection ever asked for one.
    ///
    /// <b>If anybody switches the index to WAL, this method has to move the sidecars too.</b>
    /// A WAL database keeps `-wal` and `-shm` beside it, they are named from the database
    /// file, and they are part of the database rather than debris: moving the `.db` alone
    /// would leave the new index next to the OLD index's stale `-wal`, whose header the
    /// next open checks against a file it no longer describes. `journal_mode=TRUNCATE` and
    /// `PERSIST` are the same trap in a smaller way: both leave a `-journal` on disk after
    /// a clean close. The safe shapes are the current default, or moving every sidecar
    /// with the database in one commit.
    /// </summary>
    public void Commit()
    {
        try
        {
            File.Move(Path, Destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IndexCommitException(Destination, ex);
        }

        _committed = true;
    }

    /// <summary>
    /// Removes the build file unless it was committed, so a failed rebuild leaves no
    /// debris in the cache directory to be wondered about later.
    ///
    /// Best effort by design. This runs on the way out of a failure, and an exception
    /// thrown here would replace the real one - the reason the rebuild failed - with a
    /// complaint about tidying up.
    /// </summary>
    public void Dispose()
    {
        if (_committed) return;
        Remove(Path);
    }

    /// <summary>
    /// One database file and the sidecars SQLite writes beside it. The journal and the
    /// write-ahead log are part of the same database, so removing the file and leaving
    /// them would leave a later open reading a rollback journal that belongs to a database
    /// that is not there.
    /// </summary>
    private static void Remove(string path)
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do and nothing to be gained by saying so here: if the
                // build file cannot be removed, the next attempt to create it fails and
                // reports that, with the previous index still in place.
            }
        }
    }
}
