using System;
using FluentAssertions;
using WalkingTec.Mvvm.Core;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests
{
    public class RefreshTokenEntityTests
    {
        [Fact]
        public void NewToken_IsActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddDays(7)
            };
            token.IsActive.Should().BeTrue();
            token.IsExpired.Should().BeFalse();
            token.IsRevoked.Should().BeFalse();
        }

        [Fact]
        public void ExpiredToken_IsNotActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            token.IsActive.Should().BeFalse();
            token.IsExpired.Should().BeTrue();
        }

        [Fact]
        public void RevokedToken_IsNotActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                RevokedUtc = DateTime.UtcNow
            };
            token.IsActive.Should().BeFalse();
            token.IsRevoked.Should().BeTrue();
        }

        [Fact]
        public void NewToken_HasGeneratedID()
        {
            var token = new RefreshTokenEntity();
            token.ID.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public void NewToken_HasCreatedUtcSet()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var token = new RefreshTokenEntity();
            var after = DateTime.UtcNow.AddSeconds(1);
            token.CreatedUtc.Should().BeAfter(before).And.BeBefore(after);
        }
    }
}
