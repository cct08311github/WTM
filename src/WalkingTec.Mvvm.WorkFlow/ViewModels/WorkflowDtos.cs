#nullable enable
// WF-14: Request and response DTOs for WorkFlow controllers.
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

/// <summary>Response body for the validate endpoint.</summary>
public sealed record ValidateResponseDto(
    bool IsValid,
    string? Error);

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
