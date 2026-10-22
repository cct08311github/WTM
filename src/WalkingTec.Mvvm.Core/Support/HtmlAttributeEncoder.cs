using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Encodes text so it can be placed inside a quoted HTML attribute value (single or double
    /// quoted) of hand-built markup. The browser decodes the entities back to the original text,
    /// so what a user sees and what a form posts is unchanged, while quotes and markup in the value
    /// can no longer end the attribute or the tag.
    /// Only <c>&amp; &lt; &gt; " '</c> are encoded; letters, digits, CJK and every other character are
    /// left as they are, so ordinary values keep exactly the same bytes (unlike
    /// <c>HtmlEncoder.Default</c>, which turns non-ASCII text into numeric entities).
    /// </summary>
    public static class HtmlAttributeEncoder
    {
        /// <summary>
        /// Returns the content to place INSIDE a quoted attribute value (the surrounding quotes are not
        /// added). <c>null</c> yields an empty string.
        /// </summary>
        public static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder sb = null;
            for (int i = 0; i < value.Length; i++)
            {
                string rep = Replacement(value[i]);
                if (rep == null)
                {
                    sb?.Append(value[i]);
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

        /// <summary>
        /// Encodes <paramref name="value"/> exactly once, for text that may already be HTML-encoded (for example a
        /// value the model binder encoded when it was saved, <c>UIOptions.EncodeHtmlContent</c>): the text is
        /// HTML-decoded one level first and then encoded with <see cref="Encode(string)"/>, so an already-encoded
        /// value is not encoded a second time, while the output is always the result of <see cref="Encode(string)"/>
        /// and therefore never contains a raw <c>&lt; &gt; " '</c> or an entity the encoder did not write itself,
        /// whatever the input mixes. The decode is a single level, so text that was typed as an entity
        /// (stored as <c>&amp;amp;lt;</c>) still displays as typed.
        /// </summary>
        public static string EncodeOnce(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return Encode(WebUtility.HtmlDecode(value));
        }

        /// <summary>
        /// Same as <see cref="Encode(string)"/> for any value; it is converted to text the way string
        /// interpolation does (current culture), so replacing <c>{value}</c> by
        /// <c>{HtmlAttributeEncoder.Encode(value)}</c> does not change ordinary output.
        /// </summary>
        public static string Encode(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            if (value is string s)
            {
                return Encode(s);
            }
            string text = value is IFormattable f ? f.ToString(null, CultureInfo.CurrentCulture) : value.ToString();
            return Encode(text);
        }

        private static string Replacement(char c)
        {
            switch (c)
            {
                case '&': return "&amp;";
                case '<': return "&lt;";
                case '>': return "&gt;";
                case '"': return "&quot;";
                case '\'': return "&#39;";
            }
            return null;
        }
    }
}
