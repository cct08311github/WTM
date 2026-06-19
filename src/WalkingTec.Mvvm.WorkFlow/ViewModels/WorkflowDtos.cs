#nullable enable
// WF-14: Request and response DTOs for WorkFlow controllers.
// WF-21.2: Designer catalog DTOs (IWorkflowDefinitionStore results + WorkflowDesignerController).
//
// Design rules:
//   • Actor identity (ITCode, TenantCode) is NEVER in any request DTO.
//     It is always extracted server-side from Wtm.LoginUserInfo.
//   • [BindNever] applied to any property that must be server-set.
//   • Typed, closed shapes — no free-form dictionaries for branching.
//   • Response DTOs carry enough information for the client to act but
//     never expose internal connection strings, stack traces, or tenant details.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.ViewModels;

// ── Definition responses ──────────────────────────────────────────────────────

/// <summary>Response body for the publish endpoint.</summary>
public sealed record PublishResponseDto(
    bool Success,
    Guid? VersionId,
    int VersionNo,
    string? ContentHash,
    string? Outcome,
    string? Error);

/// <summary>
/// Response body for the validate endpoint.
/// WF-21.3: Additive optional <c>NodeKey</c> field on the response DTO (spec §3.1 / T-DSN-7),
/// lets the UI focus the offending node/form. Null when validation passes or when the error
/// is not node-specific.
/// </summary>
public sealed record ValidateResponseDto(
    bool IsValid,
    string? Error,
    string? NodeKey = null);

// ── Instance requests ─────────────────────────────────────────────────────────

/// <summary>
/// Request body for <c>POST /api/_workflow/instances/start</c>.
///
/// <para><strong>Anti-spoofing:</strong>
/// <c>InitiatorITCode</c> and <c>TenantCode</c> are decorated with
/// <see cref="BindNeverAttribute"/> so they are never bound from the request body.
/// The controller always overwrites them from <c>Wtm.LoginUserInfo</c>.</para>
/// </summary>
public sealed class StartInstanceRequest
{
    /// <summary>FK to the <c>ProcessDefinitionVersion</c> to start an instance from.</summary>
    [Required]
    public Guid DefinitionVersionId { get; set; }

    /// <summary>Optional discriminator for the associated business object type.</summary>
    [StringLength(200)]
    public string? BusinessType { get; set; }

    /// <summary>Optional PK of the associated business object.</summary>
    [StringLength(200)]
    public string? BusinessKey { get; set; }

    /// <summary>JSON-serialized form data for this submission.</summary>
    public string? FormDataJson { get; set; }

    // ── Server-set fields (NEVER bound from the request body) ─────────────────

    /// <summary>
    /// ITCode of the initiator.
    /// Bound NEVER from the client — always set from <c>Wtm.LoginUserInfo.ITCode</c>.
    /// </summary>
    [BindNever]
    public string? InitiatorITCode { get; set; }

    /// <summary>
    /// Tenant isolation code.
    /// Bound NEVER from the client — always set from <c>Wtm.LoginUserInfo.TenantCode</c>.
    /// </summary>
    [BindNever]
    public string? TenantCode { get; set; }
}

/// <summary>Response for instance start.</summary>
public sealed record StartInstanceResponse(
    Guid InstanceId,
    string State,
    string? Error = null);

/// <summary>
/// Request body for <c>POST /api/_workflow/instances/{id}/withdraw</c>.
/// </summary>
public sealed class WithdrawRequest
{
    /// <summary>Optional reason for the withdrawal (stored in the event log).</summary>
    [StringLength(500)]
    public string? Reason { get; set; }

    /// <summary>
    /// Admin override flag.
    /// Only trusted when the caller has the WorkflowAdmin privilege.
    /// Set false by default; the controller validates privilege before passing to engine.
    /// </summary>
    public bool IsAdmin { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/_workflow/instances/{id}/return-to-initiator</c>.
/// </summary>
public sealed class ReturnToInitiatorRequest
{
    /// <summary>Reason for the return (required for audit).</summary>
    [Required]
    [StringLength(500)]
    public string? Reason { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>Generic workflow action response.</summary>
public sealed record WorkflowActionResponse(
    bool Success,
    string ResultCode,
    string? Detail = null);

// ── Task requests ─────────────────────────────────────────────────────────────

/// <summary>Request body for approve/reject actions on a task.</summary>
public sealed class TaskActionRequest
{
    /// <summary>Optional comment (approve) or required reason (reject).</summary>
    [StringLength(1000)]
    public string? Comment { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set from Wtm.LoginUserInfo.ITCode.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>Inbox task summary returned by <c>GET /api/_workflow/tasks/mine</c>.</summary>
public sealed record TaskInboxItem(
    Guid TaskId,
    Guid InstanceId,
    string NodeKey,
    string State,
    string AssigneeITCode,
    DateTime? DueUtc,
    string? Comment);

// ── Task action request DTOs (WF-406) ─────────────────────────────────────────

/// <summary>
/// Request body for <c>POST /api/_workflow/tasks/{id}/add-approver</c> (加签 — WF-18).
///
/// <para><strong>Anti-spoofing:</strong>
/// Actor ITCode is always extracted server-side from <c>Wtm.LoginUserInfo</c>.
/// No client-supplied actor field is accepted.</para>
/// </summary>
public sealed class AddApproverRequest
{
    /// <summary>
    /// ITCodes of the approvers to inject (must be non-empty).
    /// The engine deduplicates the list before insertion.
    /// </summary>
    [Required]
    public List<string> NewApproverITCodes { get; set; } = new();

    /// <summary>
    /// Insertion position relative to the current sequential pointer
    /// (Sequential mode only; ignored for All/Any node modes).
    /// Defaults to <see cref="AddPosition.After"/>.
    /// </summary>
    public AddPosition Position { get; set; } = AddPosition.After;

    /// <summary>Optional reason for the 加签 action (stored in the event log).</summary>
    [StringLength(500)]
    public string? Reason { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/_workflow/tasks/{id}/delegate</c> (转办/委托-now — WF-19).
///
/// <para>Mid-flight delegation: atomically reassigns the actor's pending task to a new delegatee.</para>
/// </summary>
public sealed class DelegateRequest
{
    /// <summary>
    /// ITCode of the new assignee after reassignment (required).
    /// Must not be empty.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string DelegateeITCode { get; set; } = string.Empty;

    /// <summary>Optional FK to a <c>DelegationRule</c> for provenance tracking.</summary>
    public Guid? DelegationRuleId { get; set; }

    /// <summary>Optional reason surfaced in the event log.</summary>
    [StringLength(500)]
    public string? Reason { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/_workflow/tasks/{id}/return-to-node</c> (WF-16).
///
/// <para>Approver returns the flow to an arbitrary upstream Approval node
/// that dominates the current trigger node.</para>
/// </summary>
public sealed class ReturnToNodeRequest
{
    /// <summary>
    /// NodeKey of the target Approval node to return to (required).
    /// Must be a dominator of the trigger node in the process graph.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string TargetNodeKey { get; set; } = string.Empty;

    /// <summary>Optional reason for the return (stored in the event log).</summary>
    [StringLength(500)]
    public string? Reason { get; set; }

    // ── Server-set fields ─────────────────────────────────────────────────────

    /// <summary>Actor ITCode — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? ActorITCode { get; set; }
}

/// <summary>
/// Optional request body for <c>POST /api/_workflow/revoke-delegation/{delegationRuleId}</c> (admin).
///
/// <para>Admin-only: reverts open Pending tasks produced by the given delegation rule
/// back to their original principals.</para>
/// </summary>
public sealed class RevokeDelegationRequest
{
    /// <summary>Optional reason for the revocation (stored in the event log).</summary>
    [StringLength(500)]
    public string? Reason { get; set; }
}

// ── WF-21.2: Designer catalog DTOs ───────────────────────────────────────────

// ── Closed outcome enums ──────────────────────────────────────────────────────

/// <summary>Closed outcome codes for definition head creation.</summary>
public enum CreateDefinitionOutcome
{
    /// <summary>A new definition head was inserted.</summary>
    Created,

    /// <summary>A definition with the same code already exists in this tenant.</summary>
    DuplicateCode,
}

// ── Store result types ────────────────────────────────────────────────────────

/// <summary>One row in the paged definition list.</summary>
public sealed record DefinitionListItem(
    Guid Id,
    string Code,
    string Name,
    string? Category,
    bool IsEnabled,
    int CurrentVersionNo,
    bool HasDraft);

/// <summary>Paged result from <c>IWorkflowDefinitionStore.ListDefinitionsAsync</c>.</summary>
public sealed record DefinitionListResult(
    IReadOnlyList<DefinitionListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>Result from <c>IWorkflowDefinitionStore.CreateDefinitionAsync</c>.</summary>
/// <param name="Outcome">The specific create outcome.</param>
/// <param name="Id">The newly created definition's PK; <c>null</c> on non-success.</param>
/// <param name="Code">The code that was created; <c>null</c> on non-success.</param>
public sealed record CreateDefinitionResult(
    CreateDefinitionOutcome Outcome,
    Guid? Id,
    string? Code);

/// <summary>
/// Envelope returned by <c>GET /api/_workflow/designer/definitions/{code}/graph</c>.
///
/// <para><c>GraphJson</c> is the verbatim stored string — string escaping is byte-faithful
/// on <c>JSON.parse</c>; never re-serialized through the typed model.
/// Null when the definition has no published version yet.</para>
/// </summary>
public sealed record DefinitionGraphEnvelope(
    string? GraphJson,
    Guid? VersionId,
    int VersionNo,
    string? ContentHash,
    int? SchemaVersion,
    DateTime? PublishedAt,
    string? PublishedBy,
    DraftInfo? Draft);

/// <summary>Stub draft info (always null until WF-21.3 introduces <c>ProcessDefinitionDraft</c>).</summary>
public sealed record DraftInfo(
    string? GraphJson,
    string? RowVer,
    string? LastSavedBy,
    DateTime? LastSavedAt,
    string? BaseContentHash);

/// <summary>One item in the version history list.</summary>
public sealed record VersionHistoryItem(
    Guid VersionId,
    int VersionNo,
    string ContentHash,
    int SchemaVersion,
    DateTime? PublishedAt,
    string? PublishedBy,
    bool IsCurrent);

/// <summary>Ordered version history result.</summary>
public sealed record VersionHistoryResult(
    IReadOnlyList<VersionHistoryItem> Versions);

/// <summary>Envelope for a single immutable version's GraphJson.</summary>
public sealed record VersionGraphEnvelope(
    Guid VersionId,
    int VersionNo,
    string GraphJson,
    string ContentHash,
    int SchemaVersion,
    DateTime? PublishedAt,
    string? PublishedBy);

// ── WF-21.3: Draft CRUD outcome enums and result types ───────────────────────

/// <summary>Closed outcome codes for draft save operations.</summary>
public enum SaveDraftOutcome
{
    /// <summary>The draft was saved (created or updated) successfully.</summary>
    Saved,

    /// <summary>The definition was not found in the current tenant scope.</summary>
    DefinitionNotFound,

    /// <summary>
    /// Concurrency conflict: either the draft already exists (on create)
    /// or the RowVersion does not match (on update), or the row is gone after publish.
    /// HTTP mapping: 409 Conflict.
    /// </summary>
    Conflict,
}

/// <summary>Result from <c>IWorkflowDefinitionStore.SaveDraftAsync</c>.</summary>
/// <param name="Outcome">The specific save outcome.</param>
/// <param name="NewRowVersion">
/// The new <c>RowVersion</c> value after a successful save; <c>0</c> on non-success.
/// Must be echoed back to the client as an ETag so the next PUT can supply a fresh If-Match.
/// </param>
public sealed record SaveDraftResult(SaveDraftOutcome Outcome, uint NewRowVersion);

// ── Request DTOs ──────────────────────────────────────────────────────────────

/// <summary>
/// Request body for <c>POST /api/_workflow/designer/definitions</c> (create head).
///
/// <para>Code must match <c>^[A-Za-z0-9_\-\.]{1,64}$</c> (validated by the controller).
/// Duplicate code within a tenant → 409.</para>
///
/// <para>Anti-spoofing: <c>TenantCode</c> and <c>CreatedBy</c> are <c>[BindNever]</c> —
/// always set server-side from <c>Wtm.LoginUserInfo</c>.</para>
/// </summary>
public sealed class CreateDefinitionRequest
{
    /// <summary>Unique business code for the workflow definition.</summary>
    [Required]
    [StringLength(64)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional grouping category.</summary>
    [StringLength(100)]
    public string? Category { get; set; }

    // ── Server-set fields (NEVER bound from the request body) ─────────────────

    /// <summary>Tenant code — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? TenantCode { get; set; }

    /// <summary>Creator ITCode — always server-set, never client-supplied.</summary>
    [BindNever]
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Request body for <c>PUT /api/_workflow/designer/definitions/{code}</c> (metadata update).
///
/// <para>Only Name, Category, and IsEnabled are mutable after creation.
/// Code and TenantCode are immutable once set.</para>
/// </summary>
public sealed class UpdateDefinitionMetadataRequest
{
    /// <summary>Updated human-readable display name.</summary>
    [StringLength(200)]
    public string? Name { get; set; }

    /// <summary>Updated category (null = clear category).</summary>
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>Updated enabled state.</summary>
    public bool? IsEnabled { get; set; }
}

// ── HTTP response DTOs ────────────────────────────────────────────────────────

/// <summary>Response body for <c>POST /api/_workflow/designer/definitions</c>.</summary>
public sealed record CreateDefinitionResponseDto(
    bool Success,
    Guid? Id,
    string? Code,
    string? Error);

/// <summary>
/// Response body for <c>GET /api/_workflow/designer/definitions</c>.
/// Wraps the paged result with an outer envelope for the client.
/// </summary>
public sealed record DefinitionListResponseDto(
    IReadOnlyList<DefinitionListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);
