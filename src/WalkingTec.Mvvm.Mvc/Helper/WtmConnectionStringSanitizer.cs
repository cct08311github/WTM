#nullable enable
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Mvc;

/// <summary>
/// 遮蔽字串中常見的連線字串敏感欄位，防止帳密出現在 Serilog 日誌（#559）。
/// 適用於 Serilog Destructure.ByTransforming 及 ILogEventEnricher。
/// </summary>
public static class WtmConnectionStringSanitizer
{
    /// <summary>
    /// 匹配常見連線字串敏感鍵：Password、Pwd、User Id、UID、User。
    /// 格式：key=value，value 以 ; " ' 空白 或字串結尾作為終止符。
    /// 兩段 char class 均排除空白，避免貪婪 match 至散文句子末尾（#580）。
    /// </summary>
    private static readonly Regex _sensitiveKeyPattern = new(
        @"(?i)(password|pwd|user\s+id|uid|user)\s*=\s*[^;""'\s]+",
        RegexOptions.Compiled);

    /// <summary>
    /// 若輸入字串符合連線字串模式（含 '=' 且含敏感關鍵字），則以 [redacted] 遮蔽敏感值並回傳；
    /// 否則回傳原始字串，避免不必要的正規表達式執行。
    /// </summary>
    public static string Sanitize(string input)
    {
        // 快速篩選：不含 '=' 的字串絕不是連線字串
        if (!input.Contains('='))
            return input;

        return _sensitiveKeyPattern.Replace(input, m =>
        {
            // 保留 key= 部分，只遮蔽 value
            var eq = m.Value.IndexOf('=');
            return eq < 0 ? m.Value : m.Value[..(eq + 1)] + "[redacted]";
        });
    }
}
