#nullable enable
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.ConfigOptions
{
    /// <summary>
    /// Tests for issue #813: CookieOption.SecurePolicy must be a configurable
    /// property (not a hardcoded framework default) so production deployments
    /// behind TLS-terminating reverse proxies can force the Secure flag.
    /// </summary>
    [TestClass]
    public class CookieOptionTests
    {
        [TestMethod]
        public void SecurePolicy_defaults_to_SameAsRequest_for_backwards_compatibility()
        {
            var options = new CookieOption();
            Assert.AreEqual(CookieSecurePolicy.SameAsRequest, options.SecurePolicy,
                "Default must remain SameAsRequest so upgrading WTM does not silently change cookie behavior " +
                "for existing deployments that work over plain HTTP locally.");
        }

        [TestMethod]
        public void SecurePolicy_can_be_set_to_Always_for_production()
        {
            var options = new CookieOption
            {
                SecurePolicy = CookieSecurePolicy.Always
            };
            Assert.AreEqual(CookieSecurePolicy.Always, options.SecurePolicy);
        }

        [TestMethod]
        public void SecurePolicy_can_be_set_to_None_for_local_HTTP_development()
        {
            var options = new CookieOption
            {
                SecurePolicy = CookieSecurePolicy.None
            };
            Assert.AreEqual(CookieSecurePolicy.None, options.SecurePolicy);
        }

        [TestMethod]
        public void Other_existing_properties_retain_their_defaults_after_SecurePolicy_addition()
        {
            // Regression guard: adding SecurePolicy did not shift or break
            // any existing CookieOption field default.
            var options = new CookieOption();
            Assert.AreEqual("http://localhost", options.Issuer);
            Assert.AreEqual("http://localhost", options.Audience);
            Assert.AreEqual(3600, options.Expires);
            Assert.IsTrue(options.SlidingExpiration);
            Assert.AreEqual("/Login/Login", options.LoginPath);
            Assert.AreEqual("/Login/Logout", options.LogoutPath);
            Assert.AreEqual("/Login/Login", options.AccessDeniedPath);
            Assert.AreEqual(string.Empty, options.Domain);
            Assert.AreEqual("ReturnUrl", options.ReturnUrlParameter);
        }
    }
}
