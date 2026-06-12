#nullable enable
// WF-4: Concrete publish-flow implementation.
//
// Publish flow:
//   1. Structural validation (fail-closed; return ValidationFailed result — NOT exception).
//   2. Canonical serialization (deterministic key-sorted JSON).
//   3. SHA-256 ContentHash computation.
//   4. Idempotent hash check: if hash == current version hash → IdempotentNoOp.
//   5. INSERT new ProcessDefinitionVersion (VersionNo = max+1) + UPDATE ProcessDefinition
//      head pointer — wrapped in one EF transaction.
//
// Threading: scoped service (one instance per request).
// Concurrency: wrap steps 4–5 in a transaction to prevent a TOCTOU race where two
// concurrent publishes both conclude "new version needed" and double-insert.
// EF's SaveChangesAsync within a BeginTransactionAsync block is the correct WTM pattern;
// the unique index on (TenantCode, DefinitionId, VersionNo) in ApplyWorkFlowModels
// acts as a DB-level backstop should two transactions slip through.
//
// FIX-B2: IOptions<WorkFlowOptions> is now injected so WorkflowGraphValidator.Validate
// can enforce the AllowTimerAutoAction gate (check 14g) at publish time.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Default implementation of <see cref="IProcessDefinitionPublisher"/>.
/// Registered as scoped in <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/>.
/// </summary>
public sealed class ProcessDefinitionPublisher : IProcessDefinitionPublisher
{
    private readonly IDataContext _dc;
    private readonly WorkFlowOptions? _options;

    /// <summary>Inject the scoped <see cref="IDataContext"/> and optional <see cref="WorkFlowOptions"/>.</summary>
    /// <param name="dc">EF DataContext (required).</param>
    /// <param name="options">
    /// WorkFlow runtime options.  When present, publish-time validation enforces the
    /// <c>AllowTimerAutoAction</c> gate (check 14g of <see cref="WorkflowGraphValidator"/>).
    /// When absent (e.g. from a test or legacy DI setup), the gate check is skipped.
    /// </param>
    public ProcessDefinitionPublisher(IDataContext dc, IOptions<WorkFlowOptions>? options = null)
    {
        _dc = dc ?? throw new ArgumentNullException(nameof(dc));
        _options = options?.Value;
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
}
