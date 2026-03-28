#nullable enable
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates authentication logic previously embedded in WTMContext.
    /// Handles password verification, remote-host authentication, and token refresh.
    ///
    /// Note: The full DoLoginAsync orchestration (which mutates WTMContext._dc and calls
    /// LoadBasicInfoAsync(WTMContext)) remains in WTMContext until Phase 4. This service
    /// extracts the reusable, testable parts.
    /// </summary>
    public interface IWtmAuthService
    {
        /// <summary>
        /// Verify a password against the stored hash, auto-rehashing legacy formats.
        /// </summary>
        /// <param name="storedHash">The stored password hash.</param>
        /// <param name="providedPassword">The password provided by the user.</param>
        /// <returns>Verification result (Failed, Success, SuccessRehashNeeded).</returns>
        PasswordVerifyResult VerifyPassword(string? storedHash, string? providedPassword);

        /// <summary>
        /// Hash a plaintext password using the current algorithm (PBKDF2).
        /// </summary>
        string HashPassword(string? password);

        /// <summary>
        /// Authenticate via remote main host using a remote token or username/password.
        /// Returns the LoginUserInfo from the remote host, or null if authentication fails.
        /// </summary>
        Task<LoginUserInfo?> AuthenticateViaRemoteHostAsync(
            IWtmApiClient apiClient,
            string? remoteToken,
            string? username,
            string? password);

        /// <summary>
        /// Refresh the current user's JWT token.
        /// </summary>
        Task<Token?> RefreshTokenAsync(
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost);
    }
}
