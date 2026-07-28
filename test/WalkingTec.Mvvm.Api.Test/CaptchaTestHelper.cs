using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Session;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #837 patch 1 — the concrete obstacle to making <c>_strictFactory</c>
/// (<c>IsQuickDebug=false</c>) the login factory for every authenticated test: with
/// <c>IsQuickDebug=false</c>, <c>LoginController.cs</c>'s POST action additionally requires a
/// VerifyCode (captcha) matching the value <c>_FrameworkController.GetVerifyCode</c> stored in
/// server-side session — a check that is itself gated on the SAME <c>IsQuickDebug</c> flag as
/// the RBAC bypass patch 1 exists to close, but is an orthogonal control (anti-automation, not
/// authorization). <c>GetVerifyCode</c> renders the code as a PNG image; there is no
/// plaintext-returning test hook. A test client cannot "solve" that captcha by parsing the HTTP
/// response.
///
/// <para>
/// This helper reads the code back the other way: <c>ASP.NET Core Session</c> stores it
/// server-side, keyed by the session ID encoded (via <see cref="IDataProtector"/>, purpose
/// <c>"SessionMiddleware"</c> — see <c>Microsoft.AspNetCore.Session.SessionMiddleware</c> and its
/// internal <c>CookieProtection</c> helper, both decompiled to confirm this exact protocol) into
/// the session cookie. Given the SAME <see cref="IDistributedCache"/> and
/// <see cref="IDataProtectionProvider"/> the running app uses (both resolvable from
/// <c>factory.Services</c> with no HttpContext needed), the cookie value can be unprotected back
/// into the raw session key, and a <see cref="DistributedSession"/> can be constructed directly
/// against that key to read the value <c>_FrameworkController.GetVerifyCode</c> wrote —
/// reproducing exactly what a real ASP.NET Core request pipeline does, using only public APIs
/// (<see cref="IDataProtectionProvider.CreateProtector(string)"/>,
/// <see cref="DistributedSession"/>'s public constructor), not reflection into internals.
/// </para>
/// </summary>
internal static class CaptchaTestHelper
{
    private const string SessionCookieName = "WTMa.Session"; // Configs.CookiePre ("WTMa") + ".Session", set by AddWtmSession.
    private const string VerifyCodeSessionKey = "verify_code"; // matches _FrameworkController.GetVerifyCode's HttpContext.Session key.

    /// <summary>
    /// GETs <c>/_framework/GetVerifyCode</c> on <paramref name="client"/> (establishing a
    /// session and a freshly generated captcha value stored server-side under it), then reads
    /// that value back directly from the session store and returns it — the plaintext a real
    /// browser's user would have typed after looking at the rendered image.
    /// </summary>
    public static async Task<string> FetchAndSolveAsync(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, HttpClient client)
    {
        var verifyCodeResponse = await client.GetAsync("/_framework/GetVerifyCode");
        verifyCodeResponse.EnsureSuccessStatusCode();

        var sessionCookieValue = ExtractSessionCookieValue(verifyCodeResponse)
            ?? throw new InvalidOperationException(
                $"#837: expected a '{SessionCookieName}' Set-Cookie header from GetVerifyCode " +
                "— session must be established before a verify code can exist to read back.");

        using var scope = factory.Services.CreateScope();
        var dataProtectionProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var sessionKey = UnprotectSessionCookie(dataProtectionProvider, sessionCookieValue);

        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        // Same shape as SessionMiddleware's own DistributedSessionStore.Create(...) call —
        // idle/IO timeouts are irrelevant for an immediate read, isNewSessionKey=false because
        // GetVerifyCode already established (and committed) this session.
        var session = new DistributedSession(
            cache,
            sessionKey,
            idleTimeout: TimeSpan.FromMinutes(20),
            ioTimeout: TimeSpan.FromSeconds(10),
            tryEstablishSession: () => true,
            loggerFactory,
            isNewSessionKey: false);

        await session.LoadAsync();
        if (!session.TryGetValue(VerifyCodeSessionKey, out var raw))
        {
            throw new InvalidOperationException(
                $"#837: session key '{VerifyCodeSessionKey}' was not found after " +
                "GetVerifyCode — either the session key extracted from the cookie didn't match " +
                "the one GetVerifyCode wrote to, or ASP.NET Core's session cookie protocol " +
                "changed (see this class's doc comment: it was reverse-engineered from " +
                "Microsoft.AspNetCore.Session's decompiled source).");
        }

        // WalkingTec.Mvvm.Mvc.SessionExtensions.SetAsync<T> stores JsonSerializer.Serialize(value)
        // as a UTF8 string — for a string value that's the JSON-quoted form (e.g. "AB12"), so
        // deserialize rather than trusting the raw bytes verbatim.
        var json = Encoding.UTF8.GetString(raw);
        return JsonSerializer.Deserialize<string>(json)
            ?? throw new InvalidOperationException("#837: verify_code session value deserialized to null.");
    }

    private static string? ExtractSessionCookieValue(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return null;
        }

        foreach (var header in setCookieHeaders)
        {
            var namePrefix = SessionCookieName + "=";
            if (header.StartsWith(namePrefix, StringComparison.Ordinal))
            {
                var rest = header[namePrefix.Length..];
                var end = rest.IndexOf(';');
                var raw = end >= 0 ? rest[..end] : rest;
                // ASP.NET Core's cookie writer percent-encodes non-ASCII-safe bytes in cookie
                // values (the protected session key routinely contains '+', '/', '='), so this
                // must be undone before the base64 decode below.
                return Uri.UnescapeDataString(raw);
            }
        }

        return null;
    }

    private static string UnprotectSessionCookie(IDataProtectionProvider provider, string protectedCookieValue)
    {
        // Mirrors Microsoft.AspNetCore.Session's internal CookieProtection.Unprotect exactly
        // (decompiled to confirm): purpose string "SessionMiddleware", base64 (padded back to a
        // multiple of 4 — ASP.NET Core strips the '=' padding before setting the cookie), then
        // IDataProtector.Unprotect, then UTF8-decode to the raw 36-character session key GUID.
        var protector = provider.CreateProtector("SessionMiddleware");
        var padded = Pad(protectedCookieValue);
        var protectedBytes = Convert.FromBase64String(padded);
        var sessionKeyBytes = protector.Unprotect(protectedBytes);
        return Encoding.UTF8.GetString(sessionKeyBytes);
    }

    private static string Pad(string text)
    {
        var remainder = (text.Length + 3) % 4;
        var padLength = 3 - remainder;
        return padLength == 0 ? text : text + new string('=', padLength);
    }
}
