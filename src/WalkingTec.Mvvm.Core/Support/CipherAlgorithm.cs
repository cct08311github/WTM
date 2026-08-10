#nullable enable
namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 選擇 <see cref="Utils.EncryptString(string, string, CipherAlgorithm)"/> 使用的對稱加密演算法。
    /// 上游預設一律為 <see cref="Aes256Cbc"/>；<see cref="LegacyDes"/> 是明示 opt-in 的例外，
    /// 僅供需要保留回滾到 8.x 世代能力的下游部署使用（見 issue #1086）。
    /// </summary>
    public enum CipherAlgorithm
    {
        /// <summary>
        /// AES-256-CBC，PKCS7 填充，隨機 IV 前置於密文。目前 <c>Utils.EncryptString(string, string)</c>
        /// 的既有行為，也是本框架的預設與建議演算法。
        /// </summary>
        Aes256Cbc = 0,

        /// <summary>
        /// 舊版 DES 加密（8.x 世代格式）。DES 已被視為不安全的加密演算法，僅供需要回滾到 8.x
        /// 世代部署的情境明示選用，不得用於加密新資料。
        /// </summary>
        LegacyDes = 1
    }
}
