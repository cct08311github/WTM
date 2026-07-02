using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Regression tests for <see cref="SessionExtensions.SetAsync{T}(ISession, string, T)"/>
    /// (Issue #535). The sync <c>Set&lt;T&gt;</c> extension blocked a ThreadPool thread via
    /// <c>CommitAsync().GetAwaiter().GetResult()</c> on the unauthenticated captcha path.
    /// <c>SetAsync&lt;T&gt;</c> is additive and must serialize identically so values stay
    /// byte-compatible with <see cref="SessionExtensions.Get{T}(ISession, string)"/>
    /// regardless of which write path produced them.
    /// </summary>
    [TestClass]
    public class SessionExtensionTests
    {
        private sealed class SamplePayload
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [TestMethod]
        public async Task SetAsync_ThenGet_RoundTripsStringValue()
        {
            var session = new MockHttpSession();

            await session.SetAsync("verify_code", "AB12");

            var result = session.Get<string>("verify_code");

            Assert.AreEqual("AB12", result);
        }

        [TestMethod]
        public async Task SetAsync_ThenGet_RoundTripsComplexValue()
        {
            var session = new MockHttpSession();
            var value = new SamplePayload { Id = 42, Name = "test" };

            await session.SetAsync("payload", value);

            var result = session.Get<SamplePayload>("payload");

            Assert.IsNotNull(result);
            Assert.AreEqual(value.Id, result.Id);
            Assert.AreEqual(value.Name, result.Name);
        }

        [TestMethod]
        public async Task SetAsync_SerializesIdenticallyToSyncSet()
        {
            // SetAsync must produce the exact same serialized session string as the sync
            // Set<T> so a value can be written via one path and correctly read via Get<T>
            // regardless of which write path was used (compatibility requirement).
            var syncSession = new MockHttpSession();
            var asyncSession = new MockHttpSession();

            syncSession.Set("verify_code", "SYNC1");
            await asyncSession.SetAsync("verify_code", "SYNC1");

            var syncRaw = syncSession.GetString("verify_code");
            var asyncRaw = asyncSession.GetString("verify_code");

            Assert.AreEqual(syncRaw, asyncRaw,
                "SetAsync<T> must serialize identically to the sync Set<T> extension.");
        }
    }
}
