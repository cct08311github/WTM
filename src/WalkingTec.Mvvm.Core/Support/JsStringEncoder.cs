using System.Text;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Escapes text so it can be embedded inside an existing single- or double-quoted
    /// JavaScript string literal in generated script (including inline event handlers).
    /// The JavaScript engine decodes the escapes back to the original text, so what a
    /// user sees is unchanged, while quotes, backslashes, line breaks and markup in the
    /// value can no longer terminate the literal or the surrounding script element.
    /// Letters, digits, CJK and other non-ASCII characters are left as they are.
    /// </summary>
    public static class JsStringEncoder
    {
        /// <summary>
        /// Returns the content to place INSIDE a quoted JavaScript string literal
        /// (the surrounding quotes are not added). <c>null</c> yields an empty string.
        /// </summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder sb = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                string rep = Replacement(c);
                if (rep == null)
                {
                    sb?.Append(c);
                    continue;
                }
                if (sb == null)
                {
                    sb = new StringBuilder(value.Length + 16);
                    sb.Append(value, 0, i);
                }
                sb.Append(rep);
            }
            return sb == null ? value : sb.ToString();
        }

        private static string Replacement(char c)
        {
            switch (c)
            {
                case '\\': return "\\\\";
                case '\n': return "\\n";
                case '\r': return "\\r";
                case '\t': return "\\t";
                case '\'': return "\\u0027";
                case '"': return "\\u0022";
                case '<': return "\\u003C";
                case '>': return "\\u003E";
                case '&': return "\\u0026";
                case (char)0x2028: return "\\u2028";
                case (char)0x2029: return "\\u2029";
            }
            if (c < 0x20 || c == 0x7F)
            {
                return "\\u" + ((int)c).ToString("X4");
            }
            return null;
        }
    }
}
