#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Issue #1028 — pins the field set of <c>WtmFileProvider._fileMetadataProjection</c>, the
    /// single shared <see cref="Expression{Func}"/> both <c>GetFileCore</c> and
    /// <c>DeleteFileCore</c> use to rebuild a lightweight <see cref="FileAttachment"/> without
    /// loading <see cref="FileAttachment.FileData"/>.
    ///
    /// <para>
    /// Before this fix, each method hand-maintained its own <c>Select(x =&gt; new
    /// FileAttachment { ... })</c> field list; both lists happened to already agree on every
    /// field they DID include, but both had independently omitted
    /// <see cref="FileAttachment.HandlerInfo"/> — proof the two lists could drift (and had
    /// already drifted from the full set of relevant fields) without either method's own tests
    /// noticing, because nothing pinned the field SET. This test reflects over the shared
    /// expression's <see cref="MemberInitExpression"/> and asserts the set of assigned member
    /// names, so adding a column to <see cref="FileAttachment"/> without deciding whether it
    /// belongs in this projection fails loudly here instead of silently reproducing the #1028
    /// defect for the next field.
    /// </para>
    ///
    /// <para>
    /// Deliberately a SET assertion (<see cref="CollectionAssert.AreEquivalent"/>), not a count:
    /// a count-only assertion (e.g. <c>bindings.Count == 9</c>) passes just as well when one
    /// expected field is swapped for a different, unexpected one — it would not have caught
    /// #1028, where the count was already "right" (8 fields) but the wrong 8.
    /// </para>
    /// </summary>
    [TestClass]
    public class WtmFileProviderProjectionFieldSetTests1028
    {
        /// <summary>
        /// The complete, intentional field set as of #1028. Update this list — deliberately, in
        /// the same change that updates <c>WtmFileProvider._fileMetadataProjection</c> — when a
        /// field is intentionally added to or removed from the shared projection. Do NOT update
        /// it to make a failing test pass without reading why <c>_fileMetadataProjection</c>
        /// changed.
        /// </summary>
        private static readonly HashSet<string> ExpectedFields = new(StringComparer.Ordinal)
        {
            nameof(FileAttachment.ID),
            nameof(FileAttachment.ExtraInfo),
            nameof(FileAttachment.FileExt),
            nameof(FileAttachment.FileName),
            nameof(FileAttachment.Length),
            nameof(FileAttachment.Path),
            nameof(FileAttachment.SaveMode),
            nameof(FileAttachment.UploadTime),
            nameof(FileAttachment.HandlerInfo),
        };

        private static Expression<Func<FileAttachment, FileAttachment>> GetProjection()
        {
            var field = typeof(WtmFileProvider).GetField(
                "_fileMetadataProjection",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(field,
                "#1028: WtmFileProvider must declare a private static '_fileMetadataProjection' " +
                "field — if this field was renamed, update this test's reflection target " +
                "alongside the rename, do not delete the pin.");

            var value = field!.GetValue(null);
            Assert.IsInstanceOfType(value, typeof(Expression<Func<FileAttachment, FileAttachment>>),
                "#1028: '_fileMetadataProjection' must remain an Expression<Func<FileAttachment, FileAttachment>> " +
                "so EF Core can translate it server-side (a compiled delegate would not translate).");

            return (Expression<Func<FileAttachment, FileAttachment>>)value!;
        }

        [TestMethod]
        [Description("#1028: pins the exact set of fields the shared projection assigns — must equal, not merely contain or be contained by, the expected set.")]
        public void FileMetadataProjection_AssignedMemberSet_MatchesExpectedFields()
        {
            var projection = GetProjection();

            var memberInit = projection.Body as MemberInitExpression;
            Assert.IsNotNull(memberInit,
                "#1028: expected the projection body to be a MemberInitExpression " +
                "(x => new FileAttachment { ... }) — if the shape changed, this test needs to " +
                "change with it, not be deleted.");

            var actualFields = memberInit!.Bindings
                .OfType<MemberAssignment>()
                .Select(b => b.Member.Name)
                .ToList();

            // No duplicate assignments to the same member (would itself be a bug: the second
            // assignment silently wins and CollectionAssert.AreEquivalent's multiset comparison
            // would fail loudly here rather than let one member's binding hide another's).
            CollectionAssert.AreEquivalent(ExpectedFields.ToList(), actualFields,
                "#1028: the shared projection's assigned field set drifted from the expected " +
                "set. If this is an intentional new column, update ExpectedFields in this test " +
                "in the SAME change. If it's HandlerInfo missing again, that is the exact #1028 " +
                "regression this test exists to catch.");
        }

        [TestMethod]
        [Description("#1028: HandlerInfo specifically must be present — the field this issue adds.")]
        public void FileMetadataProjection_IncludesHandlerInfo()
        {
            var projection = GetProjection();
            var memberInit = (MemberInitExpression)projection.Body;

            var hasHandlerInfo = memberInit.Bindings
                .OfType<MemberAssignment>()
                .Any(b => b.Member.Name == nameof(FileAttachment.HandlerInfo));

            Assert.IsTrue(hasHandlerInfo,
                "#1028: the shared projection must assign HandlerInfo — its absence is the exact " +
                "defect that made WtmOssFileHandler always fall back to the first configured " +
                "OSS group/bucket instead of the one a file actually belongs to.");
        }

        [TestMethod]
        [Description("#1028: the projection must deliberately still exclude FileData — the whole point of projecting instead of a plain entity load.")]
        public void FileMetadataProjection_ExcludesFileData()
        {
            var projection = GetProjection();
            var memberInit = (MemberInitExpression)projection.Body;

            var hasFileData = memberInit.Bindings
                .OfType<MemberAssignment>()
                .Any(b => b.Member.Name == nameof(FileAttachment.FileData));

            Assert.IsFalse(hasFileData,
                "#1028: FileData (the byte[] payload) must NOT be in this projection — loading it " +
                "unconditionally would defeat the entire reason GetFileCore/DeleteFileCore use a " +
                "projection instead of a plain entity load.");
        }
    }
}
