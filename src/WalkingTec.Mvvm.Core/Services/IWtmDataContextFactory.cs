#nullable enable

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates DataContext creation logic previously embedded in WTMContext.CreateDC().
    /// All state is passed explicitly so the factory remains stateless and testable.
    /// </summary>
    public interface IWtmDataContextFactory
    {
        /// <summary>
        /// Create a new IDataContext based on the provided parameters.
        /// </summary>
        /// <param name="currentCs">Named connection string key to use (overrides default). Corresponds to WTMContext.CurrentCS.</param>
        /// <param name="currentTenant">Current tenant code (from LoginUserInfo.CurrentTenant). Null for host users.</param>
        /// <param name="refererDomain">Referer header domain for domain-based tenant resolution. Null if not available.</param>
        /// <param name="userCode">Current user's ITCode for auditing. Null for anonymous.</param>
        /// <param name="isLog">If true, uses the "defaultlog" connection if available.</param>
        /// <param name="cskey">Explicit connection string key (overrides currentCs).</param>
        /// <param name="logerror">If true, attaches the logger factory to the DataContext.</param>
        IDataContext? CreateDC(
            string? currentCs = null,
            string? currentTenant = null,
            string? refererDomain = null,
            string? userCode = null,
            bool isLog = false,
            string? cskey = null,
            bool logerror = true);
    }
}
