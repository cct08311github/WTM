using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// What is known about the length of the password column in the real database.
    /// </summary>
    public sealed class PasswordColumnInfo
    {
        private PasswordColumnInfo(bool isVerified, int? maxLength, string description)
        {
            IsVerified = isVerified;
            MaxLength = maxLength;
            Description = description;
        }

        /// <summary>False when the length could not be determined (<see cref="Description"/> says why).</summary>
        public bool IsVerified { get; }

        /// <summary>When <see cref="IsVerified"/>: the maximum number of characters, or null when the column is not limited.</summary>
        public int? MaxLength { get; }

        /// <summary>Short text for log messages: the reason when not verified, otherwise what was found.</summary>
        public string Description { get; }

        /// <summary>The length could not be determined.</summary>
        public static PasswordColumnInfo Unverified(string reason)
        {
            return new PasswordColumnInfo(false, null, reason ?? "unknown reason");
        }

        /// <summary>The column holds at most <paramref name="maxLength"/> characters.</summary>
        public static PasswordColumnInfo Limited(int maxLength)
        {
            return new PasswordColumnInfo(true, maxLength, "at most " + maxLength.ToString(CultureInfo.InvariantCulture) + " characters");
        }

        /// <summary>The column has no length limit that matters (MAX, text, or a database that does not enforce lengths).</summary>
        public static PasswordColumnInfo Unlimited { get; } = new PasswordColumnInfo(true, null, "not limited");
    }

    /// <summary>
    /// Finds out how many characters the password column of the user table can hold in the real database, which can differ
    /// from the EF model: a database created by an earlier version still has the old, shorter column after the model was widened.
    /// </summary>
    public interface IPasswordColumnInspector
    {
        /// <summary>
        /// Returns what is known about the password column. Must not throw: any failure is reported as
        /// <see cref="PasswordColumnInfo.Unverified"/>.
        /// </summary>
        /// <param name="dc">The data context the application uses for the user table.</param>
        /// <param name="userType">The mapped user type; null means <see cref="FrameworkUserBase"/>.</param>
        PasswordColumnInfo Inspect(IDataContext dc, Type userType);
    }

    /// <summary>
    /// The default <see cref="IPasswordColumnInspector"/>. It reads the column definition from the catalog of the database
    /// (INFORMATION_SCHEMA, or ALL_TAB_COLUMNS for Oracle and DaMeng) with a parameterized query on a separate connection, using the
    /// table, schema and column names of the EF model. It is only used when PBKDF2 is configured (with MD5, the default, it never runs).
    /// <list type="bullet">
    /// <item><description>SQL Server, MySQL, PostgreSQL: verified by the catalog query described above.</description></item>
    /// <item><description>Oracle, DaMeng: best effort, the same idea against ALL_TAB_COLUMNS (CHAR_LENGTH / DATA_LENGTH, whichever is smaller); any error means "cannot verify".</description></item>
    /// <item><description>SQLite and the EF in-memory provider do not enforce column lengths: reported as not limited without a query.</description></item>
    /// </list>
    /// Answers are cached per connection and column: a definite answer for one hour (so a schema change is noticed without a restart, but
    /// restart the application after any schema rollback anyway), "cannot verify" for five minutes. Only one caller at a time queries the
    /// database for a given column; a concurrent caller never waits, it gets the previous answer, or "cannot verify" (which means MD5, the
    /// safe side) when there is none yet. The query is bounded by a short connect timeout and a command timeout, so an unreachable database
    /// holds up at most one caller, and only briefly.
    /// </summary>
    public class PasswordColumnInspector : IPasswordColumnInspector
    {
        private const string PasswordPropertyName = "Password";

        private static readonly string[] SqlServerAndMySqlCharacterTypes = { "char", "varchar", "nchar", "nvarchar", "text", "ntext", "tinytext", "mediumtext", "longtext" };
        private static readonly string[] PgSqlCharacterTypes = { "character varying", "character", "text" };

        private static readonly Lazy<PasswordColumnInspector> _default = new Lazy<PasswordColumnInspector>(() => new PasswordColumnInspector());

        /// <summary>The shared instance used by <see cref="WTMContext"/> unless it is replaced there.</summary>
        public static PasswordColumnInspector Default => _default.Value;

        private readonly ConcurrentDictionary<string, Slot> _cache = new ConcurrentDictionary<string, Slot>();
        private readonly TimeSpan _retryAfterFailure;
        private readonly TimeSpan _verifiedLifetime;
        private readonly TimeSpan _connectTimeout;

        /// <param name="retryAfterFailure">How long "cannot verify" is remembered; default five minutes.</param>
        /// <param name="verifiedLifetime">How long a definite answer is remembered; default one hour.</param>
        /// <param name="connectTimeout">Longest wait for the catalog connection to open; default five seconds.</param>
        public PasswordColumnInspector(TimeSpan? retryAfterFailure = null, TimeSpan? verifiedLifetime = null, TimeSpan? connectTimeout = null)
        {
            _retryAfterFailure = retryAfterFailure ?? TimeSpan.FromMinutes(5);
            _verifiedLifetime = verifiedLifetime ?? TimeSpan.FromHours(1);
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        }

        private sealed class CacheEntry
        {
            public PasswordColumnInfo Info;
            public DateTime ExpiresUtc;
        }

        private sealed class Slot
        {
            public volatile CacheEntry Entry;
            public int Refreshing;
        }

        /// <summary>One row of the catalog: the type and the lengths it reports (null when the catalog has no value).</summary>
        public sealed class ColumnRow
        {
            public string DataType { get; set; }

            /// <summary>Length in characters (SQL Server: -1 means MAX).</summary>
            public long? CharLength { get; set; }

            /// <summary>Length in bytes; only reported by Oracle and DaMeng.</summary>
            public long? ByteLength { get; set; }
        }

        /// <inheritdoc />
        public PasswordColumnInfo Inspect(IDataContext dc, Type userType)
        {
            try
            {
                userType = userType ?? typeof(FrameworkUserBase);
                if (dc == null)
                {
                    return PasswordColumnInfo.Unverified("no data context");
                }
                var dbType = dc.DBType;
                if (dbType == DBTypeEnum.Memory || dbType == DBTypeEnum.SQLite)
                {
                    return PasswordColumnInfo.Unlimited;
                }
                if (dc is DbContext db == false)
                {
                    return PasswordColumnInfo.Unverified("the data context is not an EF Core DbContext");
                }
                var entity = db.Model.FindEntityType(userType);
                var property = entity?.FindProperty(PasswordPropertyName);
                var table = entity?.GetTableName();
                if (property == null || string.IsNullOrEmpty(table))
                {
                    return PasswordColumnInfo.Unverified("the user type is not mapped to a table");
                }
                var schema = entity.GetSchema() ?? db.Model.GetDefaultSchema();
                var column = property.GetColumnName(StoreObjectIdentifier.Table(table, schema)) ?? property.GetColumnName();
                if (string.IsNullOrEmpty(column))
                {
                    return PasswordColumnInfo.Unverified("the password property is not mapped to a column");
                }
                var key = string.Join("|", dbType, Fingerprint(db.Database.GetConnectionString()), schema, table, column);
                var slot = _cache.GetOrAdd(key, k => new Slot());
                var entry = slot.Entry;
                if (entry != null && entry.ExpiresUtc > DateTime.UtcNow)
                {
                    return entry.Info;
                }
                //single flight: one caller refreshes, the others do not wait for it
                if (Interlocked.CompareExchange(ref slot.Refreshing, 1, 0) != 0)
                {
                    return entry?.Info ?? PasswordColumnInfo.Unverified("the password column is being checked by another request");
                }
                try
                {
                    PasswordColumnInfo info;
                    try
                    {
                        var rows = ReadColumnRows(dc, dbType, schema, table, column);
                        info = Interpret(dbType, rows);
                    }
                    catch (Exception ex)
                    {
                        //only the type: messages of database errors can contain server names and similar details
                        info = PasswordColumnInfo.Unverified("querying the database catalog failed: " + ex.GetType().Name);
                    }
                    slot.Entry = new CacheEntry { Info = info, ExpiresUtc = DateTime.UtcNow + (info.IsVerified ? _verifiedLifetime : _retryAfterFailure) };
                    return info;
                }
                finally
                {
                    Volatile.Write(ref slot.Refreshing, 0);
                }
            }
            catch (Exception ex)
            {
                return PasswordColumnInfo.Unverified("inspecting the password column failed: " + ex.GetType().Name);
            }
        }

        private static string Fingerprint(string connectionString)
        {
            //hashed so that the cache does not keep a second copy of credentials in its keys
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString ?? string.Empty)));
        }

        /// <summary>
        /// Reads the catalog rows that describe the column. Virtual so that a test can supply rows without a database.
        /// Uses a new context (and so a separate connection) because the request context may be in the middle of reading.
        /// </summary>
        protected virtual IReadOnlyList<ColumnRow> ReadColumnRows(IDataContext dc, DBTypeEnum dbType, string schema, string table, string column)
        {
            var query = CreateQuery(dbType, schema, table, column);
            using (var fresh = dc.CreateNew())
            {
                var connection = ((DbContext)fresh).Database.GetDbConnection();
                var wasClosed = connection.State != ConnectionState.Open;
                try
                {
                    if (wasClosed)
                    {
                        using (var cts = new CancellationTokenSource(_connectTimeout))
                        {
                            var opening = connection.OpenAsync(cts.Token);
                            //the wait is a backstop for providers that ignore the token
                            if (opening.Wait(_connectTimeout + TimeSpan.FromSeconds(2)) == false)
                            {
                                throw new TimeoutException("opening the catalog connection timed out");
                            }
                        }
                    }
                    var raw = ExecuteRows(connection, query.Sql, query.Parameters, dbType == DBTypeEnum.Oracle || dbType == DBTypeEnum.DaMeng);
                    return raw.Select(r => ToRow(dbType, r)).ToList();
                }
                finally
                {
                    if (wasClosed)
                    {
                        connection.Close();
                    }
                }
            }
        }

        /// <summary>The catalog query for a provider. Virtual so that a test can point the read at a database that has no catalog views.</summary>
        protected virtual CatalogQuery CreateQuery(DBTypeEnum dbType, string schema, string table, string column)
        {
            return BuildQuery(dbType, schema, table, column);
        }

        /// <summary>A catalog query and its parameters (name without prefix, value).</summary>
        public sealed class CatalogQuery
        {
            public string Sql { get; set; }
            public IList<KeyValuePair<string, string>> Parameters { get; set; }
        }

        /// <summary>
        /// Builds the catalog query for a provider. The names come from the EF model, not from user input, and are passed as
        /// parameters; they are never appended to the SQL text. The parameter order matches the order of appearance in the text
        /// (Oracle binds by position unless told otherwise).
        /// </summary>
        public static CatalogQuery BuildQuery(DBTypeEnum dbType, string schema, string table, string column)
        {
            var parameters = new List<KeyValuePair<string, string>>();
            string sql;
            switch (dbType)
            {
                case DBTypeEnum.SqlServer:
                    sql = "SELECT CHARACTER_MAXIMUM_LENGTH, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c AND TABLE_SCHEMA = "
                        + (schema == null ? "SCHEMA_NAME()" : "@s");
                    parameters.Add(new KeyValuePair<string, string>("t", table));
                    parameters.Add(new KeyValuePair<string, string>("c", column));
                    if (schema != null)
                    {
                        parameters.Add(new KeyValuePair<string, string>("s", schema));
                    }
                    break;
                case DBTypeEnum.MySql:
                    //the MySQL provider is configured to drop the EF schema, so the database selected on the connection is the schema
                    sql = "SELECT CHARACTER_MAXIMUM_LENGTH, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND LOWER(TABLE_NAME) = LOWER(@t) AND LOWER(COLUMN_NAME) = LOWER(@c)";
                    parameters.Add(new KeyValuePair<string, string>("t", table));
                    parameters.Add(new KeyValuePair<string, string>("c", column));
                    break;
                case DBTypeEnum.PgSql:
                    sql = "SELECT character_maximum_length, data_type FROM information_schema.columns WHERE table_name = @t AND column_name = @c AND table_schema = "
                        + (schema == null ? "current_schema()" : "@s");
                    parameters.Add(new KeyValuePair<string, string>("t", table));
                    parameters.Add(new KeyValuePair<string, string>("c", column));
                    if (schema != null)
                    {
                        parameters.Add(new KeyValuePair<string, string>("s", schema));
                    }
                    break;
                case DBTypeEnum.Oracle:
                case DBTypeEnum.DaMeng:
                    //best effort: written from the documented catalog views, not tested against a real server
                    sql = "SELECT CHAR_LENGTH, DATA_LENGTH, DATA_TYPE FROM ALL_TAB_COLUMNS WHERE "
                        + (schema == null ? "OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')" : "UPPER(OWNER) = UPPER(:o)")
                        + " AND UPPER(TABLE_NAME) = UPPER(:t) AND UPPER(COLUMN_NAME) = UPPER(:c)";
                    if (schema != null)
                    {
                        parameters.Add(new KeyValuePair<string, string>("o", schema));
                    }
                    parameters.Add(new KeyValuePair<string, string>("t", table));
                    parameters.Add(new KeyValuePair<string, string>("c", column));
                    break;
                default:
                    throw new NotSupportedException("No catalog query for " + dbType);
            }
            return new CatalogQuery { Sql = sql, Parameters = parameters };
        }

        /// <summary>
        /// Runs a catalog query with string parameters on an open connection and returns all rows as raw values.
        /// </summary>
        protected static List<object[]> ExecuteRows(DbConnection connection, string sql, IList<KeyValuePair<string, string>> parameters, bool bindByName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 15;
                if (bindByName)
                {
                    //ODP.NET style providers bind by position unless BindByName is set
                    cmd.GetType().GetProperty("BindByName")?.SetValue(cmd, true);
                }
                foreach (var p in parameters)
                {
                    var parameter = cmd.CreateParameter();
                    parameter.ParameterName = p.Key;
                    parameter.DbType = DbType.String;
                    parameter.Value = p.Value;
                    cmd.Parameters.Add(parameter);
                }
                var rows = new List<object[]>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        rows.Add(values);
                    }
                }
                return rows;
            }
        }

        private static ColumnRow ToRow(DBTypeEnum dbType, object[] raw)
        {
            if (dbType == DBTypeEnum.Oracle || dbType == DBTypeEnum.DaMeng)
            {
                return new ColumnRow { CharLength = ToLong(raw[0]), ByteLength = ToLong(raw[1]), DataType = ToText(raw[2]) };
            }
            return new ColumnRow { CharLength = ToLong(raw[0]), DataType = ToText(raw[1]) };
        }

        private static long? ToLong(object value)
        {
            if (value == null || value is DBNull)
            {
                return null;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static string ToText(object value)
        {
            if (value == null || value is DBNull)
            {
                return null;
            }
            return value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Turns catalog rows into an answer. Every row that matches (the same table name in several schemas, or names that differ
        /// only in case) has to be roomy enough: the smallest limit wins, and one row that cannot be understood makes the whole
        /// answer "cannot verify". No row at all (table or column not found) is also "cannot verify".
        /// </summary>
        public static PasswordColumnInfo Interpret(DBTypeEnum dbType, IReadOnlyList<ColumnRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return PasswordColumnInfo.Unverified("the password column was not found in the database catalog");
            }
            long? smallest = null;
            foreach (var row in rows)
            {
                var type = (row.DataType ?? string.Empty).Trim().ToLowerInvariant();
                long? limit;
                switch (dbType)
                {
                    case DBTypeEnum.SqlServer:
                    case DBTypeEnum.MySql:
                        if (row.CharLength == null || Array.IndexOf(SqlServerAndMySqlCharacterTypes, type) < 0)
                        {
                            return PasswordColumnInfo.Unverified("the password column is not a character column (" + type + ")");
                        }
                        limit = row.CharLength.Value < 0 ? (long?)null : row.CharLength.Value;
                        break;
                    case DBTypeEnum.PgSql:
                        if (Array.IndexOf(PgSqlCharacterTypes, type) < 0)
                        {
                            return PasswordColumnInfo.Unverified("the password column is not a character column (" + type + ")");
                        }
                        //a null length is unlimited text
                        limit = row.CharLength;
                        break;
                    case DBTypeEnum.Oracle:
                    case DBTypeEnum.DaMeng:
                        if (type == "clob" || type == "nclob" || type == "long" || type == "text")
                        {
                            limit = null;
                            break;
                        }
                        if (type.Contains("char") == false)
                        {
                            return PasswordColumnInfo.Unverified("the password column is not a character column (" + type + ")");
                        }
                        //characters or bytes, whichever is smaller: never more optimistic than the real limit
                        var candidates = new[] { row.CharLength, row.ByteLength }.Where(x => x.HasValue && x.Value > 0).Select(x => x.Value).ToList();
                        if (candidates.Count == 0)
                        {
                            return PasswordColumnInfo.Unverified("the catalog reports no length for the password column (" + type + ")");
                        }
                        limit = candidates.Min();
                        break;
                    default:
                        return PasswordColumnInfo.Unverified("unsupported database type " + dbType);
                }
                if (limit.HasValue)
                {
                    smallest = smallest.HasValue ? Math.Min(smallest.Value, limit.Value) : limit;
                }
            }
            if (smallest.HasValue == false)
            {
                return PasswordColumnInfo.Unlimited;
            }
            return PasswordColumnInfo.Limited((int)Math.Min(smallest.Value, int.MaxValue));
        }
    }
}
