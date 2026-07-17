using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core
{
    public class NullContext : IDataContext
    {


        public string? TenantCode { get; } = null;
        public bool IsFake { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public IModel Model => throw new NotImplementedException();

        public DatabaseFacade Database => throw new NotImplementedException();

        public string CSName { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public DBTypeEnum DBType { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool IsDebug { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool EnableSensitiveQueryLogging { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public string? CurrentUserCode { get; set; }

        public void AddEntity<T>(T entity) where T : TopBasePoco
        {
            throw new NotImplementedException();
        }

        public void CascadeDelete<T>(T entity) where T : TreePoco
        {
            throw new NotImplementedException();
        }

        public object CreateCommandParameter(string name, object value, ParameterDirection dir)
        {
            throw new NotImplementedException();
        }

        public IDataContext CreateNew()
        {
            throw new NotImplementedException();
        }

        public Task<bool> DataInit(object? AllModel, bool IsSpa)
        {
            throw new NotImplementedException();
        }

        public void DeleteEntity<T>(T entity) where T : TopBasePoco
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {

        }

        public IDataContext ReCreate(ILoggerFactory? _logger = null)
        {
            throw new NotImplementedException();
        }

        public DataTable Run(string sql, CommandType commandType, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<TElement> Run<TElement>(string sql, CommandType commandType, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public DataTable RunSP(string command, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public DataTable RunSQL(string command, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras)
        {
            throw new NotImplementedException();
        }

        public int SaveChanges()
        {
            throw new NotImplementedException();
        }

        public int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            throw new NotImplementedException();
        }

        public Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public DbSet<T> Set<T>() where T : class
        {
            throw new NotImplementedException();
        }
        public void SetTenantCode(string? code)
        {
            throw new NotImplementedException();
        }

        public void SetLoggerFactory(ILoggerFactory factory)
        {
            throw new NotImplementedException();
        }

        public void UpdateEntity<T>(T entity) where T : TopBasePoco
        {
            throw new NotImplementedException();
        }

        public void UpdateProperty<T>(T entity, Expression<Func<T, object>> fieldExp) where T : TopBasePoco
        {
            throw new NotImplementedException();
        }

        public void UpdateProperty<T>(T entity, string fieldName) where T : TopBasePoco
        {
            throw new NotImplementedException();
        }

        public void EnsureCreate()
        {
            throw new NotImplementedException();

        }
    }
}
