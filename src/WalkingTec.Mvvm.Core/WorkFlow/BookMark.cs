#nullable enable
// [Elsa removed] BookMark.cs previously contained WtmApproveBookmark (IBookmark)
// and WtmApproveBookmarkProvider (BookmarkProvider<>). Both depended on Elsa.Services.
// Stubbed to allow compilation without Elsa packages.

using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.WorkFlow
{
    /// <summary>
    /// Stub bookmark — preserves the type name for compile compatibility.
    /// </summary>
    public class WtmApproveBookmark
    {
        public WtmApproveBookmark() { }
        public WtmApproveBookmark(string user, string name, string? tag, string entityId)
        {
            User = user;
            Tag = tag;
            Name = name;
            EntityId = entityId;
        }

        public string User { get; set; } = "";
        public string? Tag { get; set; }
        public string Name { get; set; } = "";
        public string EntityId { get; set; } = "";
    }

    /// <summary>
    /// Stub bookmark provider — no longer active.
    /// </summary>
    public class WtmApproveBookmarkProvider
    {
        // No-op after Elsa removal
    }
}
