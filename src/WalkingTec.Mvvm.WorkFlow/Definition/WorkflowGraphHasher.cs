#nullable enable
// WF-4: SHA-256 ContentHash computation for canonical GraphJson.
//
// ContentHash = lowercase hex SHA-256 of the UTF-8 bytes of the canonical JSON.
// Used for:
//   • Idempotent republish (same graph → same hash → no new version).
//   • Cache key for compiled routing predicates (WhitelistRoutingEvaluator, WF-11).
//
// Follows the repo ArrayPool pattern from AnalysisQueryEngine.Hashing.cs
// (src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryEngine.Hashing.cs:55-70)
// to avoid heap allocation for the UTF-8 byte buffer.

using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Computes the SHA-256 content hash for a canonical workflow graph JSON string.
///
/// <para>
/// <c>ContentHash = lowercase-hex(SHA-256(UTF-8(canonicalJson)))</c>
/// </para>
/// <para>
/// The canonical JSON MUST be produced by <see cref="WorkflowGraphSerializer.Serialize"/>
/// before calling this method.  Passing non-canonical JSON (e.g. prettified or
/// property-unsorted) will produce a different hash.
/// </para>
/// </summary>
public static class WorkflowGraphHasher
{
    /// <summary>
    /// Compute the 64-character lowercase hex SHA-256 hash of
    /// <paramref name="canonicalJson"/>.
    /// </summary>
    /// <param name="canonicalJson">
    /// Canonical JSON string produced by <see cref="WorkflowGraphSerializer.Serialize"/>.
    /// </param>
    /// <returns>64-character lowercase hex string (256 bits / 32 bytes).</returns>
    public static string ComputeHash(string canonicalJson)
    {
        if (canonicalJson is null) throw new ArgumentNullException(nameof(canonicalJson));

        // Rent a buffer from the pool to avoid a large heap allocation for the
        // UTF-8 encoded bytes — mirrors AnalysisQueryEngine.Hashing.cs:55-70.
        int byteCount = Encoding.UTF8.GetByteCount(canonicalJson);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Encoding.UTF8.GetBytes(canonicalJson, 0, canonicalJson.Length, rented, 0);
            Span<byte> hash = stackalloc byte[32]; // SHA-256 produces 32 bytes
            SHA256.HashData(rented.AsSpan(0, byteCount), hash);
            // Convert.ToHexString returns uppercase; ToLowerInvariant for consistency.
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
