using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public interface ITokenService
    {
        Task<Token> IssueTokenAsync(LoginUserInfo loginUserInfo,
            string? ipAddress = null);
        Task<Token> RefreshTokenAsync(string refreshToken,
            string? ipAddress = null);
        Task RevokeTokenAsync(string refreshToken,
            string? ipAddress = null, string? reason = null);
    }
}
