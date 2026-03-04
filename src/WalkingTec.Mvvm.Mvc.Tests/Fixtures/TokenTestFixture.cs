using System;
using System.Collections.Generic;
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
    /// Provides an isolated TokenService instance backed by EF InMemory for integration tests.
    /// Each test class should create a new fixture instance (don't share across tests).
    ///
    /// TokenService uses IServiceProvider.CreateScope() to resolve IDataContext.
    /// We satisfy this by registering a scoped EmptyContext with InMemory EF in the DI container.
    /// </summary>
    public class TokenTestFixture : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private bool _disposed;

        public ITokenService TokenService { get; }

        /// <summary>The InMemory DB name — unique per fixture to isolate tests.</summary>
        public string DbName { get; } = $"TokenTest_{Guid.NewGuid():N}";

        public TokenTestFixture()
        {
            var services = new ServiceCollection();

            // Use FrameworkContext (not EmptyContext) so RefreshTokenEntity is in the EF model.
            // Use the (string, DBTypeEnum) constructor so OnConfiguring picks Memory provider only.
            // Avoid AddDbContext() — it would pass DbContextOptions that combines with
            // OnConfiguring's SqlServer default, causing "two providers" InvalidOperationException.
            services.AddScoped<FrameworkContext>(_ => new FrameworkContext(DbName, DBTypeEnum.Memory));
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
        /// Access the InMemory DbContext directly for seeding or assertion queries.
        /// Caller is responsible for disposing the scope.
        /// </summary>
        public FrameworkContext CreateDbContext()
        {
            var scope = _serviceProvider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<FrameworkContext>();
        }

        /// <summary>Seed a RefreshTokenEntity and save to InMemory DB.</summary>
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
                _disposed = true;
            }
        }
    }
}
