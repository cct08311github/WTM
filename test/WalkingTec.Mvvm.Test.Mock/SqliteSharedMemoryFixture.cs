using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace WalkingTec.Mvvm.Test.Mock
{
    /// <summary>
    /// How a test-fixture <c>DbContext</c> provisions its SQLite database. See
    /// <see cref="SqliteSharedMemoryFixture"/>'s remarks for when to use which.
    /// </summary>
    public enum SqliteTestDbMode
    {
        /// <summary>
        /// Shared-cache in-memory (<c>DataSource={name}?mode=memory&amp;cache=shared</c>).
        /// Fast, zero filesystem I/O. Fine for tests that use multiple <c>DbContext</c>
        /// instances against the same logical database SEQUENTIALLY (never more than one
        /// actor actively racing a write at a time) — e.g. plain CRUD/read-back assertions.
        /// NOT safe as the default for genuinely-racing concurrency tests (T-CONC / T-ABBA-CONC
        /// family) — see <see cref="SqliteSharedMemoryFixture"/>'s remarks for why.
        /// </summary>
        SharedMemory,

        /// <summary>
        /// Per-test file-based database in WAL journal mode (<c>DataSource={path}</c>, WAL
        /// applied on open). Real per-connection concurrency: each actor gets a genuinely
        /// distinct OS-level file handle, not a name-keyed in-process shared-cache slot, and
        /// WAL's single-writer/multi-reader model gives SQLite's own lock manager real headroom
        /// to serialize writers instead of racing table-level locks. Required for tests that
        /// hold multiple <c>DbContext</c>/<c>SqliteConnection</c> instances open and ACTIVELY
        /// RACING (concurrent <c>Task.Run</c> actors, or rapid sequential open/close cycles
        /// under heavy process-level parallelism) against the same logical database.
        /// </summary>
        FileWal,
    }

    /// <summary>
    /// Shared helper for tests that need a real relational SQLite database backing
    /// <em>multiple concurrent</em> <c>DbContext</c>/<c>SqliteConnection</c> instances — the
    /// "shared-in-memory" / "file-WAL" patterns used throughout WalkingTec.Mvvm.WorkFlow.Test's
    /// concurrency (T-CONC / T-ABBA-CONC) tests and a few Core.Test cache tests. NEVER switch
    /// these tests to EF Core's InMemory provider: it cannot translate
    /// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> or several correlated-subquery LINQ shapes
    /// these fixtures exercise (Issue #119/#162).
    /// </summary>
    /// <remarks>
    /// <para><b>#709 CORRECTED root-cause mechanism (2026-07-16 minimal repro against pinned
    /// Microsoft.Data.Sqlite 10.0.9 — this supersedes an earlier, INCORRECT #709 comment on
    /// this type that claimed connection pooling was structurally impossible here; that
    /// claim was never verified against the actual connection-string form these fixtures use
    /// and must not be reintroduced):</b></para>
    ///
    /// <para>The connection string these fixtures build
    /// (<c>DataSource={name}?mode=memory&amp;cache=shared</c>) puts <c>mode=memory</c> and
    /// <c>cache=shared</c> INSIDE the <c>DataSource</c> keyword's URI value, not as separate
    /// <c>Mode=…</c>/<c>Cache=…</c> connection-string keywords. Microsoft.Data.Sqlite's
    /// connection-string parser only recognizes <c>Mode</c>/<c>Cache</c> as top-level keywords
    /// (semicolon-separated, e.g. <c>DataSource=name;Mode=Memory;Cache=Shared</c>) — in the
    /// URI-query form, <c>SqliteConnectionStringBuilder.Mode</c> stays at its default
    /// (<c>ReadWriteCreate</c>), NOT <c>Memory</c>. <c>SqliteConnectionFactory.GetPoolGroup()</c>'s
    /// <c>isNonPooled</c> check (<c>DataSource == ":memory:" || Mode == Memory ||
    /// DataSource.Length == 0 || !Pooling</c>) is therefore FALSE for every connection string
    /// this fixture built before this fix — connection pooling stayed ACTIVE, and two
    /// concurrent actors could be handed the same pooled physical connection. That is a
    /// distinct, more severe issue than the "just widen busy_timeout" framing of earlier #620 /
    /// #629 / #709-first-pass fixes: no amount of PRAGMA tuning closes a pooled-connection
    /// hand-off race.</para>
    ///
    /// <para>Separately — even granting a distinct physical connection per actor — shared-cache
    /// in-memory SQLite (<c>cache=shared</c>) uses COARSE table-level locking, under which
    /// <c>busy_timeout</c> only gives SQLite's retry loop a probabilistic chance to resolve
    /// contention before giving up; under genuine heavy parallel load it still measurably
    /// flakes (empirically reproduced by two independent adversarial reviews: round 1 found
    /// 2 failures / 128 iterations, round 2 found 1 failure / 250 iterations, even with the
    /// busy_timeout-propagation-only fix applied everywhere). Widening the PRAGMA window
    /// further is not a fix — it only lowers the observed failure rate.</para>
    ///
    /// <para><b>The fix (#709 round 2): stop fighting shared-cache in-memory locking for
    /// genuinely-racing fixtures.</b> Tests that hold multiple <c>DbContext</c> instances
    /// ACTIVELY RACING against the same logical database (the T-CONC / T-ABBA-CONC family —
    /// at minimum the fixtures backing <c>All_TCONC3</c>, <c>Any_TCONC1</c>,
    /// <c>WithdrawAsync_TCONC2</c>, <c>AdvanceAsync_ConcurrentCalls_…</c>,
    /// <c>ConcurrentAppendAsync_…</c>, and <c>T_ABBA_2902_CONC_01</c>) provision
    /// <see cref="SqliteTestDbMode.FileWal"/> instead: a unique per-test temp-file SQLite
    /// database with <c>PRAGMA journal_mode=WAL</c> applied on every connection open. Each
    /// actor's <c>DbContext</c> opens its OWN connection to the same file — a genuinely
    /// distinct OS file handle, so the pooling question above cannot arise — and WAL's
    /// single-writer/multi-reader model gives SQLite's native lock manager real per-connection
    /// concurrency instead of a coarse shared-cache table lock. WAL fully supports
    /// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/correlated subqueries, so nothing is lost
    /// versus the original reason shared-memory was chosen over EF's InMemory provider.</para>
    ///
    /// <para>Tests that only ever have ONE actor writing at a time (sequential CRUD/read-back —
    /// e.g. <c>LookupCacheTests</c>' Bug112 helpers, and the non-racing majority of
    /// <c>SequentialTests</c>/<c>EngineTests</c>/<c>AbbaFixTests</c>) are NOT destabilized by
    /// staying on <see cref="SqliteTestDbMode.SharedMemory"/> — the pooling/coarse-locking
    /// issues above only bite under genuine concurrent contention. Do not blanket-migrate every
    /// fixture to file-WAL; reserve it for fixtures that actually race.</para>
    ///
    /// <para>Both signatures observed historically are closed by the combination above:
    /// <c>SQLITE_BUSY</c> ("database is locked", code 5) statement-time contention — covered
    /// for racing fixtures by WAL's real concurrency plus <see cref="SqliteBusyTimeoutInterceptor"/>'s
    /// <c>busy_timeout</c>; and the downstream "cannot start a transaction within a transaction"
    /// (code 1) <c>SqliteTransaction</c> desync (Issue #629) that follows an unresolved BUSY on
    /// COMMIT. <see cref="SqliteBusyRetryExecutionStrategy"/> additionally retries the rare
    /// case of <c>SQLITE_BUSY</c> raised from inside <c>SqliteConnection.Open()</c> itself
    /// (observed only under shared-cache in-memory's schema-lock-at-open behavior; file-based
    /// <c>Open()</c> is a plain OS file-handle open and does not exhibit this) — kept registered
    /// on both modes as cheap defense-in-depth, not as the primary fix.</para>
    ///
    /// <para>This helper exists so every fixture builds its connection string, file path, and
    /// keep-alive/creation connection through this ONE type instead of hand-rolling (and
    /// sometimes forgetting) the mitigation per test file.</para>
    /// </remarks>
    public static class SqliteSharedMemoryFixture
    {
        /// <summary>
        /// Busy-timeout window (ms) applied to every connection touching a shared database
        /// (shared-cache in-memory OR file-WAL). Single source of truth for
        /// <see cref="SqliteBusyTimeoutInterceptor"/>, <see cref="OpenKeepAliveWithBusyTimeout"/>,
        /// and <see cref="CreateFileWalDatabase"/> — see Issue #629's widened-timeout rationale
        /// (3000ms was insufficient under CI parallel load; widened to 8000ms).
        /// </summary>
        public const int BusyTimeoutMs = 8000;

        // ── Shared-cache in-memory (SqliteTestDbMode.SharedMemory) ─────────────────────

        /// <summary>
        /// Builds a fresh, collision-safe shared-cache in-memory database name suitable for
        /// use in a connection string built by <see cref="BuildConnectionString"/>.
        /// </summary>
        public static string NewDbName(string prefix = "Wf") =>
            $"{prefix}_{Guid.NewGuid():N}";

        /// <summary>
        /// Builds the Microsoft.Data.Sqlite connection string for a shared-cache in-memory
        /// database with the given name (<c>DataSource={dbName}?mode=memory&amp;cache=shared</c>).
        /// See this type's remarks for why this is NOT safe as the default for genuinely-racing
        /// concurrency fixtures — use <see cref="BuildFileWalConnectionString"/> for those.
        /// </summary>
        public static string BuildConnectionString(string dbName) =>
            $"DataSource={dbName}?mode=memory&cache=shared";

        /// <summary>
        /// Opens a raw "keep-alive" connection to the shared-cache in-memory database named
        /// <paramref name="dbName"/> and applies <c>PRAGMA busy_timeout</c> as literally the
        /// first statement — required because this connection never goes through EF Core's
        /// <see cref="SqliteBusyTimeoutInterceptor"/>. The caller owns disposal; keep it open
        /// for the lifetime of the test (or test class) so the in-memory database isn't
        /// dropped once the last EF-Core connection against it closes.
        /// </summary>
        /// <remarks>
        /// The <c>Open()</c> call itself — not just statements issued after it succeeds —
        /// retries on <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c>, since <c>PRAGMA busy_timeout</c>
        /// cannot protect a connection's own open (see this type's remarks). This is the
        /// raw-connection counterpart to <see cref="SqliteBusyRetryExecutionStrategy"/>, which
        /// covers the same gap for EF-Core-managed connections.
        /// </remarks>
        public static SqliteConnection OpenKeepAliveWithBusyTimeout(string dbName)
        {
            var connection = new SqliteConnection(BuildConnectionString(dbName));
            OpenWithBusyRetry(connection);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
            cmd.ExecuteNonQuery();
            return connection;
        }

        // ── File-WAL (SqliteTestDbMode.FileWal) — genuinely-racing fixtures ────────────

        private static readonly string[] FileDbSidecarSuffixes = { string.Empty, "-wal", "-shm", "-journal" };

        /// <summary>
        /// Builds a fresh, collision-safe temp-file path for a <see cref="SqliteTestDbMode.FileWal"/>
        /// database. Does not create the file — the first connection opened against it (typically
        /// via <see cref="CreateFileWalDatabase"/> or a <c>DbContext.Database.EnsureCreated()</c>
        /// call routed through <see cref="BuildFileWalConnectionString"/>) creates it lazily.
        /// </summary>
        public static string NewFileDbPath(string prefix = "Wf") =>
            Path.Combine(Path.GetTempPath(), $"wtmwf-{prefix}-{Guid.NewGuid():N}.db");

        /// <summary>
        /// Builds the Microsoft.Data.Sqlite connection string for a per-test file-based
        /// database. <c>Pooling=False</c> is explicit (not merely relied-upon-by-inference):
        /// every actor gets a brand-new physical connection to the file, never a pooled
        /// hand-off — the exact property the #709 round-2 repro proved the URI-query
        /// shared-cache in-memory form silently lacked. See this type's remarks.
        /// </summary>
        public static string BuildFileWalConnectionString(string dbPath) =>
            $"DataSource={dbPath};Pooling=False";

        /// <summary>
        /// Opens (creating if needed) the file-WAL database at <paramref name="dbPath"/>,
        /// applies <c>PRAGMA journal_mode=WAL</c> and <c>PRAGMA busy_timeout</c>, and returns
        /// the open connection. Unlike <see cref="OpenKeepAliveWithBusyTimeout"/> for
        /// shared-cache in-memory, holding this connection open is NOT required to keep the
        /// database alive (it is a real file on disk) — callers may use this purely to force
        /// WAL mode + schema creation up front, then dispose it immediately, or hold it open
        /// for parity with existing keep-alive-shaped test code. Retries the <c>Open()</c>
        /// call itself on <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> for symmetry with the
        /// shared-cache path, though file-based <c>Open()</c> does not exercise the
        /// schema-lock-at-open behavior that motivated that retry there.
        /// </summary>
        public static SqliteConnection CreateFileWalDatabase(string dbPath)
        {
            var connection = new SqliteConnection(BuildFileWalConnectionString(dbPath));
            OpenWithBusyRetry(connection);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode = WAL;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
                cmd.ExecuteNonQuery();
            }
            return connection;
        }

        /// <summary>
        /// Best-effort deletes the main database file at <paramref name="dbPath"/> plus its
        /// WAL/SHM/rollback-journal sidecar files. Deliberately swallows all exceptions per
        /// file: a lingering handle (e.g. a slow WAL checkpoint, or the "collation sequence /
        /// active statements" dispose-ordering race #709 also flagged) must never fail a test
        /// during teardown — leaked temp files in the OS temp directory are a cosmetic cost,
        /// not a correctness one. Call from a <c>finally</c> block after every actor's
        /// <c>DbContext</c>/connection has been disposed.
        /// </summary>
        public static void DeleteFileDatabase(string dbPath)
        {
            foreach (var suffix in FileDbSidecarSuffixes)
            {
                try
                {
                    File.Delete(dbPath + suffix);
                }
                catch
                {
                    // Best-effort: never let temp-file cleanup fail a test.
                }
            }
        }

        // ── Shared open-retry helper ────────────────────────────────────────────────

        /// <summary>
        /// Opens <paramref name="connection"/>, retrying on <c>SQLITE_BUSY</c> (5) /
        /// <c>SQLITE_LOCKED</c> (6) with a short jittered backoff. Mirrors the retry
        /// classification in <see cref="SqliteBusyRetryExecutionStrategy"/> so the raw-connection
        /// and EF-Core-managed paths behave identically under contention.
        /// </summary>
        private static void OpenWithBusyRetry(SqliteConnection connection)
        {
            const int maxAttempts = 20;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    connection.Open();
                    return;
                }
                catch (SqliteException ex) when (
                    attempt < maxAttempts && SqliteBusyRetryExecutionStrategy.IsSqliteBusyOrLocked(ex))
                {
                    Thread.Sleep(Math.Min(5 * attempt, 100) + Random.Shared.Next(0, 25));
                }
            }
        }
    }

    /// <summary>
    /// EF Core connection interceptor that applies <c>PRAGMA journal_mode=WAL</c> and
    /// <c>PRAGMA busy_timeout</c> to every connection the moment it physically opens — the
    /// earliest hook available, and a single DRY application point covering every
    /// <c>DbContext</c> instance built against either a shared-cache in-memory or a file-WAL
    /// database, including ones constructed inline inside concurrency race loops.
    /// </summary>
    /// <remarks>
    /// <para>Microsoft.Data.Sqlite has no connection-string keyword that maps to the native
    /// <c>sqlite3_busy_timeout()</c> API (its "Default Timeout" keyword only sets
    /// <c>SqliteCommand.CommandTimeout</c>, which governs step-time retries on already-open
    /// connections, not the lock acquisition that can occur on a brand new connection's very
    /// first statement). So busy_timeout must be applied by running <c>PRAGMA busy_timeout</c>
    /// as literally the first statement on every connection. <c>ConnectionOpened</c>/
    /// <c>ConnectionOpenedAsync</c> fire immediately after the underlying ADO.NET connection
    /// physically opens and BEFORE EF Core (or any caller) issues its first query.</para>
    ///
    /// <para><c>PRAGMA journal_mode=WAL</c> is applied to file-based (<see cref="SqliteTestDbMode.FileWal"/>)
    /// connections only — detected from the connection string, NOT issued unconditionally.
    /// This was empirically verified, not assumed: an earlier draft of this interceptor issued
    /// the PRAGMA on every connection on the (unverified) assumption that SQLite would silently
    /// keep shared-cache in-memory databases on "memory" journal mode; that assumption was
    /// WRONG — requesting WAL on a <c>cache=shared</c> in-memory connection instead throws
    /// <c>SqliteException</c> "SQLite Error 8: attempt to write a readonly database", which
    /// surfaced immediately as every shared-memory fixture's very first connection open failing.
    /// WAL is the mechanism that gives <see cref="SqliteTestDbMode.FileWal"/> fixtures real
    /// per-connection concurrency (see <see cref="SqliteSharedMemoryFixture"/>'s remarks for the
    /// full #709 root-cause write-up: the URI-query shared-cache in-memory connection-string
    /// form does NOT set <c>SqliteConnectionStringBuilder.Mode</c>, so Microsoft.Data.Sqlite's
    /// connection pool stays ACTIVE for it — file-WAL sidesteps that pooling question entirely
    /// by giving each actor a distinct OS file handle).</para>
    ///
    /// <para>Issue #629 root-cause note (2026-07-10, CI run 4735): a second, distinct flake
    /// signature — <c>SqliteException</c> "cannot start a transaction within a transaction"
    /// (error code 1, not 5) — was traced to Microsoft.Data.Sqlite's <c>SqliteTransaction
    /// .Commit()</c> having no try/finally around its native "COMMIT;" call, while
    /// <c>RollbackInternal()</c> unconditionally clears the wrapper's own transaction-state
    /// tracking even when the native "ROLLBACK;" statement itself fails. Under genuine SQLite
    /// lock contention (COMMIT's RESERVED→EXCLUSIVE lock upgrade blocked by a concurrent
    /// reader's SHARED lock), this asymmetry can leave a connection's wrapper-level transaction
    /// state out of sync with the native engine. Applying busy_timeout gives SQLite's own retry
    /// logic headroom to resolve lock contention before any caller reaches that failure path;
    /// moving genuinely-racing fixtures to file-WAL (#709 round 2) removes the coarse
    /// shared-cache table lock that made this contention likely in the first place.</para>
    /// </remarks>
    public sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
    {
        /// <summary>
        /// Detects a shared-cache in-memory connection string (contains <c>mode=memory</c> in
        /// the URI-query <c>DataSource</c> form this fixture builds) so <c>PRAGMA
        /// journal_mode=WAL</c> is never attempted against it — see this type's remarks for the
        /// empirical "attempt to write a readonly database" failure that motivated this check.
        /// </summary>
        private static bool IsSharedCacheInMemory(DbConnection connection) =>
            connection.ConnectionString?.Contains("mode=memory", StringComparison.OrdinalIgnoreCase) == true;

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (!IsSharedCacheInMemory(connection))
            {
                using var walCmd = connection.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                walCmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout = {SqliteSharedMemoryFixture.BusyTimeoutMs};";
                cmd.ExecuteNonQuery();
            }
            base.ConnectionOpened(connection, eventData);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!IsSharedCacheInMemory(connection))
            {
                await using var walCmd = connection.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                await walCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout = {SqliteSharedMemoryFixture.BusyTimeoutMs};";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }
    }

    /// <summary>
    /// EF Core <see cref="ExecutionStrategy"/> that retries on <c>SQLITE_BUSY</c> (5) /
    /// <c>SQLITE_LOCKED</c> (6), closing the one gap <see cref="SqliteBusyTimeoutInterceptor"/>
    /// cannot: <c>SQLITE_BUSY</c> raised from INSIDE
    /// <c>Microsoft.Data.Sqlite.SqliteConnection.Open()</c> itself, before
    /// <c>ConnectionOpened</c> ever fires and before any PRAGMA can be applied.
    /// </summary>
    /// <remarks>
    /// Kept registered on both <see cref="SqliteTestDbMode.SharedMemory"/> and
    /// <see cref="SqliteTestDbMode.FileWal"/> contexts as cheap defense-in-depth. It is NOT the
    /// primary fix for the #709 round-2 flake class — that is moving genuinely-racing fixtures
    /// to file-WAL (see <see cref="SqliteSharedMemoryFixture"/>'s remarks). EF Core's
    /// execution-strategy pipeline wraps and, on a retryable failure, re-runs the ENTIRE unit of
    /// work for every query and <c>SaveChanges</c> call — including the implicit connection
    /// <c>Open()</c> — so a retry gets a fresh attempt after a contending actor has had a chance
    /// to release its lock. Composes safely with <see cref="SqliteBusyTimeoutInterceptor"/> and
    /// with WalkingTec.Mvvm.WorkFlow's #667 execution-strategy-wrapped transactions —
    /// <c>WorkflowEngine</c> already routes every transaction through
    /// <c>Db.Database.CreateExecutionStrategy()</c>, so it automatically picks up whichever
    /// strategy a test's <c>DbContext</c> configures.
    ///
    /// Retry tuning is deliberately short (small linear backoff, capped low) rather than EF's
    /// default network-transient-oriented exponential backoff: this class of contention is
    /// microseconds-to-low-milliseconds SQLite lock contention on an in-process database, not a
    /// network call — a long backoff would only slow the test suite without improving the odds
    /// of success.
    /// </remarks>
    public sealed class SqliteBusyRetryExecutionStrategy : ExecutionStrategy
    {
        private const int MaxAttempts = 12;

        public SqliteBusyRetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: MaxAttempts, maxRetryDelay: TimeSpan.FromMilliseconds(200))
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => IsSqliteBusyOrLocked(exception);

        /// <summary>
        /// Walks <paramref name="exception"/>'s <c>InnerException</c> chain (covers both a bare
        /// <see cref="SqliteException"/> from query/connection execution and one wrapped inside
        /// EF Core's <c>DbUpdateException</c> from <c>SaveChanges</c>) looking for
        /// <c>SQLITE_BUSY</c> (5) or <c>SQLITE_LOCKED</c> (6).
        /// </summary>
        internal static bool IsSqliteBusyOrLocked(Exception? exception)
        {
            for (var ex = exception; ex is not null; ex = ex.InnerException)
            {
                if (ex is SqliteException sqliteEx &&
                    (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
