using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class RemoteTokenTest
    {
        private const string TestKey = "remote-token-test-key-0123456789-abcdef-0123456789-abcdef-0123456789-xyz";
        private const string OtherKey = "another-test-key-that-is-not-the-same-123456";
        private const string TestIssuer = "http://issuer.test";
        private const string TestAudience = "http://audience.test";

        private static WTMContext CreateContext(string remoteToken)
        {
            var wtm = MockWtmContext.CreateWtmContext(usercode: "admin", remoteToken: remoteToken);
            wtm.ConfigInfo.JwtOptions = new JwtOption
            {
                Issuer = TestIssuer,
                Audience = TestAudience,
                SecurityKey = TestKey
            };
            // start from an anonymous request, then cache the admin user the same way the framework does
            wtm.LoginUserInfo = null;
            wtm.Cache.Add($"{GlobalConstants.CacheKey.UserInfo}:{"admin" + "$`$" + (string)null}", new LoginUserInfo { ITCode = "admin" });
            return wtm;
        }

        private static string CreateToken(
            string key = TestKey,
            string issuer = TestIssuer,
            string audience = TestAudience,
            DateTime? notBefore = null,
            DateTime? expires = null,
            bool withExpiration = true,
            string algorithm = SecurityAlgorithms.HmacSha256)
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm);
            var claims = new List<Claim> { new Claim(AuthConstants.JwtClaimTypes.Subject, "admin") };
            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: notBefore,
                expires: withExpiration ? (expires ?? DateTime.UtcNow.AddMinutes(30)) : (DateTime?)null,
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static string Base64Url(string json)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        [TestMethod]
        public void UnsignedTokenIsRejected()
        {
            var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
            var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
            var payload = Base64Url($"{{\"itcode\":\"admin\",\"iss\":\"{TestIssuer}\",\"aud\":\"{TestAudience}\",\"exp\":{exp}}}");
            var wtm = CreateContext($"{header}.{payload}.");
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void TokenSignedWithDifferentKeyIsRejected()
        {
            var wtm = CreateContext(CreateToken(key: OtherKey));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void TokenSignedWithUnexpectedAlgorithmIsRejected()
        {
            var wtm = CreateContext(CreateToken(algorithm: SecurityAlgorithms.HmacSha512));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void ExpiredTokenIsRejected()
        {
            var wtm = CreateContext(CreateToken(notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1)));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void WrongIssuerIsRejected()
        {
            var wtm = CreateContext(CreateToken(issuer: "http://other-issuer.test"));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void WrongAudienceIsRejected()
        {
            var wtm = CreateContext(CreateToken(audience: "http://other-audience.test"));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void TokenWithoutExpirationIsRejected()
        {
            var wtm = CreateContext(CreateToken(withExpiration: false));
            Assert.IsNull(wtm.LoginUserInfo);
        }

        [TestMethod]
        public void MalformedTokenIsRejectedWithoutException()
        {
            var wtm = CreateContext("not-a-jwt");
            LoginUserInfo result = null;
            try
            {
                result = wtm.LoginUserInfo;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Unexpected exception: {ex.GetType().Name}");
            }
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ValidTokenReturnsCachedUser()
        {
            var wtm = CreateContext(CreateToken());
            var result = wtm.LoginUserInfo;
            Assert.IsNotNull(result);
            Assert.AreEqual("admin", result.ITCode);
        }
    }
}
