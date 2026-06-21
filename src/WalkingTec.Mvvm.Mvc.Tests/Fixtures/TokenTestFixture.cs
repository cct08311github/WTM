using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc.Auth;

namespace WalkingTec.Mvvm.Mvc.Tests.Fixtures
{
    /// <summary>
    /// Provides an isolated TokenService instance backed by SQLite shared in-memory for integration tests.
    /// Each test class should create a new fixture instance (don't share across tests).
    ///
    /// SQLite shared in-memory is used instead of EF InMemory because TokenService.RefreshTokenAsync
    /// uses ExecuteUpdateAsync, which is not supported by the EF InMemory provider.
    ///
    /// A keep-alive SqliteConnection is held open for the fixture lifetime so the shared in-memory
    /// database is not dropped between DI scope instantiations (shared in-memory SQLite is dropped
    /// when the last connection closes).
    ///
    /// TokenService uses IServiceProvider.CreateScope() to resolve IDataContext.
    /// We satisfy this by registering a scoped FrameworkContext in the DI container.
    /// </summary>
    /// <remarks>
    /// Resolves GitHub/Gitea issue #472: the original InMemory provider threw
    /// <see cref="System.InvalidOperationException"/> on ExecuteUpdateAsync calls used
    /// by token revocation. SQLite in-memory is a full relational provider and supports
    /// all EF Core bulk-update APIs.
    /// </remarks>
    public class TokenTestFixture : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly SqliteConnection _keepAlive;
        private bool _disposed;

        public ITokenService TokenService { get; }

        /// <summary>The SQLite shared in-memory connection string — unique per fixture to isolate tests.</summary>
        public string DbConnectionString { get; }

        public TokenTestFixture()
        {
            var dbName = $"TokenTest_{Guid.NewGuid():N}";
            DbConnectionString = $"DataSource={dbName}?mode=memory&cache=shared";

            // Open a keep-alive connection so the shared in-memory DB persists for the fixture lifetime.
            _keepAlive = new SqliteConnection(DbConnectionString);
            _keepAlive.Open();

            // Create schema once before DI scopes start resolving contexts.
            using (var initCtx = new FrameworkContext(DbConnectionString, DBTypeEnum.SQLite))
            {
                initCtx.Database.EnsureCreated();
            }

            var services = new ServiceCollection();

            // Use FrameworkContext (not EmptyContext) so RefreshTokenEntity is in the EF model.
            // Use the (string, DBTypeEnum) constructor so OnConfiguring picks the SQLite provider.
            // Avoid AddDbContext() — it would pass DbContextOptions that combines with
            // OnConfiguring's SqlServer default, causing "two providers" InvalidOperationException.
            services.AddScoped<FrameworkContext>(_ => new FrameworkContext(DbConnectionString, DBTypeEnum.SQLite));
            services.AddScoped<IDataContext>(sp => sp.GetRequiredService<FrameworkContext>());

            // Build minimal Configs with JWT options
            var configs = new Configs();
            configs.JwtOptions.SecurityKey = "WTM_Test_Key_AtLeast_32_Characters!!";
            configs.JwtOptions.Issuer = "WTM_Test";
            configs.JwtOptions.Audience = "WTM_Test";
            configs.JwtOptions.Expires = 3600;

            var mockOptions = new Mock<IOptionsMonitor<Configs>>();
            mockOptions.Setup(x => x.CurrentValue).Returns(configs);
            services.AddSingleton(mockOptions.Object);
            services.AddSingleton<ITokenService>(sp =>
                new TokenService(mockOptions.Object, sp));

            _serviceProvider = services.BuildServiceProvider();
            TokenService = _serviceProvider.GetRequiredService<ITokenService>();
        }

        /// <summary>
        /// Access the SQLite DbContext directly for seeding or assertion queries.
        /// Caller is responsible for disposing the scope.
        /// </summary>
        public FrameworkContext CreateDbContext()
        {
            var scope = _serviceProvider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<FrameworkContext>();
        }

        /// <summary>Seed a RefreshTokenEntity and save to the SQLite DB.</summary>
        public void SeedToken(RefreshTokenEntity token)
        {
            using var db = CreateDbContext();
            db.Set<RefreshTokenEntity>().Add(token);
            db.SaveChanges();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _serviceProvider.Dispose();
                _keepAlive.Close();
                _keepAlive.Dispose();
                _disposed = true;
            }
        }
    }
}
