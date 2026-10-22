using System.Text;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>Makes a value taken from a request fit to be written into a log line.</summary>
    public static class LogText
    {
        /// <summary>
        /// Replaces control characters (line breaks included) by a space and cuts the text to <paramref name="maxLength"/> characters
        /// (an ellipsis marks a cut). <c>null</c> yields an empty string.
        /// </summary>
        public static string Sanitize(string value, int maxLength = 256)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var cut = value.Length > maxLength;
            var sb = new StringBuilder(cut ? maxLength + 3 : value.Length);
            for (var i = 0; i < value.Length && i < maxLength; i++)
            {
                var c = value[i];
                sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
            }
            if (cut)
            {
                sb.Append("...");
            }
            return sb.ToString();
        }
    }
}
