#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.Core.Test.ConfigOptions
{
    [TestClass]
    public class DomainTests
    {
        // ─── Property defaults ─────────────────────────────────────────────────

        [TestMethod]
        public void DefaultConstructor_AllPropertiesAreNull()
        {
            var d = new Domain();
            d.Name.Should().BeNull();
            d.Address.Should().BeNull();
            d.InnerAddress.Should().BeNull();
            d.EntryUrl.Should().BeNull();
            d.Url.Should().BeNull();
            d.InnerUrl.Should().BeNull();
        }

        // ─── Url property ─────────────────────────────────────────────────────

        [TestMethod]
        public void Url_WhenAddressIsNull_ReturnsNull()
        {
            var d = new Domain { Address = null };
            d.Url.Should().BeNull();
        }

        [TestMethod]
        public void Url_WhenAddressIsEmpty_ReturnsEmpty()
        {
            var d = new Domain { Address = "" };
            d.Url.Should().BeEmpty();
        }

        [TestMethod]
        public void Url_WhenAddressAlreadyHasHttp_ReturnsAsIs()
        {
            var d = new Domain { Address = "http://example.com" };
            d.Url.Should().Be("http://example.com");
        }

        [TestMethod]
        public void Url_WhenAddressAlreadyHasHttps_ReturnsAsIs()
        {
            var d = new Domain { Address = "https://example.com" };
            d.Url.Should().Be("https://example.com");
        }

        [TestMethod]
        public void Url_WhenAddressHasNoScheme_PrependsHttp()
        {
            var d = new Domain { Address = "example.com" };
            d.Url.Should().Be("http://example.com");
        }

        [TestMethod]
        public void Url_WhenAddressHasHttpUppercase_ReturnsAsIs()
        {
            var d = new Domain { Address = "HTTP://example.com" };
            d.Url.Should().Be("HTTP://example.com");
        }

        [TestMethod]
        public void Url_WhenAddressHasHttpsUppercase_ReturnsAsIs()
        {
            var d = new Domain { Address = "HTTPS://example.com" };
            d.Url.Should().Be("HTTPS://example.com");
        }

        [TestMethod]
        public void Url_WhenAddressHasPortWithNoScheme_PrependsHttp()
        {
            var d = new Domain { Address = "example.com:8080" };
            d.Url.Should().Be("http://example.com:8080");
        }

        // ─── InnerUrl property ────────────────────────────────────────────────

        [TestMethod]
        public void InnerUrl_WhenInnerAddressIsNull_ReturnsNull()
        {
            var d = new Domain { InnerAddress = null };
            d.InnerUrl.Should().BeNull();
        }

        [TestMethod]
        public void InnerUrl_WhenInnerAddressIsEmpty_ReturnsEmpty()
        {
            var d = new Domain { InnerAddress = "" };
            d.InnerUrl.Should().BeEmpty();
        }

        [TestMethod]
        public void InnerUrl_WhenInnerAddressHasNoScheme_PrependsHttp()
        {
            var d = new Domain { InnerAddress = "internal.example.com" };
            d.InnerUrl.Should().Be("http://internal.example.com");
        }

        [TestMethod]
        public void InnerUrl_WhenInnerAddressAlreadyHasHttp_ReturnsAsIs()
        {
            var d = new Domain { InnerAddress = "http://internal.example.com" };
            d.InnerUrl.Should().Be("http://internal.example.com");
        }

        [TestMethod]
        public void InnerUrl_WhenInnerAddressAlreadyHasHttps_ReturnsAsIs()
        {
            var d = new Domain { InnerAddress = "https://internal.example.com" };
            d.InnerUrl.Should().Be("https://internal.example.com");
        }

        // ─── Simple property set/get ──────────────────────────────────────────

        [TestMethod]
        public void Name_CanBeSetAndRetrieved()
        {
            var d = new Domain { Name = "Production" };
            d.Name.Should().Be("Production");
        }

        [TestMethod]
        public void EntryUrl_CanBeSetAndRetrieved()
        {
            var d = new Domain { EntryUrl = "/login" };
            d.EntryUrl.Should().Be("/login");
        }

        [TestMethod]
        public void Address_CanBeSetAndRetrieved()
        {
            var d = new Domain { Address = "example.com" };
            d.Address.Should().Be("example.com");
        }

        [TestMethod]
        public void InnerAddress_CanBeSetAndRetrieved()
        {
            var d = new Domain { InnerAddress = "10.0.0.1" };
            d.InnerAddress.Should().Be("10.0.0.1");
        }
    }
}
