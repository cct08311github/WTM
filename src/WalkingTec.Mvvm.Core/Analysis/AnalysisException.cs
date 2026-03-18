#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis;

/// <summary>
/// Base exception for Analysis Mode errors.
/// Subtypes are used to distinguish HTTP response codes in the controller.
/// </summary>
public class AnalysisException : InvalidOperationException
{
    public AnalysisException(string message) : base(message) { }
    public AnalysisException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a VM type name is not found in the Analysis whitelist.
/// The controller maps this to HTTP 404.
/// </summary>
public class AnalysisVmNotFoundException : AnalysisException
{
    public string VmTypeName { get; }

    public AnalysisVmNotFoundException(string vmTypeName)
        : base($"VM type not registered for analysis: {vmTypeName}")
    {
        VmTypeName = vmTypeName;
    }
}

/// <summary>
/// Thrown when a requested dimension or measure field is not enabled for analysis.
/// The controller maps this to HTTP 400.
/// </summary>
public class AnalysisFieldNotFoundException : AnalysisException
{
    public string FieldName { get; }

    public AnalysisFieldNotFoundException(string fieldName, string kind)
        : base($"{kind} field '{fieldName}' is not enabled for analysis.")
    {
        FieldName = fieldName;
    }
}
