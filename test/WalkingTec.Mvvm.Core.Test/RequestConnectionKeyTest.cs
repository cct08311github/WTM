using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc.Filters;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The database connection of a request is decided by the server (FixConnection, the connection function, the default). A connection
    /// key sent in the query string (DONOTUSECSName) is honoured only when the application opts in with
    /// GenericEndpoints:AllowRequestConnectionKey.
    /// </summary>
    [TestClass]
    public class RequestConnectionKeyTest
    {
        [TestMethod]
        public void Option_IsOffByDefault()
        {
            Assert.IsFalse(new Configs().GenericEndpoints.AllowRequestConnectionKey);
            Assert.IsFalse(new GenericEndpointOptions().AllowRequestConnectionKey);
        }

        [TestMethod]
        public void ByDefault_TheKeyOfTheRequestIsIgnored()
        {
            var configs = new Configs();

            Assert.IsNull(DataContextFilter.GetRequestedConnectionKey("default", configs));
            Assert.IsNull(DataContextFilter.GetRequestedConnectionKey("other", configs));
            Assert.IsNull(DataContextFilter.GetRequestedConnectionKey(null, configs));
        }

        [TestMethod]
        public void WithoutConfiguration_TheKeyOfTheRequestIsIgnored()
        {
            Assert.IsNull(DataContextFilter.GetRequestedConnectionKey("other", null));
        }

        [TestMethod]
        public void WhenTheApplicationOptsIn_TheKeyOfTheRequestIsUsed()
        {
            var configs = new Configs { GenericEndpoints = new GenericEndpointOptions { AllowRequestConnectionKey = true } };

            Assert.AreEqual("other", DataContextFilter.GetRequestedConnectionKey("other", configs));
            Assert.IsNull(DataContextFilter.GetRequestedConnectionKey(null, configs));
        }
    }
}
