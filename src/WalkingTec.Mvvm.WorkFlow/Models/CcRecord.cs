#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.WorkFlow.Models;

/// <summary>
/// 抄送 (CC) receipt.  Non-blocking by construction — creating a CC record and
/// advancing the process happen inside the same engine transaction.
/// Recipients may mark-read or comment but cannot approve or reject.
/// </summary>
/// <remarks>
/// DIRECT descendant of <see cref="BasePoco"/> and <see cref="ITenant"/>.
/// </remarks>
public class CcRecord : BasePoco, ITenant
{
    /// <inheritdoc/>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>FK to the <see cref="ProcessInstance"/> this CC belongs to.</summary>
    [Required]
    public Guid InstanceId { get; set; }

    /// <summary>Navigation to the owning instance.</summary>
    public ProcessInstance? Instance { get; set; }

    /// <summary>Key of the node that triggered this CC (matches <c>nodeKey</c> in graph).</summary>
    [StringLength(100)]
    public string? NodeKey { get; set; }

    /// <summary>ITCode of the CC recipient.</summary>
    [Required]
    [StringLength(50)]
    public string RecipientITCode { get; set; } = string.Empty;

    /// <summary>When this CC was triggered.</summary>
    public CcTrigger Trigger { get; set; }

    /// <summary>UTC timestamp when the CC notification was sent.</summary>
    public DateTime? SentAtUtc { get; set; }

    /// <summary>UTC timestamp when the recipient marked this CC as read.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>Optional comment left by the recipient.</summary>
    public string? Comment { get; set; }
}
