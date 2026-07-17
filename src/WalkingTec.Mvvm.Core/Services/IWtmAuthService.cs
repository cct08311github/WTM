#nullable enable
using System;
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
        /// SECURITY (#721): this overload used to reissue a brand-new token pair purely
        /// from <paramref name="loginUser"/> identity, completely ignoring whatever
        /// refresh token (if any) the caller actually presented — an auth bypass that let
        /// anyone holding a still-valid access token mint fresh tokens indefinitely, with
        /// the entire <see cref="ITokenService"/> rotation/replay-guard machinery dead on
        /// this path. It now always rejects (returns <c>null</c>). Use
        /// <see cref="RefreshTokenAsync(string?, LoginUserInfo?, ITokenService, IWtmApiClient?, bool)"/>,
        /// which validates the presented refresh token (or forwards it to the mainhost for
        /// federation frontends) before issuing anything. Kept only for source/binary
        /// compatibility.
        /// </summary>
        [Obsolete("Insecure: performed identity-based reissue that ignored the presented refresh token (#721 auth bypass). Always rejects now. Use RefreshTokenAsync(string, LoginUserInfo, ITokenService, IWtmApiClient, bool).")]
        Task<Token?> RefreshTokenAsync(
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost);

        /// <summary>
        /// Refreshes a token pair by validating the caller-presented <paramref name="refreshToken"/>.
        /// This is the sanctioned refresh path (#721 security fix) — a bogus, never-issued,
        /// expired, or already-rotated refresh token is rejected (returns <c>null</c>); it
        /// never reissues purely from <paramref name="loginUser"/> identity. For federation
        /// frontends (<paramref name="hasMainHost"/> true with no current tenant) the real
        /// presented token is forwarded to the mainhost's hardened endpoint for validation —
        /// never an empty body.
        /// </summary>
        /// <param name="refreshToken">The refresh token the caller is presenting.</param>
        /// <param name="loginUser">The caller's current identity (used only for federation routing).</param>
        /// <param name="tokenService">The token service that performs local validation/rotation.</param>
        /// <param name="apiClient">API client used to forward the request to the mainhost when federated.</param>
        /// <param name="hasMainHost">Whether this host is a federation frontend of a mainhost.</param>
        Task<Token?> RefreshTokenAsync(
            string? refreshToken,
            LoginUserInfo? loginUser,
            ITokenService tokenService,
            IWtmApiClient? apiClient,
            bool hasMainHost);
    }
}
