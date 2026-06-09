#nullable enable
// WF-14: CC recipient tenant validation interface.
//
// The CcHandler (WF-13) writes CcRecord rows but deferred the FrameworkUser tenant lookup
// to WF-14 because it requires a database query against the framework user table.
//
// This interface is the seam that CcHandler calls to validate that a resolved CC recipient
// ITCode is a real user in the instance's tenant BEFORE writing the CcRecord.
//
// Invariant: a CC recipient whose ITCode cannot be verified as a real same-tenant user
// is rejected/skipped (logged) and NEVER written as a CcRecord.
// This ensures cross-tenant CC leaks are impossible at the data level.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Validates that a CC recipient ITCode refers to a real user in the given tenant.
///
/// <para>Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>
/// (WF-14).  The default implementation queries <c>FrameworkUser</c> (the WTM framework
/// user table) to verify the ITCode exists and belongs to the specified tenant.</para>
///
/// <para>When the framework user table is unavailable (e.g. unit-test context without
/// framework tables), the default implementation returns <c>true</c> (accept) to prevent
/// test-harness breakage — the higher-level engine tenant-isolation invariant still holds
/// because all CcRecords are stamped with the INSTANCE's TenantCode.</para>
/// </summary>
public interface ICcTenantValidator
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="recipientITCode"/> is a real, active user
    /// in <paramref name="tenantCode"/>; <c>false</c> when the user does not exist or
    /// belongs to a different tenant.
    ///
    /// <para>For single-tenant deployments where <paramref name="tenantCode"/> is null,
    /// this method validates that the user exists and is active (tenant check skipped).</para>
    /// </summary>
    /// <param name="db">The DbContext already in scope for the current engine operation.</param>
    /// <param name="recipientITCode">ITCode of the CC recipient to validate.</param>
    /// <param name="tenantCode">Tenant code of the process instance; null for single-tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsValidTenantUserAsync(
        DbContext db,
        string recipientITCode,
        string? tenantCode,
        CancellationToken ct = default);
}
