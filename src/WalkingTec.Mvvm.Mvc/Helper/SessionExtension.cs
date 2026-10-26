using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WalkingTec.Mvvm.Mvc
{
    public static class SessionExtensions
    {
        public static void Set<T>(this ISession session, string key, T value)
        {
            session.SetString(key, JsonSerializer.Serialize(value));
            session.CommitAsync().Wait();
        }

        /// <summary>
        /// Same as <see cref="Set{T}"/>, but the commit is awaited instead of blocking the thread.
        /// </summary>
        public static async Task SetAsync<T>(this ISession session, string key, T value, CancellationToken cancellationToken = default)
        {
            session.SetString(key, JsonSerializer.Serialize(value));
            await session.CommitAsync(cancellationToken);
        }

        public static T Get<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default(T) :
                                  JsonSerializer.Deserialize<T>(value);
        }
    }
}
