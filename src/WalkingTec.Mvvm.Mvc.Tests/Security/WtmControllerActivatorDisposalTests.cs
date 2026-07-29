using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc.Helper;

namespace WalkingTec.Mvvm.Mvc.Tests.Security
{
    /// <summary>
    /// Issue #882 review, second round: <see cref="WtmControllerActivator"/> decorates whichever
    /// <see cref="IControllerActivator"/> was already registered, but <c>FrameworkServiceExtension
    /// .AddWtmContext</c> constructs that inner instance itself (invoking the captured
    /// descriptor's factory, or <c>ActivatorUtilities.CreateInstance</c> against its
    /// implementation type) -- bypassing the DI container's own creation path, which is what
    /// normally enrolls a freshly-created disposable instance into the owning scope's disposables
    /// list. The DI container only ever sees and tracks the OUTER <see cref="WtmControllerActivator"/>;
    /// before this fix it implemented neither <see cref="IDisposable"/> nor
    /// <see cref="IAsyncDisposable"/> at all, so a disposable third-party inner activator would
    /// silently stop being disposed once wrapped. Neither built-in activator
    /// (<c>DefaultControllerActivator</c>, <c>ServiceBasedControllerActivator</c>) is itself
    /// disposable, which is why the existing HTTP test suite never caught this.
    /// </summary>
    [TestClass]
    public class WtmControllerActivatorDisposalTests
    {
        /// <summary>Records whether Dispose/DisposeAsync was called, and how many times.</summary>
        private sealed class SpyDisposableActivator : IControllerActivator, IDisposable
        {
            public int DisposeCount { get; private set; }

            public object Create(ControllerContext context) => new object();

            public void Release(ControllerContext context, object controller) { }

            public void Dispose() => DisposeCount++;
        }

        private sealed class SpyAsyncDisposableActivator : IControllerActivator, IAsyncDisposable
        {
            public int DisposeAsyncCount { get; private set; }

            public object Create(ControllerContext context) => new object();

            public void Release(ControllerContext context, object controller) { }

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCount++;
                return default;
            }
        }

        private sealed class SpyBothDisposableActivator : IControllerActivator, IDisposable, IAsyncDisposable
        {
            public int DisposeCount { get; private set; }
            public int DisposeAsyncCount { get; private set; }

            public object Create(ControllerContext context) => new object();

            public void Release(ControllerContext context, object controller) { }

            public void Dispose() => DisposeCount++;

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCount++;
                return default;
            }
        }

        // ─── Direct unit tests on WtmControllerActivator's own Dispose/DisposeAsync ───

        [TestMethod]
        public void Dispose_OwnsInnerTrue_DisposesTheDisposableInner()
        {
            var inner = new SpyDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: true);

            wrapper.Dispose();

            inner.DisposeCount.Should().Be(1, "the wrapper built `inner` itself (ownsInner: true), so nothing else will ever dispose it");
        }

        [TestMethod]
        public void Dispose_OwnsInnerFalse_DoesNotDisposeTheInner()
        {
            var inner = new SpyDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: false);

            wrapper.Dispose();

            inner.DisposeCount.Should().Be(0, "a pre-built/possibly-shared inner (ownsInner: false) must not be torn down by a single wrapper's disposal");
        }

        [TestMethod]
        public async Task DisposeAsync_OwnsInnerTrue_PrefersInnersDisposeAsync_OverDispose()
        {
            var inner = new SpyBothDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: true);

            await wrapper.DisposeAsync();

            inner.DisposeAsyncCount.Should().Be(1, "DisposeAsync should reach IAsyncDisposable.DisposeAsync when available");
            inner.DisposeCount.Should().Be(0, "DisposeAsync should not ALSO call the synchronous Dispose when IAsyncDisposable is available");
        }

        [TestMethod]
        public async Task DisposeAsync_OwnsInnerTrue_FallsBackToDispose_WhenInnerIsOnlySyncDisposable()
        {
            var inner = new SpyDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: true);

            await wrapper.DisposeAsync();

            inner.DisposeCount.Should().Be(1, "an inner that is only IDisposable (not IAsyncDisposable) must still be released via DisposeAsync's fallback");
        }

        [TestMethod]
        public async Task DisposeAsync_OwnsInnerFalse_DoesNotDisposeTheInner()
        {
            var inner = new SpyAsyncDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: false);

            await wrapper.DisposeAsync();

            inner.DisposeAsyncCount.Should().Be(0, "a pre-built/possibly-shared inner (ownsInner: false) must not be torn down by a single wrapper's disposal");
        }

        /// <summary>
        /// #882 review, third round -- Blocking 1: a sync <c>Dispose()</c> on an inner that
        /// implements ONLY <see cref="IAsyncDisposable"/> (not <see cref="IDisposable"/>) must
        /// THROW, not silently do nothing. Before this fix, <c>Dispose()</c> only checked
        /// <see cref="IDisposable"/> and fell straight through for an async-only inner -- a
        /// loud failure (the DI container's own sync scope disposal throws
        /// <see cref="InvalidOperationException"/> for a tracked service in this exact shape)
        /// silently became a no-op success. This is the direct regression test for that fix;
        /// see also <c>AddWtmContext_SyncScopeDispose_ThrowsForAsyncOnlyInnerActivator_
        /// InsteadOfSilentlySkipping</c> below for the same thing through the real container.
        /// </summary>
        [TestMethod]
        public void Dispose_OwnsInnerTrue_InnerIsAsyncDisposableOnly_ThrowsInsteadOfSilentlySkipping()
        {
            var inner = new SpyAsyncDisposableActivator();
            var wrapper = new WtmControllerActivator(inner, ownsInner: true);

            var act = () => wrapper.Dispose();

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*IAsyncDisposable*")
                .WithMessage("*DisposeAsync*");
            inner.DisposeAsyncCount.Should().Be(0, "the sync path cannot actually complete the async disposal -- it must throw, not silently claim success");
        }

        // ─── End-to-end: the REAL AddWtmContext wiring, not a mirrored copy ───

        /// <summary>
        /// Proves the actual registration in <c>FrameworkServiceExtension.AddWtmContext</c> --
        /// not just the <see cref="WtmControllerActivator"/> class in isolation -- disposes a
        /// disposable custom <see cref="IControllerActivator"/> that was registered before
        /// <c>AddWtmContext()</c> ran (the supported call order: <c>AddMvc()</c>/
        /// <c>AddControllers()</c> [+ any custom activator] BEFORE <c>AddWtmContext()</c>).
        /// This is what would have caught the original gap: both built-in activators are
        /// non-disposable, so only a real custom disposable activator, wrapped through the
        /// real production code path, can prove the fix.
        /// </summary>
        [TestMethod]
        public void AddWtmContext_DisposesCustomDisposableInnerActivator_OnScopeDispose()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            var inner = new SpyDisposableActivator();
            services.Replace(ServiceDescriptor.Singleton<IControllerActivator>(inner));

            var config = BuildMinimalWtmConfig();
            services.AddWtmContext(config);

            using (var provider = services.BuildServiceProvider())
            {
                using (var scope = provider.CreateScope())
                {
                    var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
                    activator.Should().BeOfType<WtmControllerActivator>();
                }
                // scope disposed here
            }

            inner.DisposeCount.Should().Be(
                0,
                "the custom activator was registered as a pre-built ImplementationInstance (a Singleton instance, potentially reused across every request) -- " +
                "AddWtmContext must NOT dispose it on a single scope's teardown, matching the DI container's own long-standing rule that it never auto-disposes " +
                "an ImplementationInstance registration either, since the caller who built the instance owns its lifetime");
        }

        /// <summary>
        /// Same wiring, but the custom activator is registered by TYPE (Transient), the shape
        /// AddWtmContext actually owns constructing (via ActivatorUtilities.CreateInstance) --
        /// this is the case that leaked before the fix.
        /// </summary>
        [TestMethod]
        public void AddWtmContext_DisposesTypeRegisteredInnerActivator_OnScopeDispose()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            services.Replace(ServiceDescriptor.Transient<IControllerActivator, TrackingDisposableActivator>());

            var config = BuildMinimalWtmConfig();
            services.AddWtmContext(config);

            using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
                activator.Should().BeOfType<WtmControllerActivator>();
            }

            TrackingDisposableActivator.LastInstanceDisposeCount.Should().Be(
                1,
                "AddWtmContext built this inner instance itself via ActivatorUtilities.CreateInstance, bypassing the container's own tracked " +
                "construction path -- before the fix nothing disposed it once wrapped, because the outer WtmControllerActivator implemented " +
                "neither IDisposable nor IAsyncDisposable");
        }

        private sealed class TrackingDisposableActivator : IControllerActivator, IDisposable
        {
            public static int LastInstanceDisposeCount;

            public object Create(ControllerContext context) => new object();

            public void Release(ControllerContext context, object controller) { }

            public void Dispose() => LastInstanceDisposeCount++;
        }

        /// <summary>
        /// Same wiring again, but through the <c>ImplementationFactory</c> branch specifically
        /// (<c>services.Replace(ServiceDescriptor.Transient&lt;IControllerActivator&gt;(sp =>
        /// ...))</c>) -- the third of the three ways <c>existingActivatorDescriptor</c> can be
        /// shaped (<c>ImplementationInstance</c>/<c>ImplementationType</c>/
        /// <c>ImplementationFactory</c>), and the one the other two disposal tests above don't
        /// cover.
        /// </summary>
        [TestMethod]
        public void AddWtmContext_DisposesFactoryRegisteredInnerActivator_OnScopeDispose()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            var spy = new SpyDisposableActivator();
            services.Replace(ServiceDescriptor.Transient<IControllerActivator>(_ => spy));

            var config = BuildMinimalWtmConfig();
            services.AddWtmContext(config);

            using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
                activator.Should().BeOfType<WtmControllerActivator>();
            }

            spy.DisposeCount.Should().Be(
                1,
                "AddWtmContext built this inner instance itself by invoking the captured descriptor's ImplementationFactory, bypassing the container's own " +
                "tracked construction path -- ownsInner must be true for this branch exactly like the ImplementationType branch above");
        }

        /// <summary>
        /// #882 review, third round: proves the REAL container's async scope teardown
        /// (<c>AsyncServiceScope.DisposeAsync</c>) reaches <c>WtmControllerActivator</c>'s
        /// <c>DisposeAsync</c> -- not its sync <c>Dispose</c> -- and that it in turn prefers the
        /// inner's <see cref="IAsyncDisposable"/> over any <see cref="IDisposable"/>. The direct
        /// unit test (<c>DisposeAsync_OwnsInnerTrue_PrefersInnersDisposeAsync_OverDispose</c>)
        /// proves the class's own logic; this proves the real wiring reaches it.
        /// </summary>
        [TestMethod]
        public async Task AddWtmContext_AsyncScopeDisposeAsync_PrefersInnersAsyncDisposal_ThroughRealContainer()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            services.Replace(ServiceDescriptor.Transient<IControllerActivator, TrackingBothDisposableActivator>());

            var config = BuildMinimalWtmConfig();
            TrackingBothDisposableActivator.Reset();

            services.AddWtmContext(config);

            await using (var provider = services.BuildServiceProvider())
            {
                var scope = provider.CreateAsyncScope();
                var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
                activator.Should().BeOfType<WtmControllerActivator>();
                await scope.DisposeAsync();
            }

            TrackingBothDisposableActivator.DisposeAsyncCount.Should().Be(1, "an async scope teardown must prefer the inner's DisposeAsync");
            TrackingBothDisposableActivator.DisposeCount.Should().Be(0, "and must not ALSO call the inner's synchronous Dispose when DisposeAsync is available");
        }

        private sealed class TrackingBothDisposableActivator : IControllerActivator, IDisposable, IAsyncDisposable
        {
            public static int DisposeCount;
            public static int DisposeAsyncCount;

            public static void Reset()
            {
                DisposeCount = 0;
                DisposeAsyncCount = 0;
            }

            public object Create(ControllerContext context) => new object();

            public void Release(ControllerContext context, object controller) { }

            public void Dispose() => DisposeCount++;

            public ValueTask DisposeAsync()
            {
                DisposeAsyncCount++;
                return default;
            }
        }

        /// <summary>
        /// #882 review, third round -- Blocking 1, through the real container: a SYNC
        /// <c>scope.Dispose()</c> against an async-only-disposable inner activator must throw,
        /// exactly like the DI container's own sync disposal would for a service it tracked
        /// itself (see <c>WtmControllerActivator.Dispose</c>'s XML doc for the verified
        /// original exception this reproduces). Registered by TYPE so AddWtmContext builds it
        /// itself (ownsInner: true) -- the case this bug applied to.
        /// </summary>
        [TestMethod]
        public void AddWtmContext_SyncScopeDispose_ThrowsForAsyncOnlyInnerActivator_InsteadOfSilentlySkipping()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            services.Replace(ServiceDescriptor.Transient<IControllerActivator, SpyAsyncDisposableActivator>());

            var config = BuildMinimalWtmConfig();
            services.AddWtmContext(config);

            using var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
            activator.Should().BeOfType<WtmControllerActivator>();

            var act = () => scope.Dispose();

            act.Should().Throw<InvalidOperationException>("a sync scope.Dispose() must not silently skip disposing an async-only-disposable inner activator");
        }

        /// <summary>
        /// Issue #882 review, second round: a KEYED <see cref="IControllerActivator"/>
        /// registration (.NET 8+ keyed services) must not be picked as "the" existing
        /// activator to wrap, even if it is the last-registered descriptor for that service
        /// type. A keyed descriptor's unkeyed ImplementationType/ImplementationFactory/
        /// ImplementationInstance getters all return null (they are irrelevant to an unkeyed
        /// resolution), and an unkeyed sp.GetRequiredService&lt;IControllerActivator&gt;() --
        /// what this activator and MVC itself actually call -- never resolves a keyed
        /// registration regardless of registration order. Before the fix, AddWtmContext's
        /// LastOrDefault had no IsKeyedService check, so this exact scenario would throw at
        /// first resolution instead of correctly finding and wrapping the real (unkeyed)
        /// activator underneath.
        /// </summary>
        [TestMethod]
        public void AddWtmContext_IgnoresKeyedControllerActivatorRegistration_WrapsRealUnkeyedOne()
        {
            var services = new ServiceCollection();
            services.AddMvcCore();
            // A keyed registration added AFTER the real (unkeyed) DefaultControllerActivator
            // AddMvcCore() just registered -- last in the collection, but invisible to an
            // unkeyed GetRequiredService<IControllerActivator>() call.
            services.AddKeyedSingleton<IControllerActivator, SpyDisposableActivator>("someKey");

            var config = BuildMinimalWtmConfig();
            var act = () => services.AddWtmContext(config);

            act.Should().NotThrow("a keyed IControllerActivator registration must be skipped when picking the activator to wrap");

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var activator = scope.ServiceProvider.GetRequiredService<IControllerActivator>();
            activator.Should().BeOfType<WtmControllerActivator>("the real (unkeyed) DefaultControllerActivator registration must still be the one wrapped");
        }

        /// <summary>
        /// AddWtmContext (FrameworkServiceExtension.cs:570) walks <c>conf.Connections</c> at
        /// registration time to eagerly EnsureCreate() every enabled connection -- an empty
        /// ConfigurationBuilder().Build() leaves Configs.Connections null (Configs.Get&lt;T&gt;
        /// binds nothing), producing an unrelated NullReferenceException before this test's own
        /// assertions are ever reached. An explicit empty "Connections" array avoids that without
        /// touching a real database, since the loop then has nothing to iterate.
        /// </summary>
        private static IConfiguration BuildMinimalWtmConfig()
        {
            var json = "{\"Connections\":[]}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
    }
}
