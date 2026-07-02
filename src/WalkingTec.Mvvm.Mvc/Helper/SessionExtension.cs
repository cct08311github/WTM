using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    public static class SessionExtensions
    {
        public static void Set<T>(this ISession session, string key, T value)
        {
            session.SetString(key, JsonSerializer.Serialize(value));
            // TODO: Set<T> is a sync extension; CommitAsync().GetAwaiter().GetResult() avoids Wait() deadlock
            session.CommitAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async counterpart of <see cref="Set{T}(ISession, string, T)"/>. Serializes
        /// <paramref name="value"/> identically (JSON via System.Text.Json) so values stay
        /// byte-compatible with <see cref="Get{T}(ISession, string)"/>, but commits the
        /// session store without blocking a ThreadPool thread. Prefer this on hot,
        /// pre-authentication paths (e.g. captcha generation) where the session store may
        /// be a distributed backend (Redis / SQL Server).
        /// Issue #535.
        /// </summary>
        public static async Task SetAsync<T>(this ISession session, string key, T value)
        {
            session.SetString(key, JsonSerializer.Serialize(value));
            await session.CommitAsync();
        }

        public static T Get<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default(T) :
                                  JsonSerializer.Deserialize<T>(value);
        }
    }
}
