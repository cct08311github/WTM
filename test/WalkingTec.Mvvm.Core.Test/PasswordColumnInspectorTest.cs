using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using static WalkingTec.Mvvm.Core.PasswordColumnInspector;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Same user model as <see cref="LoginTestContext"/>, but for a provider that does not need to be reachable: only the model is read.
    /// </summary>
    public class ProviderModelContext : FrameworkContext
    {
        public ProviderModelContext(string cs, DBTypeEnum dbtype, string version = null) : base(cs, dbtype, version)
        {
        }

        public DbSet<LoginTestUser> LoginTestUsers { get; set; }
    }

    [TestClass]
    public class PasswordColumnInspectorTest
    {
        /// <summary>An inspector whose catalog rows (or failure) are scripted, so no server is needed.</summary>
        private class ScriptedInspector : PasswordColumnInspector
        {
            public ScriptedInspector(TimeSpan? retryAfterFailure = null) : base(retryAfterFailure)
            {
            }

            public int Reads { get; private set; }
            public List<(DBTypeEnum Type, string Schema, string Table, string Column)> Requests { get; } = new List<(DBTypeEnum, string, string, string)>();
            public Func<IReadOnlyList<ColumnRow>> Rows { get; set; } = () => new List<ColumnRow>();

            protected override IReadOnlyList<ColumnRow> ReadColumnRows(IDataContext dc, DBTypeEnum dbType, string schema, string table, string column)
            {
                Reads++;
                Requests.Add((dbType, schema, table, column));
                return Rows();
            }

            public static List<object[]> Run(System.Data.Common.DbConnection connection, string sql, IList<KeyValuePair<string, string>> parameters)
            {
                return ExecuteRows(connection, sql, parameters, false);
            }
        }

        private static ColumnRow Row(string type, long? chars, long? bytes = null) => new ColumnRow { DataType = type, CharLength = chars, ByteLength = bytes };

        private static ProviderModelContext Context(DBTypeEnum type, string cs = "Server=never;Database=x", string version = null) => new ProviderModelContext(cs, type, version);

        #region Interpret

        [TestMethod]
        public void SqlServerCatalogRows()
        {
            Assert.AreEqual(32, Info(DBTypeEnum.SqlServer, Row("nvarchar", 32)).MaxLength);
            Assert.AreEqual(128, Info(DBTypeEnum.SqlServer, Row("nvarchar", 128)).MaxLength);
            var max = Info(DBTypeEnum.SqlServer, Row("nvarchar", -1));
            Assert.IsTrue(max.IsVerified);
            Assert.IsNull(max.MaxLength, "-1 is MAX");
            Assert.IsFalse(Info(DBTypeEnum.SqlServer, Row("int", null)).IsVerified, "not a character column");
        }

        [TestMethod]
        public void MySqlCatalogRows()
        {
            Assert.AreEqual(32, Info(DBTypeEnum.MySql, Row("varchar", 32)).MaxLength);
            Assert.AreEqual(65535, Info(DBTypeEnum.MySql, Row("text", 65535)).MaxLength);
            Assert.AreEqual(int.MaxValue, Info(DBTypeEnum.MySql, Row("longtext", 4294967295)).MaxLength, "clamped, not overflowed");
            Assert.IsFalse(Info(DBTypeEnum.MySql, Row("int", null)).IsVerified);
        }

        [TestMethod]
        public void PostgreSqlCatalogRows()
        {
            Assert.AreEqual(32, Info(DBTypeEnum.PgSql, Row("character varying", 32)).MaxLength);
            var text = Info(DBTypeEnum.PgSql, Row("text", null));
            Assert.IsTrue(text.IsVerified);
            Assert.IsNull(text.MaxLength, "text has no limit");
            var unlimited = Info(DBTypeEnum.PgSql, Row("character varying", null));
            Assert.IsTrue(unlimited.IsVerified);
            Assert.IsNull(unlimited.MaxLength);
            Assert.IsFalse(Info(DBTypeEnum.PgSql, Row("integer", null)).IsVerified, "a null length alone does not mean unlimited");
        }

        [TestMethod]
        public void OracleAndDaMengCatalogRowsUseTheSmallerOfCharactersAndBytes()
        {
            foreach (var type in new[] { DBTypeEnum.Oracle, DBTypeEnum.DaMeng })
            {
                Assert.AreEqual(32, Info(type, Row("NVARCHAR2", 32, 64)).MaxLength, "national character set: 2 bytes per character");
                Assert.AreEqual(32, Info(type, Row("VARCHAR2", 32, 128)).MaxLength, "char semantics: up to 4 bytes per character");
                Assert.AreEqual(32, Info(type, Row("VARCHAR2", 32, 32)).MaxLength);
                Assert.AreEqual(20, Info(type, Row("VARCHAR2", 32, 20)).MaxLength, "never more optimistic than the byte limit");
                Assert.AreEqual(128, Info(type, Row("NVARCHAR2", 128, 256)).MaxLength);
                Assert.IsNull(Info(type, Row("CLOB", 0, 4000)).MaxLength);
                Assert.IsTrue(Info(type, Row("NCLOB", 0, 4000)).IsVerified);
                Assert.IsFalse(Info(type, Row("NUMBER", 0, 22)).IsVerified, "no usable length");
            }
        }

        [TestMethod]
        public void NoRowsOrAnyUnclearRowMeansCannotVerify()
        {
            foreach (DBTypeEnum type in new[] { DBTypeEnum.SqlServer, DBTypeEnum.MySql, DBTypeEnum.PgSql, DBTypeEnum.Oracle, DBTypeEnum.DaMeng })
            {
                Assert.IsFalse(PasswordColumnInspector.Interpret(type, new List<ColumnRow>()).IsVerified, type.ToString());
                Assert.IsFalse(PasswordColumnInspector.Interpret(type, null).IsVerified, type.ToString());
            }
            //the same table name in two schemas: both must be roomy enough
            var two = PasswordColumnInspector.Interpret(DBTypeEnum.SqlServer, new List<ColumnRow> { Row("nvarchar", 128), Row("nvarchar", 32) });
            Assert.AreEqual(32, two.MaxLength);
            var unclear = PasswordColumnInspector.Interpret(DBTypeEnum.SqlServer, new List<ColumnRow> { Row("nvarchar", 128), Row("int", null) });
            Assert.IsFalse(unclear.IsVerified);
            Assert.IsFalse(PasswordColumnInspector.Interpret(DBTypeEnum.Memory, new List<ColumnRow> { Row("x", 1) }).IsVerified);
        }

        private static PasswordColumnInfo Info(DBTypeEnum type, ColumnRow row) => PasswordColumnInspector.Interpret(type, new List<ColumnRow> { row });

        #endregion

        #region Queries

        [TestMethod]
        public void QueriesAreParameterizedAndNeverContainTheNames()
        {
            const string table = "Fra'meworkUsers";
            const string column = "Pass\"word";
            const string schema = "my;schema";
            foreach (DBTypeEnum type in new[] { DBTypeEnum.SqlServer, DBTypeEnum.MySql, DBTypeEnum.PgSql, DBTypeEnum.Oracle, DBTypeEnum.DaMeng })
            {
                foreach (var s in new[] { null, schema })
                {
                    var q = PasswordColumnInspector.BuildQuery(type, s, table, column);
                    Assert.IsFalse(q.Sql.Contains("Fra'meworkUsers") || q.Sql.Contains("Pass\"word") || q.Sql.Contains("my;schema"), type + ": " + q.Sql);
                    Assert.IsTrue(q.Parameters.Any(x => x.Value == table) && q.Parameters.Any(x => x.Value == column), type.ToString());
                    foreach (var p in q.Parameters)
                    {
                        Assert.IsTrue(q.Sql.Contains("@" + p.Key) || q.Sql.Contains(":" + p.Key), type + " uses " + p.Key);
                    }
                }
            }
        }

        [TestMethod]
        public void SqlServerQueryUsesInformationSchemaWithMappedNames()
        {
            var q = PasswordColumnInspector.BuildQuery(DBTypeEnum.SqlServer, null, "FrameworkUsers", "Password");
            Assert.AreEqual("SELECT CHARACTER_MAXIMUM_LENGTH, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c AND TABLE_SCHEMA = SCHEMA_NAME()", q.Sql);
            CollectionAssert.AreEqual(new[] { "t", "c" }, q.Parameters.Select(x => x.Key).ToArray());
            var withSchema = PasswordColumnInspector.BuildQuery(DBTypeEnum.SqlServer, "sec", "FrameworkUsers", "Password");
            StringAssert.EndsWith(withSchema.Sql, "TABLE_SCHEMA = @s");
            Assert.AreEqual("sec", withSchema.Parameters.Single(x => x.Key == "s").Value);
        }

        [TestMethod]
        public void OracleParametersFollowTheOrderOfAppearance()
        {
            foreach (var type in new[] { DBTypeEnum.Oracle, DBTypeEnum.DaMeng })
            {
                var q = PasswordColumnInspector.BuildQuery(type, "APP", "FrameworkUsers", "Password");
                CollectionAssert.AreEqual(new[] { "o", "t", "c" }, q.Parameters.Select(x => x.Key).ToArray());
                Assert.IsTrue(q.Sql.IndexOf(":o") < q.Sql.IndexOf(":t") && q.Sql.IndexOf(":t") < q.Sql.IndexOf(":c"));
                q = PasswordColumnInspector.BuildQuery(type, null, "FrameworkUsers", "Password");
                CollectionAssert.AreEqual(new[] { "t", "c" }, q.Parameters.Select(x => x.Key).ToArray());
                StringAssert.Contains(q.Sql, "ALL_TAB_COLUMNS");
            }
        }

        [TestMethod]
        public void QueryParametersReachTheDatabase()
        {
            using (var connection = new SqliteConnection("Data Source=:memory:"))
            {
                connection.Open();
                var parameters = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("t", "FrameworkUsers"),
                    new KeyValuePair<string, string>("c", "Password"),
                };
                var rows = ScriptedInspector.Run(connection, "SELECT 128, 'text', @c WHERE @t = 'FrameworkUsers'", parameters);
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(128L, Convert.ToInt64(rows[0][0]));
                Assert.AreEqual("Password", rows[0][2]);
                Assert.AreEqual(0, ScriptedInspector.Run(connection, "SELECT 128 WHERE @t = 'other'", parameters.Take(1).ToList()).Count);
            }
        }

        #endregion

        #region Inspect

        [TestMethod]
        public void SqliteAndInMemoryDoNotEnforceLengthsAndAreNotQueried()
        {
            var inspector = new ScriptedInspector();
            foreach (var type in new[] { DBTypeEnum.SQLite, DBTypeEnum.Memory })
            {
                using (var dc = Context(type, type == DBTypeEnum.SQLite ? "Data Source=:memory:" : Guid.NewGuid().ToString("N")))
                {
                    var info = inspector.Inspect(dc, typeof(LoginTestUser));
                    Assert.IsTrue(info.IsVerified, type.ToString());
                    Assert.IsNull(info.MaxLength);
                }
            }
            Assert.AreEqual(0, inspector.Reads);
        }

        [TestMethod]
        public void DefaultInspectorAllowsSqliteEndToEnd()
        {
            using (var dc = Context(DBTypeEnum.SQLite, "Data Source=:memory:"))
            {
                Assert.IsTrue(PasswordColumnInspector.Default.Inspect(dc, typeof(LoginTestUser)).IsVerified);
            }
        }

        [TestMethod]
        public void UsesTheMappedNamesOfTheUserTable()
        {
            var inspector = new ScriptedInspector { Rows = () => new List<ColumnRow> { Row("nvarchar", 32) } };
            using (var dc = Context(DBTypeEnum.SqlServer))
            {
                var info = inspector.Inspect(dc, typeof(LoginTestUser));
                Assert.AreEqual(32, info.MaxLength, "a database created by an earlier version");
            }
            var request = inspector.Requests.Single();
            Assert.AreEqual(DBTypeEnum.SqlServer, request.Type);
            Assert.AreEqual("FrameworkUsers", request.Table);
            Assert.AreEqual("Password", request.Column);
            Assert.IsNull(request.Schema);

            //DaMeng is left out: its EF provider package targets EF Core 9 and cannot build a model under EF Core 10 here (the inspector reports that as "cannot verify", see DaMengModelFailureIsNotVerified)
            foreach (var type in new[] { DBTypeEnum.PgSql, DBTypeEnum.Oracle })
            {
                inspector = new ScriptedInspector { Rows = () => new List<ColumnRow> { Row("character varying", 128, 128) } };
                using (var dc = Context(type))
                {
                    var info = inspector.Inspect(dc, typeof(LoginTestUser));
                    Assert.AreEqual(128, info.MaxLength, type + ": " + info.Description);
                }
                Assert.AreEqual("FrameworkUsers", inspector.Requests.Single().Table, type.ToString());
            }
        }

        [TestMethod]
        public void DefiniteAnswersAreCachedPerConnection()
        {
            var inspector = new ScriptedInspector { Rows = () => new List<ColumnRow> { Row("nvarchar", 128) } };
            using (var first = Context(DBTypeEnum.SqlServer, "Server=a;Database=x"))
            using (var again = Context(DBTypeEnum.SqlServer, "Server=a;Database=x"))
            using (var other = Context(DBTypeEnum.SqlServer, "Server=b;Database=x"))
            {
                Assert.AreEqual(128, inspector.Inspect(first, typeof(LoginTestUser)).MaxLength);
                Assert.AreEqual(128, inspector.Inspect(again, typeof(LoginTestUser)).MaxLength);
                Assert.AreEqual(1, inspector.Reads, "same connection: asked once");
                inspector.Inspect(other, typeof(LoginTestUser));
                Assert.AreEqual(2, inspector.Reads, "another database is another answer");
            }
        }

        [TestMethod]
        public void FailuresAreRememberedBrieflyAndThenRetried()
        {
            var fail = true;
            var inspector = new ScriptedInspector(TimeSpan.FromHours(1))
            {
                Rows = () => fail ? throw new InvalidOperationException("secret detail") : new List<ColumnRow> { Row("nvarchar", 128) }
            };
            using (var dc = Context(DBTypeEnum.SqlServer))
            {
                var first = inspector.Inspect(dc, typeof(LoginTestUser));
                Assert.IsFalse(first.IsVerified);
                StringAssert.Contains(first.Description, "InvalidOperationException");
                Assert.IsFalse(first.Description.Contains("secret detail"));
                fail = false;
                Assert.IsFalse(inspector.Inspect(dc, typeof(LoginTestUser)).IsVerified, "still remembered");
                Assert.AreEqual(1, inspector.Reads);
            }

            fail = true;
            var retrying = new ScriptedInspector(TimeSpan.Zero) { Rows = () => fail ? throw new InvalidOperationException() : new List<ColumnRow> { Row("nvarchar", 128) } };
            using (var dc = Context(DBTypeEnum.SqlServer))
            {
                Assert.IsFalse(retrying.Inspect(dc, typeof(LoginTestUser)).IsVerified);
                fail = false;
                Assert.AreEqual(128, retrying.Inspect(dc, typeof(LoginTestUser)).MaxLength, "asked again after the failure");
                Assert.AreEqual(2, retrying.Reads);
                retrying.Inspect(dc, typeof(LoginTestUser));
                Assert.AreEqual(2, retrying.Reads, "the definite answer sticks");
            }
        }

        [TestMethod]
        public void DaMengModelFailureIsNotVerified()
        {
            //whatever goes wrong while the model or catalog is read ends up as "cannot verify", never as an exception
            var inspector = new ScriptedInspector { Rows = () => new List<ColumnRow> { Row("VARCHAR", 128, 128) } };
            using (var dc = Context(DBTypeEnum.DaMeng))
            {
                var info = inspector.Inspect(dc, typeof(LoginTestUser));
                Assert.IsNotNull(info);
            }
        }

        [TestMethod]
        public void MissingColumnAndUnsupportedContextsAreNotVerified()
        {
            var inspector = new ScriptedInspector();
            using (var dc = Context(DBTypeEnum.SqlServer))
            {
                Assert.IsFalse(inspector.Inspect(dc, typeof(LoginTestUser)).IsVerified, "no catalog row: table or column not there");
                Assert.IsFalse(inspector.Inspect(dc, typeof(Student)).IsVerified, "a type that is not in the model");
            }
            Assert.IsFalse(inspector.Inspect(null, typeof(LoginTestUser)).IsVerified);
            Assert.IsFalse(inspector.Inspect(new NullContext(), typeof(LoginTestUser)).IsVerified, "never throws");
        }

        #endregion
    }
}
