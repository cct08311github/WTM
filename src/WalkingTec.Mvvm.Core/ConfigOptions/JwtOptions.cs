using System;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core.Auth;

namespace WalkingTec.Mvvm.Core
{
    public class JwtOption
    {
        public string Issuer { get; set; } = "http://localhost";
        public string Audience { get; set; } = "http://localhost";
        public int Expires { get; set; } = 3600;
        private string _securiteKey= "wtmwtmwtmwtmwtmwtm";
        public string SecurityKey { get
            {
                return _securiteKey;
            }
            set {
                _securiteKey = value;
                if (_securiteKey.Length < 32)
                {
                    var count = 32 - _securiteKey.Length;
                    for (int i = 0; i < count; i++)
                    {
                        _securiteKey += "x";
                    }
                }

            }
        }
        public string LoginPath { get; set; }

        /// <summary>
        /// Build the parameters used to validate tokens issued with these options
        /// (signature, algorithm, issuer, audience and lifetime are all required).
        /// </summary>
        public TokenValidationParameters CreateValidationParameters()
        {
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecurityKey)),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = AuthConstants.JwtClaimTypes.Name,
                RoleClaimType = AuthConstants.JwtClaimTypes.Role
            };
        }
    }
}
