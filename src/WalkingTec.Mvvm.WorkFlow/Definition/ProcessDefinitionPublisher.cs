#nullable enable
// WF-4: Concrete publish-flow implementation.
// WF-21.1: Additive PublishRawAsync for the designer raw-bytes path (CAS + canonical-raw persistence).
//
// Publish flow (typed path — PublishAsync, unchanged):
//   1. Structural validation (fail-closed; return ValidationFailed result — NOT exception).
//   2. Canonical serialization (deterministic key-sorted JSON).
//   3. SHA-256 ContentHash computation.
//   4. Idempotent hash check: if hash == current version hash → IdempotentNoOp.
//   5. INSERT new ProcessDefinitionVersion (VersionNo = max+1) + UPDATE ProcessDefinition
//      head pointer — wrapped in one EF transaction.
//
// Publish flow (raw path — PublishRawAsync, WF-21.1):
//   1. WorkflowGraphSerializer.Canonicalize(rawJson) — unknown fields + exact numbers survive.
//   2. SHA-256 ContentHash from canonical bytes.
//   3. Typed Deserialize for validation only (WorkflowGraphValidator.Validate).
//   4. Designer gate: schemaVersion != 1 → ValidationFailed (SchemaVersionUnsupported).
//   5. Same idempotent hash check as typed path.
//   6. CAS check: expectedBaseContentHash non-null + mismatch → BaseVersionChanged (409).
//   7. INSERT version storing CANONICAL RAW BYTES (not typed re-serialization), repoint head,
//      delete draft row (once ProcessDefinitionDraft entity exists in WF-21.3).
//
// Threading: scoped service (one instance per request).
// Concurrency: transaction + unique index (TenantCode, DefinitionId, VersionNo) backstop.
//
// FIX-B2: IOptions<WorkFlowOptions> is now injected so WorkflowGraphValidator.Validate
// can enforce the AllowTimerAutoAction gate (check 14g) at publish time.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Default implementation of <see cref="IProcessDefinitionPublisher"/>.
/// Registered as scoped in <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
/// </summary>
public sealed class ProcessDefinitionPublisher : IProcessDefinitionPublisher, IDisposable
{
    private readonly IDataContext _dc;
    private readonly WorkFlowOptions? _options;
    // True when this instance owns the DataContext lifetime (created via IWtmDataContextFactory).
    // False when the caller (tests) passed a pre-existing IDataContext — caller manages lifetime.
    private readonly bool _ownsDc;

    /// <summary>
    /// Production constructor: resolves the real <see cref="IDataContext"/> via the
    /// <see cref="IWtmDataContextFactory"/> (same mechanism as <c>WTMContext.DC</c>).
    /// This publisher owns and disposes the created DataContext on <see cref="Dispose"/>.
    /// </summary>
    public ProcessDefinitionPublisher(IWtmDataContextFactory dcFactory, IOptions<WorkFlowOptions>? options = null)
    {
        if (dcFactory is null) throw new ArgumentNullException(nameof(dcFactory));
        _dc = dcFactory.CreateDC()
            ?? throw new InvalidOperationException(
                "IWtmDataContextFactory.CreateDC() returned null. " +
                "Ensure a valid database connection is configured in appsettings.json.");
        _options = options?.Value;
        _ownsDc = true;
    }

    /// <summary>
    /// Test/direct constructor: accepts a pre-existing <see cref="IDataContext"/> and optional options.
    /// The caller is responsible for the DataContext lifetime — this publisher does NOT dispose it.
    /// </summary>
    public ProcessDefinitionPublisher(IDataContext dc, IOptions<WorkFlowOptions>? options = null)
    {
        _dc = dc ?? throw new ArgumentNullException(nameof(dc));
        _options = options?.Value;
        _ownsDc = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsDc)
            _dc.Dispose();
    }

    /// <inheritdoc/>
    public async Task<PublishResult> PublishAsync(
        string definitionCode,
        WorkflowGraph graph,
        string? publishedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(definitionCode))
            throw new ArgumentException("definitionCode must not be empty.", nameof(definitionCode));
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));

        // ── Step 1: structural validation (fail-closed, Result not exception) ──
        // FIX-B2: pass options so check 14g (AllowTimerAutoAction gate) is enforced at publish time.
        var validation = WorkflowGraphValidator.Validate(graph, _options);
        if (!validation.IsValid)
            return PublishResult.Invalid(validation.Error, validation.ErrorMessage!);

        // ── Step 2 & 3: canonicalize + hash ───────────────────────────────────
        var canonicalJson = WorkflowGraphSerializer.Serialize(graph);
        var contentHash   = WorkflowGraphHasher.ComputeHash(canonicalJson);

        // ── Steps 4 & 5: load definition, hash-check, version-insert ──────────
        // Wrap in a transaction to prevent TOCTOU double-insert under concurrent publish.
        await using var tx = await _dc.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Load the definition head (tenant filter auto-applied by DataContext).
            var definition = await _dc.Set<ProcessDefinition>()
                .FirstOrDefaultAsync(d => d.Code == definitionCode, cancellationToken);

            if (definition == null)
                return PublishResult.NotFound(definitionCode);

            // ── Step 4: idempotent no-op check ─────────────────────────────────
            if (definition.CurrentVersionId.HasValue)
            {
                var currentVersion = await _dc.Set<ProcessDefinitionVersion>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        v => v.ID == definition.CurrentVersionId.Value,
                        cancellationToken);

                if (currentVersion != null &&
                    string.Equals(currentVersion.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    // Same graph — no-op.
                    await tx.RollbackAsync(cancellationToken);
                    return PublishResult.NoOp(
                        currentVersion.ID,
                        currentVersion.VersionNo,
                        contentHash);
                }
            }

            // ── Step 5: compute next VersionNo ─────────────────────────────────
            // max(VersionNo) for this definition, or 0 if none exist.
            var maxVersionNo = await _dc.Set<ProcessDefinitionVersion>()
                .Where(v => v.DefinitionId == definition.ID)
                .Select(v => (int?)v.VersionNo)
                .MaxAsync(cancellationToken) ?? 0;

            var newVersionNo = maxVersionNo + 1;

            // ── Step 5a: INSERT new immutable version row ──────────────────────
            var newVersion = new ProcessDefinitionVersion
            {
                ID            = Guid.NewGuid(),
                TenantCode    = definition.TenantCode,
                DefinitionId  = definition.ID,
                VersionNo     = newVersionNo,
                SchemaVersion = graph.SchemaVersion,
                GraphJson     = canonicalJson,
                ContentHash   = contentHash,
                PublishedAt   = DateTime.UtcNow,
                PublishedBy   = publishedBy,
                IsValid       = true,
            };
            _dc.AddEntity(newVersion);

            // ── Step 5b: repoint definition head to the new version ────────────
            // Use UpdateProperty to issue a narrow UPDATE — avoids a full entity
            // update when only CurrentVersionId changes.
            definition.CurrentVersionId = newVersion.ID;
            _dc.UpdateProperty(definition, d => d.CurrentVersionId!);

            await _dc.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PublishResult.NewVersion(newVersion.ID, newVersionNo, contentHash);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ── WF-21.1: Designer raw-path publish ────────────────────────────────────

    /// <inheritdoc/>
    public async Task<PublishResult> PublishRawAsync(
        string definitionCode,
        string rawGraphJson,
        string? publishedBy,
        string? expectedBaseContentHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(definitionCode))
            throw new ArgumentException("definitionCode must not be empty.", nameof(definitionCode));
        if (string.IsNullOrWhiteSpace(rawGraphJson))
            throw new ArgumentException("rawGraphJson must not be null or empty.", nameof(rawGraphJson));

        // ── Step 1: Canonicalize (unknown fields + exact number literals preserved) ──
        // JsonException on malformed JSON — let it propagate (controller gates this upstream).
        string canonicalJson;
        try
        {
            canonicalJson = WorkflowGraphSerializer.Canonicalize(rawGraphJson);
        }
        catch (JsonException)
        {
            // Re-throw — caller is responsible for size/content-type/well-formedness gates.
            throw;
        }

        // ── Step 2: SHA-256 ContentHash from canonical bytes ──────────────────
        var contentHash = WorkflowGraphHasher.ComputeHash(canonicalJson);

        // ── Step 3: Typed deserialize for validation only ─────────────────────
        // The typed model is used ONLY for WorkflowGraphValidator.Validate; the
        // canonical raw bytes are what gets persisted (fidelity contract).
        WorkflowGraph graphForValidation;
        try
        {
            graphForValidation = WorkflowGraphSerializer.Deserialize(canonicalJson);
        }
        catch (JsonException ex)
        {
            // This should not happen after a successful Canonicalize, but be explicit.
            return PublishResult.Invalid(
                GraphValidationError.None,
                $"Canonical JSON could not be deserialized for validation: {ex.Message}");
        }

        // ── Step 4: Designer gate — schemaVersion != 1 is unsupported ────────
        if (graphForValidation.SchemaVersion != 1)
            return PublishResult.SchemaVersionUnsupported(graphForValidation.SchemaVersion);

        // ── Step 5: Structural validation (fail-closed) ───────────────────────
        var validation = WorkflowGraphValidator.Validate(graphForValidation, _options);
        if (!validation.IsValid)
            return PublishResult.Invalid(validation.Error, validation.ErrorMessage!);

        // ── Steps 6 & 7: DB transaction — idempotent check, CAS, version-insert ──
        await using var tx = await _dc.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Load head (tenant filter auto-applied).
            var definition = await _dc.Set<ProcessDefinition>()
                .FirstOrDefaultAsync(d => d.Code == definitionCode, cancellationToken);

            if (definition == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PublishResult.NotFound(definitionCode);
            }

            string? currentHash = null;

            if (definition.CurrentVersionId.HasValue)
            {
                var currentVersion = await _dc.Set<ProcessDefinitionVersion>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        v => v.ID == definition.CurrentVersionId.Value,
                        cancellationToken);

                if (currentVersion != null)
                {
                    currentHash = currentVersion.ContentHash;

                    // ── Idempotent no-op check (short-circuits CAS, per spec) ──
                    // If the incoming canonical bytes are byte-identical to the current
                    // version, this is always a NoOp — regardless of expectedBaseContentHash.
                    if (string.Equals(currentHash, contentHash, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return PublishResult.NoOp(
                            currentVersion.ID,
                            currentVersion.VersionNo,
                            contentHash);
                    }

                    // ── CAS check — only when expected hash is supplied ────────
                    if (!string.IsNullOrEmpty(expectedBaseContentHash) &&
                        !string.Equals(expectedBaseContentHash, currentHash, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return PublishResult.CasConflict(currentHash);
                    }
                }
            }
            else
            {
                // First publish — no current version.
                // CAS requires null/empty expectedBaseContentHash (first publish).
                // If caller supplied a non-empty hash, it can't match (no current version),
                // so treat as conflict to prevent a "stale first publish" race.
                if (!string.IsNullOrEmpty(expectedBaseContentHash))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PublishResult.CasConflict(string.Empty);
                }
            }

            // ── Compute next VersionNo ────────────────────────────────────────
            var maxVersionNo = await _dc.Set<ProcessDefinitionVersion>()
                .Where(v => v.DefinitionId == definition.ID)
                .Select(v => (int?)v.VersionNo)
                .MaxAsync(cancellationToken) ?? 0;

            var newVersionNo = maxVersionNo + 1;

            // ── INSERT immutable version row with CANONICAL RAW BYTES ─────────
            // Key fidelity point: GraphJson stores the canonical raw bytes (not the
            // typed-path re-serialization), so unknown fields and exact number literals
            // are preserved verbatim in the version history.
            var newVersion = new ProcessDefinitionVersion
            {
                ID            = Guid.NewGuid(),
                TenantCode    = definition.TenantCode,
                DefinitionId  = definition.ID,
                VersionNo     = newVersionNo,
                SchemaVersion = graphForValidation.SchemaVersion,
                GraphJson     = canonicalJson,  // canonical raw bytes — fidelity contract
                ContentHash   = contentHash,
                PublishedAt   = DateTime.UtcNow,
                PublishedBy   = publishedBy,
                IsValid       = true,
            };
            _dc.AddEntity(newVersion);

            // ── Repoint definition head ───────────────────────────────────────
            definition.CurrentVersionId = newVersion.ID;
            _dc.UpdateProperty(definition, d => d.CurrentVersionId!);

            // ── Draft deletion hook (WF-21.3) ─────────────────────────────────
            // ProcessDefinitionDraft entity is introduced in WF-21.3.
            // The deletion happens here, in the same transaction, so that a concurrent
            // stale editor's subsequent PUT /draft sees the row gone and returns 409.
            // No-op until the entity type is registered in ApplyWorkFlowModels.
            await DeleteDraftIfExistsAsync(definition.ID, cancellationToken);

            await _dc.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PublishResult.NewVersion(newVersion.ID, newVersionNo, contentHash);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Delete the draft row for <paramref name="definitionId"/> if one exists.
    /// Called inside the publish transaction so draft-delete and version-insert are atomic.
    ///
    /// <para>WF-21.3: Active — <c>ProcessDefinitionDraft</c> is registered in
    /// <c>ApplyWorkFlowModels</c>.  A concurrent stale editor whose draft was just deleted here
    /// will receive a 409 on its next <c>PUT /draft</c> because the row is gone and
    /// <c>SaveDraftAsync</c> does not create-on-missing under <c>If-Match</c> semantics
    /// (resurrection guard, spec §3.2).</para>
    /// </summary>
    private async Task DeleteDraftIfExistsAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        // WF-21.3: ProcessDefinitionDraft is now registered in ApplyWorkFlowModels.
        var draft = await _dc.Set<ProcessDefinitionDraft>()
            .FirstOrDefaultAsync(d => d.DefinitionId == definitionId, cancellationToken);
        if (draft != null)
            _dc.DeleteEntity(draft);
    }
}
