#nullable enable
// WF-21.2: Scoped definition-catalog store implementation.
// WF-21.3: Draft CRUD (GetDraftAsync, SaveDraftAsync, DeleteDraftAsync) +
//           GetCurrentGraphAsync now populates the Draft envelope field.
//
// Backs the WorkflowDesignerController read/create/metadata operations.
// All DB work happens here — controllers NEVER touch IDataContext directly (WTM red line).
//
// Tenant isolation: all queries go through IDataContext.Set<T>() which has the tenant
// query filter applied automatically by the consumer DataContext (DIRECT PersistPoco/ITenant
// descendant pattern — same guarantee as ProcessDefinitionPublisher).
//
// Version immutability: this class has NO method that updates or deletes a
// ProcessDefinitionVersion row.  The public interface (IWorkflowDefinitionStore) is
// reflection-asserted for this absence in T-DSN-14.
//
// Threading: scoped service (one instance per request, wraps the scoped IDataContext).
//
// IDataContext resolution (WTM pattern):
//   WTM registers IDataContext in DI as NullContext (a stub that throws NotImplementedException).
//   The real DataContext is created transiently via IWtmDataContextFactory.CreateDC() — the same
//   mechanism used by WTMContext.DC getter.  In production AddWtmWorkFlowDesigner() registers
//   this store via a factory lambda that calls IWtmDataContextFactory.CreateDC(), and the store
//   owns the resulting connection (disposing it via IDisposable).  In tests, the direct
//   WorkflowDefinitionStore(IDataContext) constructor is used with a real test context.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

// WF-21.3: ProcessDefinitionDraft referenced in draft CRUD methods below.

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Default implementation of <see cref="IWorkflowDefinitionStore"/>.
/// Registered as scoped by <see cref="ServiceCollectionExtensions.AddWtmWorkFlowDesigner"/>.
/// </summary>
public sealed class WorkflowDefinitionStore : IWorkflowDefinitionStore, IDisposable
{
    private const int MaxPageSize = 200;

    private readonly IDataContext _dc;
    // True when this instance owns the DataContext lifetime (created via IWtmDataContextFactory).
    // False when the caller (tests) passed a pre-existing IDataContext — caller manages lifetime.
    private readonly bool _ownsDc;

    /// <summary>
    /// Production constructor: resolves the real <see cref="IDataContext"/> via the
    /// <see cref="IWtmDataContextFactory"/> (same mechanism as <c>WTMContext.DC</c>).
    /// This store owns and disposes the created DataContext on <see cref="Dispose"/>.
    /// </summary>
    public WorkflowDefinitionStore(IWtmDataContextFactory dcFactory)
    {
        if (dcFactory is null) throw new ArgumentNullException(nameof(dcFactory));
        _dc = dcFactory.CreateDC()
            ?? throw new InvalidOperationException(
                "IWtmDataContextFactory.CreateDC() returned null. " +
                "Ensure a valid database connection is configured in appsettings.json.");
        _ownsDc = true;
    }

    /// <summary>
    /// Test/direct constructor: accepts a pre-existing <see cref="IDataContext"/>.
    /// The caller is responsible for the DataContext lifetime — this store does NOT dispose it.
    /// </summary>
    public WorkflowDefinitionStore(IDataContext dc)
    {
        _dc = dc ?? throw new ArgumentNullException(nameof(dc));
        _ownsDc = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsDc)
            _dc.Dispose();
    }

    // ── Head catalog ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DefinitionListResult> ListDefinitionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamp inputs.
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var query = _dc.Set<ProcessDefinition>()
            .AsNoTracking()
            // Include the current version for the VersionNo join.
            .Include(d => d.CurrentVersion);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(d => d.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // WF-21.3: Load draft existence for the current page.
        // Single extra query to avoid N+1; result set is small (max 200 rows/page).
        var pageIds = rows.Select(d => d.ID).ToList();
        var definitionIdsWithDraft = await _dc.Set<ProcessDefinitionDraft>()
            .AsNoTracking()
            .Where(dr => pageIds.Contains(dr.DefinitionId))
            .Select(dr => dr.DefinitionId)
            .ToListAsync(cancellationToken);
        var draftSet = new System.Collections.Generic.HashSet<Guid>(definitionIdsWithDraft);

        var items = rows
            .Select(d => new DefinitionListItem(
                Id:               d.ID,
                Code:             d.Code,
                Name:             d.Name,
                Category:         d.Category,
                IsEnabled:        d.IsEnabled,
                CurrentVersionNo: d.CurrentVersion?.VersionNo ?? 0,
                HasDraft:         draftSet.Contains(d.ID)))
            .ToList();

        return new DefinitionListResult(items, totalCount, page, pageSize);
    }

    /// <inheritdoc/>
    public async Task<CreateDefinitionResult> CreateDefinitionAsync(
        CreateDefinitionRequest request,
        string? tenantCode,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // Check for duplicate code in this tenant (tenant filter auto-applied).
        var exists = await _dc.Set<ProcessDefinition>()
            .AnyAsync(d => d.Code == request.Code, cancellationToken);

        if (exists)
            return new CreateDefinitionResult(CreateDefinitionOutcome.DuplicateCode, null, null);

        var head = new ProcessDefinition
        {
            ID            = Guid.NewGuid(),
            Code          = request.Code,
            Name          = request.Name,
            Category      = request.Category,
            IsEnabled     = true,
            TenantCode    = tenantCode,
            // CurrentVersionId is null until first publish — correct initial state.
            CurrentVersionId = null,
            IsValid       = true,
            CreateBy      = createdBy,
            CreateTime    = DateTime.UtcNow,
        };

        _dc.AddEntity(head);
        await _dc.SaveChangesAsync(cancellationToken);

        return new CreateDefinitionResult(CreateDefinitionOutcome.Created, head.ID, head.Code);
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateDefinitionMetadataAsync(
        string code,
        UpdateDefinitionMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));
        if (request is null) throw new ArgumentNullException(nameof(request));

        var definition = await _dc.Set<ProcessDefinition>()
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return false;

        // Apply values then mark the specific properties as modified.
        // UpdateProperty uses EF change tracking to issue a narrow UPDATE.
        if (request.Name is not null)
        {
            definition.Name = request.Name;
            _dc.UpdateProperty(definition, d => d.Name);
        }

        if (request.IsEnabled.HasValue)
        {
            definition.IsEnabled = request.IsEnabled.Value;
            _dc.UpdateProperty(definition, d => d.IsEnabled);
        }

        if (request.Category is not null)
        {
            definition.Category = request.Category;
            _dc.UpdateProperty(definition, d => d.Category!);
        }

        await _dc.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ── Version access (read-only) ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DefinitionGraphEnvelope?> GetCurrentGraphAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));

        // Load head + include the current version (single left-join query).
        var definition = await _dc.Set<ProcessDefinition>()
            .AsNoTracking()
            .Include(d => d.CurrentVersion)
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return null;

        var cv = definition.CurrentVersion;

        // WF-21.3: Load draft info if one exists for this definition.
        DraftInfo? draftInfo = null;
        var draft = await _dc.Set<ProcessDefinitionDraft>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DefinitionId == definition.ID, cancellationToken);
        if (draft is not null)
        {
            draftInfo = new DraftInfo(
                GraphJson:       draft.GraphJson,
                RowVer:          draft.RowVersion.ToString(),
                LastSavedBy:     draft.LastSavedBy,
                LastSavedAt:     draft.LastSavedAt,
                BaseContentHash: draft.BaseContentHash);
        }

        return new DefinitionGraphEnvelope(
            GraphJson:    cv?.GraphJson,
            VersionId:    cv?.ID,
            VersionNo:    cv?.VersionNo ?? 0,
            ContentHash:  cv?.ContentHash,
            SchemaVersion: cv?.SchemaVersion,
            PublishedAt:  cv?.PublishedAt,
            PublishedBy:  cv?.PublishedBy,
            Draft:        draftInfo);
    }

    /// <inheritdoc/>
    public async Task<VersionHistoryResult?> GetVersionHistoryAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));

        // Resolve the definition to get its ID and current version pointer.
        var definition = await _dc.Set<ProcessDefinition>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return null;

        // Load all versions for this definition, newest first.
        var versions = await _dc.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .Where(v => v.DefinitionId == definition.ID)
            .OrderByDescending(v => v.VersionNo)
            .ToListAsync(cancellationToken);

        var items = versions
            .Select(v => new VersionHistoryItem(
                VersionId:    v.ID,
                VersionNo:    v.VersionNo,
                ContentHash:  v.ContentHash,
                SchemaVersion: v.SchemaVersion,
                PublishedAt:  v.PublishedAt,
                PublishedBy:  v.PublishedBy,
                IsCurrent:    v.ID == definition.CurrentVersionId))
            .ToList();

        return new VersionHistoryResult(items);
    }

    /// <inheritdoc/>
    public async Task<VersionGraphEnvelope?> GetVersionGraphAsync(
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        // Tenant filter is applied automatically — cross-tenant ID behaves as 404.
        var version = await _dc.Set<ProcessDefinitionVersion>()
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ID == versionId, cancellationToken);

        if (version is null)
            return null;

        return new VersionGraphEnvelope(
            VersionId:    version.ID,
            VersionNo:    version.VersionNo,
            GraphJson:    version.GraphJson,
            ContentHash:  version.ContentHash,
            SchemaVersion: version.SchemaVersion,
            PublishedAt:  version.PublishedAt,
            PublishedBy:  version.PublishedBy);
    }

    // ── WF-21.3: Draft CRUD ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DraftInfo?> GetDraftAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));

        // Resolve the definition head to get its ID (tenant filter auto-applied).
        var definition = await _dc.Set<ProcessDefinition>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return null;

        // Find the draft for this definition.
        var draft = await _dc.Set<ProcessDefinitionDraft>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DefinitionId == definition.ID, cancellationToken);

        if (draft is null)
            return null;

        return new DraftInfo(
            GraphJson:       draft.GraphJson,
            RowVer:          draft.RowVersion.ToString(),
            LastSavedBy:     draft.LastSavedBy,
            LastSavedAt:     draft.LastSavedAt,
            BaseContentHash: draft.BaseContentHash);
    }

    /// <inheritdoc/>
    public async Task<SaveDraftResult> SaveDraftAsync(
        string code,
        string graphJson,
        string? baseContentHash,
        uint expectedRowVersion,
        bool create,
        string? savedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));
        if (string.IsNullOrWhiteSpace(graphJson)) throw new ArgumentException("graphJson must not be empty.", nameof(graphJson));

        // Resolve the definition head (tenant filter auto-applied).
        var definition = await _dc.Set<ProcessDefinition>()
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return new SaveDraftResult(SaveDraftOutcome.DefinitionNotFound, 0);

        if (create)
        {
            // If-None-Match:* semantics — insert a new draft.
            // Must not already have a draft.
            var existing = await _dc.Set<ProcessDefinitionDraft>()
                .FirstOrDefaultAsync(d => d.DefinitionId == definition.ID, cancellationToken);

            if (existing is not null)
            {
                // Draft already exists — conflict (client should use If-Match to update).
                return new SaveDraftResult(SaveDraftOutcome.Conflict, 0);
            }

            var draft = new ProcessDefinitionDraft
            {
                ID               = Guid.NewGuid(),
                TenantCode       = definition.TenantCode,
                DefinitionId     = definition.ID,
                GraphJson        = graphJson,
                BaseContentHash  = baseContentHash,
                RowVersion       = 1,     // initial version
                LastSavedBy      = savedBy,
                LastSavedAt      = DateTime.UtcNow,
                IsValid          = true,
                CreateTime       = DateTime.UtcNow,
            };

            _dc.AddEntity(draft);
            await _dc.SaveChangesAsync(cancellationToken);

            return new SaveDraftResult(SaveDraftOutcome.Saved, draft.RowVersion);
        }
        else
        {
            // FIX-A4: Guard-in-WHERE CAS — single-statement atomic update.
            // A read-compare-write (load → check RowVersion → save) has a TOCTOU race:
            // two concurrent saves can both read the same row, both see a matching
            // RowVersion, and both succeed — the second overwriting the first silently.
            //
            // Solution: WHERE DefinitionId = @id AND RowVersion = @expected.
            // rows == 0  →  either the row was deleted (post-publish) or another write
            //               already incremented RowVersion.  Both cases return 409.
            // rows == 1  →  exactly one writer won; return the new RowVersion.
            var nowUtc        = DateTime.UtcNow;
            var newRowVersion = expectedRowVersion + 1;
            var finalHash     = baseContentHash;   // resolved below if null

            // We need the current hash to preserve it when the caller supplies null.
            // Read the current row cheaply (projection only) — this is safe even in the
            // TOCTOU sense because the subsequent ExecuteUpdateAsync is the real guard.
            var existing = await _dc.Set<ProcessDefinitionDraft>()
                .AsNoTracking()
                .Where(d => d.DefinitionId == definition.ID && d.RowVersion == expectedRowVersion)
                .Select(d => new { d.BaseContentHash })
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
            {
                // Row gone or RowVersion already incremented — return 409.
                return new SaveDraftResult(SaveDraftOutcome.Conflict, 0);
            }

            if (finalHash is null)
            {
                finalHash = existing.BaseContentHash;
            }

            // Single-statement guarded UPDATE: WHERE DefinitionId=@id AND RowVersion=@expected.
            // rows == 0 → concurrent write already incremented RowVersion → 409 Conflict.
            // rows == 1 → this writer won the CAS.
            var rows = await _dc.Set<ProcessDefinitionDraft>()
                .Where(d => d.DefinitionId == definition.ID && d.RowVersion == expectedRowVersion)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.GraphJson,       graphJson)
                        .SetProperty(d => d.BaseContentHash, finalHash)
                        .SetProperty(d => d.RowVersion,      newRowVersion)
                        .SetProperty(d => d.LastSavedBy,     savedBy)
                        .SetProperty(d => d.LastSavedAt,     nowUtc),
                    cancellationToken);

            if (rows == 0)
            {
                // Another concurrent save already won the CAS.
                return new SaveDraftResult(SaveDraftOutcome.Conflict, 0);
            }

            return new SaveDraftResult(SaveDraftOutcome.Saved, newRowVersion);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteDraftAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code must not be empty.", nameof(code));

        // Resolve the definition head.
        var definition = await _dc.Set<ProcessDefinition>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Code == code, cancellationToken);

        if (definition is null)
            return false;   // definition not found → 404

        // Find and delete the draft (idempotent — no-op if no draft exists).
        var draft = await _dc.Set<ProcessDefinitionDraft>()
            .FirstOrDefaultAsync(d => d.DefinitionId == definition.ID, cancellationToken);

        if (draft is not null)
        {
            _dc.DeleteEntity(draft);
            await _dc.SaveChangesAsync(cancellationToken);
        }

        return true;    // definition was found (draft may not have existed)
    }
}
