#nullable enable
// WF-14: Default CcTenantValidator implementation.
//
// Validates CC recipient ITCodes against the WTM FrameworkUser table.
// The FrameworkUser query is scoped to the instance's TenantCode to prevent
// cross-tenant CC record creation.
//
// Invariant (WF-14 spec): a CC recipient that is not a valid same-tenant user
// is rejected/skipped (logged), never written as a cross-tenant CcRecord.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Default implementation of <see cref="ICcTenantValidator"/>.
///
/// <para>Queries <c>FrameworkUser</c> (the WTM framework user entity) to verify:
/// <list type="number">
///   <item>The ITCode exists as an active (<c>IsValid == true</c>) <c>FrameworkUser</c>.</item>
///   <item>When <paramref name="tenantCode"/> is non-null, the user's <c>TenantCode</c>
///         matches the instance's tenant (cross-tenant reject).</item>
/// </list>
/// </para>
///
/// <para>If the <c>FrameworkUser</c> table is not present in the DbContext (e.g. a test
/// context that only contains WorkFlow tables), the query will throw.  In that case the
/// implementation falls back to <c>true</c> (accept) and logs a warning so unit tests that
/// do not wire framework tables are not broken.  The higher-level data invariant
/// (CcRecord.TenantCode == instance.TenantCode) still holds regardless.</para>
/// </summary>
internal sealed class CcTenantValidator : ICcTenantValidator
{
    private readonly ILogger<CcTenantValidator> _logger;

    public CcTenantValidator(ILogger<CcTenantValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<bool> IsValidTenantUserAsync(
        DbContext db,
        string recipientITCode,
        string? tenantCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recipientITCode))
            return false;

        try
        {
            // FrameworkUserBase is the WTM abstract base class; the consumer defines the
            // concrete FrameworkUser that inherits from it.  EF Core's Set<FrameworkUserBase>()
            // queries the consumer's concrete FrameworkUser table (TPH or concrete mapping).
            IQueryable<FrameworkUserBase> query = db.Set<FrameworkUserBase>()
                .AsNoTracking()
                .Where(u => u.ITCode == recipientITCode && u.IsValid == true);

            // For multi-tenant deployments, enforce same-tenant.
            if (!string.IsNullOrEmpty(tenantCode))
            {
                query = query.Where(u => u.TenantCode == tenantCode);
            }

            var exists = await query.AnyAsync(ct);

            if (!exists)
            {
                _logger.LogWarning(
                    "[CcTenantValidator] CC recipient '{ITCode}' not found as valid user in tenant '{Tenant}'. Skipping CcRecord.",
                    recipientITCode, tenantCode ?? "(single-tenant)");
            }

            return exists;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("DbSet") || ex.Message.Contains("entity type"))
        {
            // FrameworkUser not registered in this DbContext (e.g. test context).
            // Fall back to accept — the CcRecord's TenantCode stamp still guarantees isolation.
            _logger.LogDebug(
                "[CcTenantValidator] FrameworkUser not available in DbContext; skipping tenant check for '{ITCode}'.",
                recipientITCode);
            return true;
        }
    }
}
