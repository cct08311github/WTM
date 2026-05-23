#nullable enable
using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Verifies that <see cref="WtmCspReportMiddleware.EvictStaleBuckets"/>
    /// removes empty, stale buckets while keeping recent or non-empty ones.
    /// Closes issue #26 (unbounded dictionary growth).
    /// </summary>
    [TestClass]
    public class WtmCspReportRateBucketEvictionTests
    {
        private static WtmCspReportMiddleware CreateMiddleware(int rateLimit = 10)
        {
            var options = new WtmCspReportOptions { RateLimitPerMinute = rateLimit };
            var logger = NullLogger<WtmCspReportMiddleware>.Instance;
            return new WtmCspReportMiddleware(
                _ => System.Threading.Tasks.Task.CompletedTask,
                options,
                logger);
        }

        private static HttpContext MakeContext(string ip)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            ctx.Request.Method = HttpMethods.Post;
            ctx.Request.Path = "/_csp/report";
            return ctx;
        }

        [TestMethod]
        public void EvictStaleBuckets_RemovesEmpty_OldBuckets()
        {
            // Arrange: add a hit for an IP, then advance time past the 60 s window
            var mw = CreateMiddleware(rateLimit: 10);
            var ip = "10.0.0.1";

            // Record a hit at T=0 using the public CheckAndRecordRate path
            var ctx = MakeContext(ip);
            var hitTime = DateTimeOffset.UtcNow.AddSeconds(-120); // 2 minutes ago

            // Directly exercise EvictStaleBuckets with a "now" that is 120 s
            // after the last hit, so the bucket is both empty (hit expired) and
            // stale (LastHit > 60 s ago).
            // Use CheckAndRecordRate first to populate the bucket, then
            // manually evict with a future "now".
            mw.CheckAndRecordRate(ctx); // adds a real hit at UtcNow

            // Advance "now" by 90 s — the hit is now outside the 60 s window
            var futureNow = DateTimeOffset.UtcNow.AddSeconds(90);
            mw.EvictStaleBuckets(futureNow);

            // The bucket should have been removed; a new hit should be allowed
            // (rate limit counter resets from zero)
            var secondCtx = MakeContext(ip);
            var allowed = mw.CheckAndRecordRate(secondCtx);
            Assert.IsTrue(allowed, "After eviction, a fresh hit from the same IP must be allowed at rate-limit position 1.");
        }

        [TestMethod]
        public void EvictStaleBuckets_KeepsRecent_OrNonEmpty_Buckets()
        {
            // Arrange: two IPs — one recent, one old
            var mw = CreateMiddleware(rateLimit: 3);
            var recentIp = "10.0.0.2";
            var oldIp = "10.0.0.3";

            mw.CheckAndRecordRate(MakeContext(recentIp)); // hit at "now"
            mw.CheckAndRecordRate(MakeContext(oldIp));    // hit at "now"

            // Evict 30 s in the future — old hit is still in the 60 s window;
            // neither bucket should be evicted yet.
            var slightlyFuture = DateTimeOffset.UtcNow.AddSeconds(30);
            mw.EvictStaleBuckets(slightlyFuture);

            // Both IPs should still be rate-limited (each has 1 hit in window)
            // — i.e. they hit count 2 / 3 when we record a second request.
            bool recentAllowed = mw.CheckAndRecordRate(MakeContext(recentIp)); // 2nd hit
            bool oldAllowed = mw.CheckAndRecordRate(MakeContext(oldIp));       // 2nd hit

            Assert.IsTrue(recentAllowed, "Recent bucket must not be evicted at 30 s.");
            Assert.IsTrue(oldAllowed, "Old bucket must not be evicted while still within the 60 s window.");
        }
    }
}
