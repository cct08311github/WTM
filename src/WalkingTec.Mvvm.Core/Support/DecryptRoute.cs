#nullable enable
namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// <see cref="Utils.DecryptString(string, string)"/> 對一段輸入實際會走哪一條解密路徑。
    /// <para>
    /// <b>這個列舉回答的是「<c>DecryptString</c> 會怎麼處理這段輸入」，不是「這段密文是用什麼演算法
    /// 加密的」——後者無法單從密文本身判斷。</b> 見 <see cref="Utils.GetDecryptRoute(string?)"/>
    /// 的說明與 <see cref="AesAttempted"/> 的備註。
    /// </para>
    /// </summary>
    public enum DecryptRoute
    {
        /// <summary>
        /// 輸入為 <c>null</c> 或空字串。<see cref="Utils.DecryptString(string, string)"/> 直接回傳
        /// 空字串，不進行任何 Base64 解碼或解密嘗試。
        /// </summary>
        Empty = 0,

        /// <summary>
        /// 輸入不是合法的 Base64（含以空白取代 <c>+</c> 後仍無法解碼的情況）。
        /// <see cref="Utils.DecryptString(string, string)"/> 在解碼階段直接回傳空字串——
        /// <b>不會呼叫 <c>DecryptStringLegacy</c>，也不會嘗試 AES</b>，這與下面兩個路徑都不同。
        /// </summary>
        NotBase64 = 1,

        /// <summary>
        /// Base64 解碼後少於 32 bytes。AES-256-CBC（<c>EncryptString</c> 產生的格式）至少需要
        /// 16 bytes 的 IV 加上 16 bytes 的 PKCS7 區塊，因此這個長度不可能是本框架寫出的 AES 密文。
        /// <see cref="Utils.DecryptString(string, string)"/> 確定性地（不嘗試 AES）直接呼叫舊版
        /// <c>DecryptStringLegacy</c>（DES）解密——這是唯一在方向上「安全」可預期的分類：這裡沒有
        /// 機率性的誤判空間。
        /// </summary>
        LegacyDesShort = 2,

        /// <summary>
        /// Base64 解碼後 &gt;= 32 bytes。<see cref="Utils.DecryptString(string, string)"/> 會先嘗試
        /// AES-256-CBC 解密。
        /// <para>
        /// <b>這個值不代表這段密文是用 AES 加密的，只代表 <c>DecryptString</c> 接下來會先嘗試用 AES
        /// 解密它。</b> 一段用舊版 DES 加密的密文，只要長度剛好 &gt;= 32 bytes，也會落在這一類——
        /// 這正是這整個判準存在的原因：從密文本身無法判斷它究竟是哪種演算法加密的，能判斷的只有
        /// <c>DecryptString</c> 接下來會走哪條路。
        /// </para>
        /// <para>
        /// 最終解密結果是拿到 AES 明文、還是回退到 legacy DES，取決於金鑰是否正確與
        /// <c>LooksLikePlaintext</c> 的啟發式明文檢查是否通過（見該方法備註），是機率性路徑，
        /// 不是確定性判斷。一段長度 &gt;= 32 bytes 的 legacy DES 密文落在這一類，用錯誤金鑰誤判為
        /// AES 明文的機率極低但並非零——這正是下游若把這個值誤讀成「已確認是 AES」會踩到的方向。
        /// </para>
        /// </summary>
        AesAttempted = 3,
    }
}
