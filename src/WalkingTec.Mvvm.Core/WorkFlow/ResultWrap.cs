#nullable enable
// [Elsa removed] ResultWrap previously referenced InstanceWrap (which inherited from Elsa WorkflowInstance).
// Stubbed to allow compilation without Elsa packages.

using System;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    public class ResultWrap
    {
        // Replaced InstanceWrap with object after Elsa removal
        public object? WorkflowInstance { get; set; }

        public string? ActivityId { get; set; }
        public Exception? Exception { get; set; }
        public bool Executed { get; set; }
    }
}
