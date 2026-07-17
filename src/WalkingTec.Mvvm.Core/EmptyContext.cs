using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Npgsql;
using MySql.EntityFrameworkCore.Extensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    public partial class EmptyContext : DbContext, IDataContext
    {
        private ILoggerFactory? _loggerFactory;

        /// <summary>
        /// Commited
        /// </summary>
        public bool Commited { get; set; }

        /// <summary>
        /// IsFake
        /// </summary>
        public bool IsFake { get; set; }
        private string? _tenantCode;
        public string? TenantCode
        {
            get
            {
                return _tenantCode;
            }
        }
        public bool IsDebug { get; set; }

        /// <summary>
        /// Controls whether EF Core query parameter values are included in log output.
        /// Default is <c>false</c> to protect PII/credentials in debug deployments.
        /// Set to <c>true</c> only in trusted local development environments.
        /// Requires <see cref="IsDebug"/> to also be <c>true</c> to take effect.
        /// </summary>
        public bool EnableSensitiveQueryLogging { get; set; } = false;

        public string? CurrentUserCode { get; set; }
        /// <summary>
        /// CSName
        /// </summary>
        public string CSName { get; set; } = "default";

        public DBTypeEnum DBType { get; set; }

        /// <summary>
        /// 可測試的時間來源。預設 TimeProvider.System。
        /// </summary>
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        public string? Version { get; set; }
        public CS ConnectionString { get; set; } = null!;
        public DbSet<AnalysisSavedQuery> AnalysisSavedQueries { get; set; } = null!;


        /// <summary>
        /// FrameworkContext
        /// </summary>
        public EmptyContext()
        {
            CSName = "default";
            DBType = DBTypeEnum.SqlServer;
        }

        /// <summary>
        /// FrameworkContext
        /// </summary>
        /// <param name="cs"></param>
        public EmptyContext(string cs)
        {
            CSName = cs;
            DBType = DBTypeEnum.SqlServer;
        }

        public EmptyContext(string cs, DBTypeEnum dbtype, string? version = null)
        {
            CSName = cs;
            DBType = dbtype;
            Version = version;
        }

        public EmptyContext(CS cs)
        {
            CSName = cs.Value ?? "default";
            DBType = cs.DbType ?? DBTypeEnum.SqlServer;
            Version = cs.Version;
            ConnectionString = cs;
        }

        public EmptyContext(DbContextOptions options) : base(options) { }

        public IDataContext CreateNew()
        {
            if (ConnectionString != null)
            {
                return (IDataContext)this.GetType().GetConstructor(new Type[] { typeof(CS) })!.Invoke(new object[] { ConnectionString });
            }
            else
            {
                return (IDataContext)this.GetType().GetConstructor(new Type[] { typeof(string), typeof(DBTypeEnum), typeof(string) })!.Invoke(new object[] { CSName, DBType, Version! });
            }
        }

        public IDataContext ReCreate(ILoggerFactory? _logger=null)
        {
            if (this?.Database?.CurrentTransaction != null)
            {
                return this;
            }
            else
            {
                IDataContext rv = null!;
                if (ConnectionString != null)
                {
                    rv = (IDataContext)this.GetType().GetConstructor(new Type[] { typeof(CS) })!.Invoke(new object[] { ConnectionString });
                }
                else
                {
                    // Prefer the 3-arg constructor so the Version (DB compatibility level) is
                    // preserved.  Fall back to the 2-arg constructor only when the subtype does
                    // not expose the 3-arg form (e.g. an old custom subclass).
                    var ctor3 = this.GetType().GetConstructor(new Type[] { typeof(string), typeof(DBTypeEnum), typeof(string) });
                    if (ctor3 != null)
                    {
                        rv = (IDataContext)ctor3.Invoke(new object?[] { CSName, DBType, Version });
                    }
                    else
                    {
                        rv = (IDataContext)this.GetType().GetConstructor(new Type[] { typeof(string), typeof(DBTypeEnum) })!.Invoke(new object[] { CSName, DBType });
                    }
                }
                rv.SetTenantCode(this.TenantCode);
                if (_logger != null)
                {
                    rv.IsDebug = true;
                    rv.SetLoggerFactory(_logger);
                }
                return rv;
            }
        }
        /// <summary>
        /// 将一个实体设为填加状态
        /// </summary>
        /// <param name="entity">实体</param>
        public void AddEntity<T>(T entity) where T : TopBasePoco
        {
            this.Entry(entity).State = EntityState.Added;
        }

        /// <summary>
        /// 将一个实体设为修改状态
        /// </summary>
        /// <param name="entity">实体</param>
        public void UpdateEntity<T>(T entity) where T : TopBasePoco
        {
            this.Entry(entity).State = EntityState.Modified;
        }

        /// <summary>
        /// 将一个实体的某个字段设为修改状态，用于只更新个别字段的情况
        /// </summary>
        /// <typeparam name="T">实体类</typeparam>
        /// <param name="entity">实体</param>
        /// <param name="fieldExp">要设定为修改状态的字段</param>
        public void UpdateProperty<T>(T entity, Expression<Func<T, object>> fieldExp)
            where T : TopBasePoco
        {
            var set = this.Set<T>();
            if (set.Local.AsQueryable().CheckID(entity.GetID()).FirstOrDefault() == null)
            {
                set.Attach(entity);
            }
            this.Entry(entity).Property(fieldExp).IsModified = true;
        }

        /// <summary>
        /// UpdateProperty
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity"></param>
        /// <param name="fieldName"></param>
        public void UpdateProperty<T>(T entity, string fieldName)
            where T : TopBasePoco
        {
            var set = this.Set<T>();
            if (set.Local.AsQueryable().CheckID(entity.GetID()).FirstOrDefault() == null)
            {
                set.Attach(entity);
            }
            this.Entry(entity).Property(fieldName).IsModified = true;
        }

        /// <summary>
        /// 将一个实体设定为删除状态
        /// </summary>
        /// <param name="entity">实体</param>
        public void DeleteEntity<T>(T entity) where T : TopBasePoco
        {
            var set = this.Set<T>();
            var exist = set.Local.AsQueryable().CheckID(entity.GetID()).FirstOrDefault();
            if (exist == null)
            {
                set.Attach(entity);
                set.Remove(entity);
            }
            else
            {
                set.Remove(exist);

            }
        }

        /// <summary>
        /// CascadeDelete
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity"></param>
        public void CascadeDelete<T>(T entity) where T : TreePoco
        {
            CascadeDelete(entity, new HashSet<Guid>());
        }

        // Internal overload with a visited set to guard against cyclic parent-child references
        // and extremely deep trees (both would cause an unbounded StackOverflowException in the
        // original public method).
        private void CascadeDelete<T>(T entity, HashSet<Guid> visited) where T : TreePoco
        {
            if (entity == null || entity.ID == Guid.Empty)
            {
                return;
            }
            // If we have already processed this node (cycle or duplicate), skip it.
            if (!visited.Add(entity.ID))
            {
                return;
            }
            var set = this.Set<T>();
            List<T> entities = [.. set.Where(x => x.ParentId == entity.ID)];
            foreach (var item in entities)
            {
                CascadeDelete(item, visited);
            }
            DeleteEntity(entity);
        }

        /// <summary>
        /// GetCoreType
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public Type GetCoreType(Type t)
        {
            if (t != null && t.IsNullable())
            {
                if (!t.GetTypeInfo().IsValueType)
                {
                    return t;
                }
                else
                {
                    if ("DateTime".Equals(t.GenericTypeArguments[0].Name))
                    {
                        return typeof(string);
                    }
                    return Nullable.GetUnderlyingType(t)!;
                }
            }
            else
            {
                if (t != null && "DateTime".Equals(t.Name))
                {
                    return typeof(string);
                }
                return t!;
            }
        }

        /// <summary>
        /// OnModelCreating
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (DBType == DBTypeEnum.Oracle)
            {
                // #525: EF Core 10 removed the IConventionModelBuilder cast path from ModelBuilder,
                // so the old ((IConventionModelBuilder)modelBuilder).HasMaxIdentifierLength(30) throws
                // InvalidCastException at runtime.  Use the IMutableModel extension instead — it works
                // on both EF Core 8 and EF Core 10.
                modelBuilder.Model.SetMaxIdentifierLength(30);
            }
        }

        /// <summary>
        /// OnConfiguring
        /// </summary>
        /// <param name="optionsBuilder"></param>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            switch (DBType)
            {
                case DBTypeEnum.SqlServer:
                    var ver = 120;
                    if (string.IsNullOrEmpty(Version)==false)
                    {
                        int.TryParse(Version, out ver);
                    }
                    optionsBuilder.UseSqlServer(CSName,o => o.UseCompatibilityLevel(ver));
                    break;
                case DBTypeEnum.MySql:
                    optionsBuilder.UseMySQL(CSName);
                    break;
                case DBTypeEnum.PgSql:
                    optionsBuilder.UseNpgsql(CSName);
                    break;
                case DBTypeEnum.Memory:
                    optionsBuilder.UseInMemoryDatabase(CSName);
                    break;
                case DBTypeEnum.SQLite:
                    optionsBuilder.UseSqlite(CSName);
                    break;
                //case DBTypeEnum.DaMeng:
                //    optionsBuilder.UseDm(CSName);
                //    break;
                case DBTypeEnum.Oracle:
                    optionsBuilder.UseOracle(CSName, option =>
                    {
                        switch (Version)
                        {
                            case "19":
                                option.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19);
                                break;
                            case "21":
                                option.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion21);
                                break;
                            case "23":
                                option.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion23);
                                break;
                            default:
                                option.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19);
                                break;
                        }
                    });
                    break;
                default:
                    break;
            }
            if (IsDebug == true)
            {
                optionsBuilder.EnableDetailedErrors();
                // EnableSensitiveDataLogging logs query parameter values which may contain
                // PII or credentials. It is gated behind a separate explicit opt-in so that
                // debug deployments do not leak sensitive data unintentionally. (M15)
                if (EnableSensitiveQueryLogging)
                {
                    optionsBuilder.EnableSensitiveDataLogging();
                }
                if (_loggerFactory != null)
                {
                    optionsBuilder.UseLoggerFactory(_loggerFactory);
                }
            }
            base.OnConfiguring(optionsBuilder);
        }

        public void SetLoggerFactory(ILoggerFactory factory)
        {
            this._loggerFactory = factory;
        }

        public void SetTenantCode(string? code)
        {
            this._tenantCode = code;
        }
        /// <summary>
        /// 数据初始化
        /// </summary>
        /// <param name="allModules"></param>
        /// <param name="IsSpa"></param>
        /// <returns>返回true表示需要进行初始化数据操作，返回false即数据库已经存在或不需要初始化数据</returns>
        public async virtual Task<bool> DataInit(object? allModules, bool IsSpa)
        {
            bool rv = await Database.EnsureCreatedAsync();
            return rv;
        }

        #region 执行存储过程返回datatable
        /// <summary>
        /// 执行存储过程，返回datatable结果集
        /// </summary>
        /// <param name="command">存储过程名称</param>
        /// <param name="paras">存储过程参数</param>
        /// <returns></returns>
        public DataTable RunSP(string command, params object[] paras)
        {
            return Run(command, CommandType.StoredProcedure, paras);
        }
        #endregion

        public IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras)
        {
            return Run<TElement>(command, CommandType.StoredProcedure, paras);
        }

        #region 执行Sql语句，返回datatable
        public DataTable RunSQL(string sql, params object[] paras)
        {
            return Run(sql, CommandType.Text, paras);
        }
        #endregion

        public IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras)
        {
            return Run<TElement>(sql, CommandType.Text, paras);
        }


        #region 执行存储过程或Sql语句返回DataTable
        /// <summary>
        /// 执行存储过程或Sql语句返回DataTable
        /// </summary>
        /// <param name="sql">存储过程名称或Sql语句</param>
        /// <param name="commandType">命令类型</param>
        /// <param name="paras">参数</param>
        /// <returns></returns>
        public DataTable Run(string sql, CommandType commandType, params object[] paras)
        {
            DataTable table = new DataTable();
            var connection = this.Database.GetDbConnection();
            var isClosed = connection.State == ConnectionState.Closed;
            if (isClosed)
            {
                connection.Open();
            }
            // M16: try/finally ensures the connection is closed even if ExecuteReader or
            // DataTable.Load throws, preventing a connection leak on error paths.
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandTimeout = 2400;
                    command.CommandType = commandType;
                    if (this.Database.CurrentTransaction != null)
                    {
                        command.Transaction = this.Database.CurrentTransaction.GetDbTransaction();
                    }
                    if (paras != null)
                    {
                        foreach (var param in paras)
                        {
                            // #147: build each parameter via the command's own factory so all
                            // providers (including Oracle) get a correctly-typed DbParameter.
                            // The caller passes any DbParameter (e.g. built via
                            // CreateCommandParameter for non-Oracle, or a bare SqlParameter)
                            // and Run() re-creates it from the executing command's factory,
                            // which returns the right type (OracleParameter, SqlParameter, etc.)
                            // without requiring a compile-time reference to any provider assembly.
                            var dp = (DbParameter)param;
                            var p = command.CreateParameter();
                            p.ParameterName = dp.ParameterName;
                            p.Value = dp.Value ?? DBNull.Value;
                            p.Direction = dp.Direction;
                            command.Parameters.Add(p);
                        }
                    }
                    using (var reader = command.ExecuteReader())
                    {
                        table.Load(reader);
                    }
                }
            }
            finally
            {
                if (isClosed)
                {
                    connection.Close();
                }
            }
            return table;
        }
        #endregion


        public IEnumerable<TElement> Run<TElement>(string sql, CommandType commandType, params object[] paras)
        {
            IEnumerable<TElement> entityList = [];
            DataTable dt = Run(sql, commandType, paras);
            entityList = EntityHelper.GetEntityList<TElement>(dt);
            return entityList;
        }


        /// <remarks>
        /// As of #147, <see cref="Run(string,CommandType,object[])"/> builds parameters via
        /// <c>DbCommand.CreateParameter()</c> instead of this method, so Oracle works without a
        /// provider-assembly reference. This helper is kept for external callers; its Oracle
        /// <see cref="NotSupportedException"/> is intentional and must remain.
        /// </remarks>
        public object CreateCommandParameter(string name, object value, ParameterDirection dir)
        {
            switch (this.DBType)
            {
                case DBTypeEnum.SqlServer:
                    return new SqlParameter(name, value) { Direction = dir };
                case DBTypeEnum.MySql:
                    return new MySqlParameter(name, value) { Direction = dir };
                case DBTypeEnum.PgSql:
                    return new NpgsqlParameter(name, value) { Direction = dir };
                case DBTypeEnum.SQLite:
                    return new SqliteParameter(name, value) { Direction = dir };
                case DBTypeEnum.Oracle:
                    // M14: Oracle provider (Oracle.EntityFrameworkCore) is loaded at runtime.
                    // Rather than a commented-out OracleParameter (which silently returned null
                    // and caused an NRE in Run()), throw a clear NotSupportedException so the
                    // caller gets actionable feedback. Use Run() which calls command.CreateParameter()
                    // internally (provider-agnostic) for parameterized Oracle raw SQL.
                    throw new NotSupportedException(
                        "CreateCommandParameter does not support Oracle. " +
                        "Pass any DbParameter to Run() — it rebuilds parameters via DbCommand.CreateParameter() internally.");
                default:
                    throw new NotSupportedException(
                        $"CreateCommandParameter does not support DBType '{this.DBType}'.");
            }
        }

        private void ApplyAuditFields()
        {
            if (ChangeTracker == null) return;

            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.Entity is IBasePoco entity)
                {
                    switch (entry.State)
                    {
                        case EntityState.Added:
                            if (entity.CreateTime == null)
                                entity.CreateTime = TimeProvider.GetLocalNow().DateTime;
                            if (string.IsNullOrEmpty(entity.CreateBy))
                                entity.CreateBy = CurrentUserCode;
                            break;

                        case EntityState.Modified:
                            if (entity.UpdateTime == null)
                                entity.UpdateTime = TimeProvider.GetLocalNow().DateTime;
                            if (string.IsNullOrEmpty(entity.UpdateBy))
                                entity.UpdateBy = CurrentUserCode;
                            break;
                    }
                }
            }
        }

        public override int SaveChanges()
        {
            ApplyAuditFields();
            return base.SaveChanges();
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplyAuditFields();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplyAuditFields();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            ApplyAuditFields();
            return base.SaveChangesAsync(cancellationToken);
        }

        public void EnsureCreate()
        {
            this.Database.EnsureCreated();
        }
    }
}
