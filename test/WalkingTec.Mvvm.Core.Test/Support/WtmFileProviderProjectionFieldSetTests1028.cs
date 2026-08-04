#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
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
                "defect that made the object-storage handler removed by #1055 always fall back " +
                "to the first configured group/bucket instead of the one a file actually " +
                "belongs to.");
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

        // ═══════════════════════════════════════════════════════════════════════════════════
        // The three tests above compare the projection against a HARDCODED ExpectedFields list.
        // That list is written by a human and can go stale the same way the two original
        // hand-maintained Select projections went stale: nothing forces it to track
        // FileAttachment itself. The test below instead derives its expectation from
        // FileAttachment's own reflected shape, so a column added to FileAttachment tomorrow
        // without a corresponding decision here fails loudly — this is the actual
        // recurrence-prevention mechanism #1028 asked for; ExpectedFields above is a readable,
        // reviewable snapshot, not the safety net.
        // ═══════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fields that are intentionally NOT in <c>_fileMetadataProjection</c>, with the reason
        /// each is excluded. A property must be listed here — with a reason — or be in the
        /// projection; there is no third option. Do not add an entry here to silence a failing
        /// test without first deciding, and writing down, why the field belongs outside the
        /// projection.
        /// </summary>
        private static readonly Dictionary<string, string> DeliberatelyExcludedFields =
            new(StringComparer.Ordinal)
            {
                [nameof(FileAttachment.FileData)] =
                    "the byte[] payload — the entire reason GetFileCore/DeleteFileCore use a " +
                    "projection instead of a plain entity load is to avoid loading this column.",

                [nameof(FileAttachment.TenantCode)] =
                    "drives the EF Core ITenant global query filter applied to the source " +
                    "IQueryable<FileAttachment> BEFORE Select ever runs; it is not part of the " +
                    "IWtmFile contract any caller of the projection's result consumes, so " +
                    "carrying it into the projected object would be dead weight, not a fix.",
            };

        /// <summary>
        /// True for a property reflection should NOT treat as a mapped scalar column: EF
        /// navigation/collection properties, and any other property type that plainly isn't a
        /// column-shaped value. <see cref="FileAttachment"/> has none of these today (no
        /// navigation properties), but the projection-pinning test below must still classify
        /// them correctly if one is ever added — a navigation property must never silently be
        /// demanded of <c>_fileMetadataProjection</c>, which only ever selects scalar columns.
        /// <c>string</c> and <c>byte[]</c> both implement <see cref="IEnumerable"/> but are
        /// ordinary scalar-mapped EF column types, so both are explicitly carved out before the
        /// general collection check.
        /// </summary>
        private static bool IsNavigationOrCollectionProperty(PropertyInfo property)
        {
            var type = property.PropertyType;

            if (type == typeof(string) || type == typeof(byte[]))
            {
                return false;
            }

            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                return true; // collection navigation (List<T>, ICollection<T>, arrays of entities, ...)
            }

            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(Guid) ||
                underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) ||
                underlying == typeof(decimal) || underlying == typeof(TimeSpan))
            {
                return false; // scalar value type (or Nullable<> of one)
            }

            // Any remaining reference type (Stream, a related entity, ...) is treated as a
            // navigation/non-scalar member for this heuristic.
            return type.IsClass && type != typeof(string);
        }

        /// <summary>
        /// Reflects over <see cref="FileAttachment"/> itself (walking the base class chain —
        /// <c>Type.GetProperties</c> includes inherited public instance members for a CLASS by
        /// default, which is what picks up <c>TopBasePoco.ID</c>; note this is specifically a
        /// class behaviour — <c>Type.GetMember</c> called on an INTERFACE does not walk base
        /// interfaces the same way, a distinct gotcha that does not apply here because
        /// <see cref="FileAttachment"/> is a class, not an interface) and returns the set of
        /// properties that are: public, readable AND writable (excludes computed-only members
        /// like <c>TopBasePoco.IsBasePoco</c>), not decorated <c>[NotMapped]</c>, and not a
        /// navigation/collection-shaped type per <see cref="IsNavigationOrCollectionProperty"/>.
        /// </summary>
        private static HashSet<string> GetFileAttachmentMappedScalarPropertyNames()
        {
            return typeof(FileAttachment)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => p.GetCustomAttribute<NotMappedAttribute>() == null)
                .Where(p => !IsNavigationOrCollectionProperty(p))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
        }

        [TestMethod]
        [Description("#1028: the actual recurrence guard — FileAttachment's own reflected mapped-scalar property set must equal (projection fields) UNION (DeliberatelyExcludedFields), so a newly added column that is in neither fails loudly instead of silently missing the projection.")]
        public void FileAttachmentMappedScalarProperties_EqualsProjectionUnionDeliberateExclusions()
        {
            var mappedScalarProperties = GetFileAttachmentMappedScalarPropertyNames();

            var projection = GetProjection();
            var memberInit = (MemberInitExpression)projection.Body;
            var projectedFields = memberInit.Bindings
                .OfType<MemberAssignment>()
                .Select(b => b.Member.Name)
                .ToHashSet(StringComparer.Ordinal);

            var accountedFor = new HashSet<string>(projectedFields, StringComparer.Ordinal);
            accountedFor.UnionWith(DeliberatelyExcludedFields.Keys);

            var undecided = mappedScalarProperties.Except(accountedFor).ToList();
            var stale = accountedFor.Except(mappedScalarProperties).ToList();

            if (undecided.Count == 0 && stale.Count == 0)
            {
                return;
            }

            var message = "#1028: FileAttachment's mapped scalar properties no longer match " +
                "(_fileMetadataProjection's fields) UNION (DeliberatelyExcludedFields).";

            if (undecided.Count > 0)
            {
                message += " UNDECIDED — these FileAttachment properties are mapped/scalar but " +
                    "appear in NEITHER the projection NOR DeliberatelyExcludedFields: [" +
                    string.Join(", ", undecided) + "]. For each: either add it to " +
                    "WtmFileProvider._fileMetadataProjection if callers/handlers need it, or add " +
                    "it to DeliberatelyExcludedFields in this test with a reason — this exact " +
                    "'silently neither' shape is the #1028 regression.";
            }

            if (stale.Count > 0)
            {
                message += " STALE — these names are in the projection or " +
                    "DeliberatelyExcludedFields but are NOT a current FileAttachment mapped " +
                    "scalar property (renamed or removed?): [" + string.Join(", ", stale) +
                    "]. Update _fileMetadataProjection/DeliberatelyExcludedFields to match.";
            }

            Assert.Fail(message);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════
        // Field-set pinning (above) only protects the projection's CONTENT. It says nothing
        // about whether GetFileCore and DeleteFileCore still point at the SAME expression object
        // — someone could satisfy every test above by giving DeleteFileCore its own separate
        // Expression<Func<FileAttachment, FileAttachment>> field with an identical field list,
        // silently reintroducing the "two hand-maintained lists" shape #1028 exists to close.
        // This scans each method's IL for a direct `ldsfld` of _fileMetadataProjection.
        // ═══════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// True if <paramref name="method"/>'s IL contains an <c>ldsfld</c> instruction (opcode
        /// <c>0x7E</c>, a single-byte non-prefixed opcode always followed by a 4-byte metadata
        /// token — no short form exists) whose token resolves to <paramref name="targetField"/>.
        /// This is a byte-pattern scan, not a full IL decoder: it does not track instruction
        /// boundaries, so in principle a <c>0x7E</c> byte occurring inside another instruction's
        /// operand could be misread as an <c>ldsfld</c>. That risk is one-sided and safe here —
        /// <see cref="Module.ResolveField(int)"/> throws (caught below, scan continues) for a
        /// token that doesn't resolve to a field at all, and is exceedingly unlikely to
        /// coincidentally resolve to exactly <paramref name="targetField"/> — so a false POSITIVE
        /// is not a realistic concern; a false negative would only make this test too strict
        /// (fail when it shouldn't), which the RED/GREEN proof below rules out for the current
        /// method shapes.
        /// </summary>
        private static bool MethodReferencesField(MethodInfo method, FieldInfo targetField)
        {
            var body = method.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (il == null)
            {
                return false;
            }

            const byte LdsfldOpcode = 0x7E;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != LdsfldOpcode)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    var resolved = method.Module.ResolveField(token);
                    if (resolved.DeclaringType == targetField.DeclaringType &&
                        resolved.Name == targetField.Name)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Not a valid field token at this offset — either a coincidental 0x7E byte
                    // inside another instruction's operand, or a token this scan doesn't need to
                    // resolve. Keep scanning; do not fail the test from inside the scan loop.
                }
            }

            return false;
        }

        [TestMethod]
        [Description("#1028: GetFileCore and DeleteFileCore must reference the exact SAME _fileMetadataProjection field object, not two independently-declared expressions of the same shape — this is what actually prevents the two-hand-maintained-lists shape from recurring, since the field-set tests above would stay green even if DeleteFileCore got its own separate (but identical) projection field.")]
        public void GetFileCoreAndDeleteFileCore_BothReferenceTheSameSharedProjectionField()
        {
            var field = typeof(WtmFileProvider).GetField(
                "_fileMetadataProjection",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "#1028: WtmFileProvider must declare '_fileMetadataProjection'.");

            var getFileCore = typeof(WtmFileProvider).GetMethod(
                "GetFileCore", BindingFlags.NonPublic | BindingFlags.Instance);
            var deleteFileCore = typeof(WtmFileProvider).GetMethod(
                "DeleteFileCore", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(getFileCore,
                "#1028: WtmFileProvider.GetFileCore not found by reflection — if it was renamed, " +
                "update this test's reflection target alongside the rename, do not delete the pin.");
            Assert.IsNotNull(deleteFileCore,
                "#1028: WtmFileProvider.DeleteFileCore not found by reflection — if it was renamed, " +
                "update this test's reflection target alongside the rename, do not delete the pin.");

            Assert.IsTrue(MethodReferencesField(getFileCore!, field!),
                "#1028: GetFileCore's IL no longer references the shared _fileMetadataProjection " +
                "field — if it was changed to build its own inline Select(x => new FileAttachment " +
                "{ ... }) projection, that silently reintroduces the two-hand-maintained-lists " +
                "shape #1028 fixed. Route it back through _fileMetadataProjection.");
            Assert.IsTrue(MethodReferencesField(deleteFileCore!, field!),
                "#1028: DeleteFileCore's IL no longer references the shared _fileMetadataProjection " +
                "field — if it was changed to build its own inline Select(x => new FileAttachment " +
                "{ ... }) projection (even one with an identical field list), that silently " +
                "reintroduces the two-hand-maintained-lists shape #1028 fixed. Route it back " +
                "through _fileMetadataProjection.");
        }
    }
}
