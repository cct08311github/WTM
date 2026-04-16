#nullable enable
namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Closed enumeration of declarative action types understood by the
    /// client-side <c>ff.DispatchAction</c> whitelist. Introduced in issue
    /// #806 as the typed successor to the raw-string <c>WtmAction.Type</c>
    /// from #789 Phase 3C — unknown types are rejected at serialization
    /// time instead of silently logged by the JS dispatcher.
    /// </summary>
    /// <remarks>
    /// Serialized to JSON as camelCase strings — the serializer options on
    /// <see cref="WtmActionResult"/> register a
    /// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
    /// with <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/> that
    /// maps these PascalCase members to the literal strings the JS
    /// dispatcher in <c>framework_layui.js</c> switches on.
    /// </remarks>
    public enum WtmActionType
    {
        /// <summary>Unset — indicates a WtmAction constructed but not populated. Serializes to "none" and is ignored by the client dispatcher.</summary>
        None = 0,

        Alert,
        Message,
        CloseDialog,
        RefreshGrid,
        RefreshPage,
        Reload,
        Redirect,
    }
}
