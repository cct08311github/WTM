namespace WalkingTec.Mvvm.Core.Analysis;

[AttributeUsage(AttributeTargets.Property)]
public class DimensionAttribute : Attribute
{
    public string? DisplayName { get; set; }
}
