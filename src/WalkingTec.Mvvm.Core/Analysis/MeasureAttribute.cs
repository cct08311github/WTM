namespace WalkingTec.Mvvm.Core.Analysis;

[AttributeUsage(AttributeTargets.Property)]
public class MeasureAttribute : Attribute
{
    public AggregateFunc AllowedFuncs { get; set; }
    public string? DisplayName { get; set; }
}
