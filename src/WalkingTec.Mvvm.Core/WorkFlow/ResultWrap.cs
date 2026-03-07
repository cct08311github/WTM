#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    public class ResultWrap
    {
        public InstanceWrap? WorkflowInstance { get; set; }

        public string? ActivityId { get; set; }
        public Exception? Exception { get; set; }
        public bool Executed { get; set; }
    }
}
