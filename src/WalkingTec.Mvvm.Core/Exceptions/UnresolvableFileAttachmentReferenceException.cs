#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Exceptions
{
    /// <summary>
    /// Issue #824: thrown by <see cref="WalkingTec.Mvvm.Core.FileAttachmentSaveChangesGuard"/>
    /// when an Added or Modified entity carries a <c>FileAttachment</c>-typed foreign key whose
    /// posted id does not resolve to a <c>FileAttachment</c> row visible under the caller's own
    /// tenant scope — and is not itself being inserted (state <c>Added</c>) in the very same
    /// unit of work.
    /// <para>
    /// Deliberately does <b>not</b> derive from
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>: existing catch blocks
    /// around <c>SaveChanges</c>/<c>SaveChangesAsync</c> across this codebase (and in downstream
    /// apps) treat a <c>DbUpdateException</c> as a transient store failure — worth retrying,
    /// worth logging as "the database rejected this", not worth treating as "this request must
    /// never succeed". A rejected #824 write is a SECURITY decision, not a store failure, and
    /// must not be silently classified (or retried) as one.
    /// </para>
    /// <para>
    /// The message deliberately says only "not resolvable in current scope" and never states
    /// whether the id exists at all, or under a different tenant — either phrasing would confirm
    /// to the caller that the id exists somewhere, which is exactly the fact #824 exists to keep
    /// from leaking.
    /// </para>
    /// </summary>
    public class UnresolvableFileAttachmentReferenceException : InvalidOperationException
    {
        /// <summary>
        /// CLR type of the entity that carried the rejected foreign key. <see langword="null"/>
        /// only for <see cref="ForResolutionFailure"/>, where the batched resolution QUERY itself
        /// failed before any single id could be evaluated — see that factory's doc comment.
        /// </summary>
        public Type? EntityType { get; }

        /// <summary>
        /// Name of the foreign key scalar property that was rejected. <see langword="null"/> only
        /// for <see cref="ForResolutionFailure"/> — see <see cref="EntityType"/>.
        /// </summary>
        public string? PropertyName { get; }

        /// <summary>
        /// The posted <c>FileAttachment</c> id that could not be resolved. <see langword="null"/>
        /// only for <see cref="ForResolutionFailure"/> — see <see cref="EntityType"/>.
        /// </summary>
        public Guid? Id { get; }

        /// <summary>
        /// A specific FK on a specific entity was posted with an id that does not resolve.
        /// </summary>
        public UnresolvableFileAttachmentReferenceException(Type entityType, string propertyName, Guid id)
            : base($"FileAttachment reference on {entityType.Name}.{propertyName} is not resolvable in current scope.")
        {
            EntityType = entityType;
            PropertyName = propertyName;
            Id = id;
        }

        private UnresolvableFileAttachmentReferenceException(string message, Exception? innerException)
            : base(message, innerException)
        {
            EntityType = null;
            PropertyName = null;
            Id = null;
        }

        /// <summary>
        /// Issue #824 / #828 precedent (<c>BaseCRUDVM.RejectWholeRequestForResolutionFailure</c>):
        /// a resolution QUERY failure (timeout, transient connection loss, a future provider
        /// change, ...) is not the same fact as "none of these ids resolve" — it must still
        /// reject the save (never narrow to an ambiguous empty resolved set), but there is no
        /// single offending id to name because the query never completed.
        /// </summary>
        public static UnresolvableFileAttachmentReferenceException ForResolutionFailure(Exception? inner)
        {
            return new UnresolvableFileAttachmentReferenceException(
                "One or more FileAttachment references could not be verified in current scope; rejecting the save.",
                inner);
        }
    }
}
