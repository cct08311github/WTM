using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #721 — [SECURITY] refresh-token identity-bypass regression test.
///
/// Before the fix, <c>WTMContext.RefreshTokenAsync()</c> (called by the app-level
/// <c>AccountController.RefreshToken(string refreshToken)</c> action) ignored the
/// presented refresh token entirely and reissued a fresh token pair purely from
/// <c>LoginUserInfo</c> identity — a bogus/never-issued/expired/already-rotated
/// refresh token was never actually checked. The existing unit suites
/// (TokenChainSecurityTests / RefreshTokenAtomicRotationTests / TokenServiceIntegrationTests)
/// call <c>ITokenService</c> directly and therefore never exercised the HTTP route where
/// the bypass lived — that is exactly what this class covers.
///
/// These tests drive the full HTTP pipeline (DemoWebApplicationFactory) against
/// <c>POST /api/_account/refreshtoken</c>, the single canonical endpoint after the
/// #721 fix (the demo's shadowing AccountController.RefreshToken action was removed).
///
/// CRITICAL: the pre-fix vulnerable path (the demo's <c>[AllRights]</c>-gated
/// <c>AccountController.RefreshToken</c> action) was only ever reachable by a caller
/// who ALREADY holds a valid access token — the bypass was "mint a fresh token pair
/// from my own live identity while presenting a garbage/absent refresh token", not
/// "refresh with no credentials at all". An unauthenticated request never reached the
/// vulnerable identity-based-reissue code; it was rejected upstream by the auth filter
/// for an unrelated reason (empirically: 401 with an empty body and a
/// <c>WWW-Authenticate: Bearer</c> challenge header — the signature of "you're not
/// logged in", not "your refresh token is invalid"). That earlier unauthenticated-only
/// version of these tests therefore passed on BOTH pre-fix and post-fix code, for
/// unrelated reasons, and would not have caught a regression. Every test below
/// authenticates first (LoginJwt) and attaches the resulting access token as a Bearer
/// credential before presenting the bogus/empty/absent refresh token — reproducing the
/// exact caller profile the #721 bypass required. Verified by reproduction against the
/// pre-fix commit: with a valid Bearer token attached, POSTing a bogus refresh token to
/// this exact route returned HTTP 200 with a freshly minted, fully usable
/// access_token/refresh_token pair (the bypass); the fixed code returns 401 with
/// <c>{"message":"Invalid or expired refresh token"}</c> and no WWW-Authenticate header.
/// </summary>
[TestClass]
public class RefreshTokenApiTests
{
    private static DemoWebApplicationFactory _factory = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>
    /// Logs in via the JWT API (admin/000000, the demo's seeded account) and returns the
    /// issued access/refresh token pair — this is a GENUINELY issued, DB-backed
    /// RefreshTokenEntity row, as opposed to the bogus strings used elsewhere in this class.
    /// </summary>
    private async Task<TokenDto> LoginAndGetTokenAsync(HttpClient client)
    {
        var loginResp = await client.PostAsync("/api/_account/LoginJwt",
            new StringContent(JsonSerializer.Serialize(new { Account = "admin", Password = "000000" }),
                Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.OK, loginResp.StatusCode,
            $"Precondition failed: admin/000000 LoginJwt should succeed. Got {(int)loginResp.StatusCode}: " +
            await loginResp.Content.ReadAsStringAsync());

        var body = await loginResp.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<TokenDto>(body);
        Assert.IsFalse(string.IsNullOrEmpty(token?.RefreshToken),
            $"Precondition failed: LoginJwt did not return a refresh_token. Body: {body}");
        Assert.IsFalse(string.IsNullOrEmpty(token?.AccessToken),
            $"Precondition failed: LoginJwt did not return an access_token. Body: {body}");
        return token!;
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string? refreshToken) =>
        client.PostAsync("/api/_account/refreshtoken",
            new StringContent(JsonSerializer.Serialize(new { RefreshToken = refreshToken }),
                Encoding.UTF8, "application/json"));

    /// <summary>
    /// Presents <paramref name="refreshToken"/> to the refresh endpoint WHILE authenticated
    /// as a real, currently-logged-in user (Bearer <paramref name="accessToken"/> attached).
    /// This is the exact caller profile the #721 bypass required — an unauthenticated
    /// request never reached the vulnerable code path at all (see class doc).
    /// </summary>
    private static Task<HttpResponseMessage> PostRefreshAuthenticatedAsync(
        HttpClient client, string accessToken, string? refreshToken, string? rawJsonBody = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/_account/refreshtoken")
        {
            Content = new StringContent(
                rawJsonBody ?? JsonSerializer.Serialize(new { RefreshToken = refreshToken }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(req);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (a) MANDATORY — bogus / never-issued refresh token, presented by an
    //     AUTHENTICATED caller (valid Bearer access token attached), must be
    //     rejected. This reproduces the exact #721 caller profile: someone who
    //     already holds a live access token and could, pre-fix, mint a fresh
    //     token pair from that identity alone while presenting garbage (or no)
    //     refresh token. An unauthenticated request never reached the
    //     vulnerable code — see class doc — so these tests MUST authenticate
    //     first to actually discriminate pre-fix from post-fix behavior.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RefreshToken_AuthenticatedCaller_WithBogusNeverIssuedToken_IsRejected()
    {
        var client = CreateClient();
        var authenticated = await LoginAndGetTokenAsync(client);

        var resp = await PostRefreshAuthenticatedAsync(
            client, authenticated.AccessToken!,
            "this-refresh-token-was-never-issued-by-anyone-" + Guid.NewGuid());
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode,
            $"#721: an authenticated caller presenting a bogus/never-issued refresh token MUST be " +
            $"rejected (401), not silently honoured via identity-based reissue. " +
            $"Got {(int)resp.StatusCode}. Body: {body}");

        // The pre-fix bypass returns a WWW-Authenticate-free, freshly-issued token pair with
        // HTTP 200; the fixed business-logic rejection is a 401 with no WWW-Authenticate
        // challenge header (that header would instead indicate an upstream auth-filter
        // rejection — a different, non-discriminating failure mode; see class doc).
        Assert.IsNull(resp.Headers.WwwAuthenticate.FirstOrDefault(),
            $"#721: this must be a business-logic rejection from the refresh-token endpoint " +
            $"itself (no WWW-Authenticate challenge), not an upstream auth-filter bounce. " +
            $"Headers: {resp.Headers}");
        Assert.IsTrue(body.Contains("Invalid or expired refresh token", StringComparison.OrdinalIgnoreCase),
            $"#721: expected the hardened endpoint's specific rejection message. Body: {body}");

        // Must not accidentally hand back a usable token pair in the body either.
        Assert.IsFalse(body.Contains("access_token", StringComparison.OrdinalIgnoreCase),
            $"#721: rejection response must not contain a usable access_token. Body: {body}");
    }

    [TestMethod]
    public async Task RefreshToken_AuthenticatedCaller_WithEmptyToken_IsRejected()
    {
        var client = CreateClient();
        var authenticated = await LoginAndGetTokenAsync(client);

        var resp = await PostRefreshAuthenticatedAsync(client, authenticated.AccessToken!, "");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#721: an authenticated caller presenting an empty refresh token MUST be rejected — " +
            $"this is exactly the shape of the old identity-based-reissue bypass (no real token " +
            $"presented, but a live access token available to reissue from). Got {(int)resp.StatusCode}. Body: {body}");
        Assert.IsFalse(body.Contains("access_token", StringComparison.OrdinalIgnoreCase),
            $"#721: rejection response must not contain a usable access_token. Body: {body}");
    }

    [TestMethod]
    public async Task RefreshToken_AuthenticatedCaller_WithNoBodyAtAll_IsRejected()
    {
        var client = CreateClient();
        var authenticated = await LoginAndGetTokenAsync(client);

        // Simulates the pre-fix Blazor/Vue3/Demo call sites, which POSTed an EMPTY body
        // (`new { }`) while authenticated and relied on server-side identity to reissue —
        // the core #721 shape.
        var resp = await PostRefreshAuthenticatedAsync(
            client, authenticated.AccessToken!, refreshToken: null, rawJsonBody: "{}");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#721: an authenticated caller sending an empty-body refresh request MUST be rejected, " +
            $"not reissued from identity. Got {(int)resp.StatusCode}. Body: {body}");
        Assert.IsFalse(body.Contains("access_token", StringComparison.OrdinalIgnoreCase),
            $"#721: rejection response must not contain a usable access_token. Body: {body}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (b) A genuinely-issued, valid refresh token DOES rotate successfully.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RefreshToken_WithGenuinelyIssuedToken_ReturnsNewRotatedPair()
    {
        var client = CreateClient();
        var original = await LoginAndGetTokenAsync(client);

        var resp = await PostRefreshAsync(client, original.RefreshToken);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#721: a genuinely-issued, unexpired refresh token should succeed. Got {(int)resp.StatusCode}: {body}");

        var rotated = JsonSerializer.Deserialize<TokenDto>(body);
        Assert.IsFalse(string.IsNullOrEmpty(rotated?.AccessToken), "Rotated response must include an access_token.");
        Assert.IsFalse(string.IsNullOrEmpty(rotated?.RefreshToken), "Rotated response must include a refresh_token.");
        Assert.AreNotEqual(original.RefreshToken, rotated!.RefreshToken,
            "#721: the refresh token must be ROTATED (old token superseded), not reused.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (c) REPLAY — presenting the now-rotated OLD token again must be rejected.
    //     Proves the ExecuteUpdateAsync atomic-claim + replay-guard machinery is
    //     actually live on the HTTP path (not just below it, per the unit suites).
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RefreshToken_ReplayOfRotatedToken_IsRejected()
    {
        var client = CreateClient();
        var original = await LoginAndGetTokenAsync(client);

        var firstResp = await PostRefreshAsync(client, original.RefreshToken);
        Assert.AreEqual(HttpStatusCode.OK, firstResp.StatusCode,
            $"Precondition failed: first rotation should succeed. Body: {await firstResp.Content.ReadAsStringAsync()}");

        // Replay the OLD (now-rotated/revoked) refresh token.
        var replayResp = await PostRefreshAsync(client, original.RefreshToken);
        var replayBody = await replayResp.Content.ReadAsStringAsync();

        Assert.IsTrue(
            replayResp.StatusCode == HttpStatusCode.Unauthorized || replayResp.StatusCode == HttpStatusCode.BadRequest,
            $"#721/replay-guard: replaying a rotated refresh token MUST be rejected on the HTTP path. " +
            $"Got {(int)replayResp.StatusCode}. Body: {replayBody}");
        Assert.IsFalse(replayBody.Contains("access_token", StringComparison.OrdinalIgnoreCase),
            $"Replay rejection must not contain a usable access_token. Body: {replayBody}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Route-shadow regression (#721): exactly one action must live at this
    // route. Two attribute-routed actions on the same URL+verb throw
    // AmbiguousMatchException (500) — this proves the demo's duplicate
    // AccountController.RefreshToken action is really gone.
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RefreshToken_Route_IsNotAmbiguous()
    {
        var client = CreateClient();

        var resp = await PostRefreshAsync(client, "irrelevant-value-just-checking-routing");

        Assert.AreNotEqual(HttpStatusCode.InternalServerError, resp.StatusCode,
            "#721: api/_account/refreshtoken must resolve to exactly one action " +
            "(AmbiguousMatchException => 500 indicates the demo AccountController still shadows " +
            "the framework's _FrameworkController.RefreshToken route).");
    }
}
