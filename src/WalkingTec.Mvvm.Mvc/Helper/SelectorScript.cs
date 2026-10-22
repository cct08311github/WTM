using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Builds the pieces of script that the selector dialog view (<c>Views/_Framework/Selector.cshtml</c>) writes from the values of the
    /// request (<c>_DONOT_USE_*</c>) and from <c>ViewBag</c>. Every value is encoded for the place it ends up in, so that quotes, backslashes,
    /// line breaks and markup in a value can neither end the script string it is written into nor the script element. Ordinary values
    /// (identifiers, field names, ordinary text) come out exactly as they were.
    /// </summary>
    public static class SelectorScript
    {
        // a function name or a member path such as "MyPage.onPick"; anything else is not a reference the dialog could call
        private static readonly Regex FunctionPath = new Regex(@"^[\p{L}_$][\p{L}\p{N}_$]*(\.[\p{L}_$][\p{L}\p{N}_$]*)*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedSubmitFuncs = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        /// <summary>
        /// Writes a warning (once for each distinct value) when a selector asks for a submit handler that is not a function name or member path
        /// and is therefore not called, so that the application notices. Returns true when a warning was written.
        /// </summary>
        public static bool ReportDroppedSubmitFunc(Microsoft.Extensions.Logging.ILogger logger, string submitFunc)
        {
            if (string.IsNullOrEmpty(submitFunc) || FunctionReference(submitFunc) != null || logger == null || ReportedSubmitFuncs.Count > 100)
            {
                return false;
            }
            if (ReportedSubmitFuncs.TryAdd(submitFunc, 0) == false)
            {
                return false;
            }
            logger.LogWarning("The selector dialog does not call the submit handler '{SubmitFunc}': only a function name or a member path such as MyPage.onPick is accepted.", LogText.Sanitize(submitFunc, 100));
            return true;
        }

        /// <summary>The text to place INSIDE a quoted JavaScript string literal (the quotes are not added).</summary>
        public static string JsString(object value)
        {
            return JsStringEncoder.Escape(value?.ToString());
        }

        /// <summary>
        /// The text to place inside a quoted HTML attribute value of markup that is itself built inside a quoted JavaScript string literal
        /// (the hidden inputs the dialog adds to the page): first encoded for the attribute, then for the string literal.
        /// </summary>
        public static string HtmlAttributeInJsString(object value)
        {
            return JsStringEncoder.Escape(HtmlAttributeEncoder.Encode(value?.ToString()));
        }

        /// <summary><c>true</c> or <c>false</c>, whatever the value is.</summary>
        public static string Boolean(object value)
        {
            if (value is bool b)
            {
                return b ? "true" : "false";
            }
            return bool.TryParse(value?.ToString(), out var parsed) && parsed ? "true" : "false";
        }

        /// <summary>
        /// The name of the function the dialog calls with the selected rows (<c>SubmitFunc</c> of the selector), when the value is a function
        /// name or a member path; <c>null</c> otherwise, in which case the call is left out. Names that are valid identifiers are returned as they are.
        /// </summary>
        public static string FunctionReference(object value)
        {
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text) || FunctionPath.IsMatch(text) == false)
            {
                return null;
            }
            return text;
        }

        /// <summary>
        /// A JSON text (the rows that are already selected) for the script element: <c>&lt;</c> is written as the equivalent
        /// <c><</c> escape, so that no text inside a value can close the script element or open a comment. The data is the same.
        /// </summary>
        public static string JsonData(object value)
        {
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text))
            {
                return "[]";
            }
            return text.Replace("<", "\\u003C");
        }

        /// <summary>The script that tells the linked field about the new selection (<c>TriggerUrl</c> of the selector).</summary>
        public static string TriggerScript(object triggerUrl, object valName, object selId)
        {
            return $@"
            var url = '{JsString(triggerUrl)}?t=' + new Date().getTime();
            for (var i = 0; i < selected.length; i++) {{
              url += '&id=' +encodeURIComponent(selected[i]['{JsString(valName)}']);
              }}
          ff.ChainChange(url, $('#{JsString(selId)}_Display')[0]);
  ";
        }
    }
}
