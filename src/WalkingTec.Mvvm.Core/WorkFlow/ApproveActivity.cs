#nullable enable
// [Elsa removed] This file previously contained WtmApproveActivity which inherited from Elsa.Services.Activity.
// The class is preserved as a stub so that dependent code referencing the type name still compiles.
// All Elsa attributes and base class have been removed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    /// <summary>
    /// Stub replacing the Elsa-based WtmApproveActivity.
    /// All workflow execution logic has been removed.
    /// </summary>
    public class WtmApproveActivity
    {
        public List<string> ApproveUsers { get; set; } = new();
        public List<string> ApproveUsersFullText { get; set; } = new();
        public ICollection<string> ApproveRoles { get; set; } = new List<string>();
        public ICollection<string> ApproveGroups { get; set; } = new List<string>();
        public ICollection<string> ApproveManagers { get; set; } = new List<string>();
        public ICollection<string> ApproveSpecials { get; set; } = new List<string>();
        public string? Remark { get; set; }
        public string? ApprovedBy { get; set; }
        public string? Tag { get; set; }
    }
}
