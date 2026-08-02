#nullable enable
using System;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Issue #1028 — <see cref="WtmFileProvider"/>'s <c>GetFileCore</c>/<c>DeleteFileCore</c>
    /// each rebuilt a <see cref="FileAttachment"/> via a hand-maintained <c>Select</c>
    /// projection that omitted <see cref="FileAttachment.HandlerInfo"/>. Any handler that
    /// branches on it (<c>WtmOssFileHandler</c> uses <c>HandlerInfo</c> to pick the OSS
    /// group/bucket, falling back to the first configured group when null — see
    /// <c>WtmOssFileHandler.GetFileData</c>/<c>DeleteFile</c>) therefore always saw null
    /// regardless of what was persisted: in a multi-group OSS deployment this makes reads look
    /// in the wrong bucket, and deletes issue against the wrong bucket (a delete against a
    /// non-existent object typically "succeeds", so the object that should have been removed
    /// silently survives).
    ///
    /// <para>
    /// <strong>Scope:</strong> this proves only that <c>HandlerInfo</c> now survives the
    /// projection from a persisted row to the <see cref="IWtmFile"/> object <c>GetFile</c>
    /// hands back to its caller (and, in production, on to whatever handler
    /// <c>CreateFileHandler</c> resolves). No multi-group OSS environment is stood up here —
    /// the wrong-bucket consequence itself is traced, not reproduced against a real OSS
    /// endpoint. The delete path (<c>DeleteFileCore</c>) shares the exact same centralized
    /// projection expression (<c>WtmFileProvider._fileMetadataProjection</c>, pinned by
    /// <see cref="WtmFileProviderProjectionFieldSetTests1028"/>) but is not separately exercised
    /// here: it is a <c>void</c> method with no observable return value, and reaching its
    /// internal <c>file.HandlerInfo</c> would require injecting a capturing handler through
    /// <c>WtmFileProvider</c>'s private, process-wide static handler registry
    /// (<c>_handlers</c>/<c>_defaultHandler</c>, mutated by <c>WtmFileProvider.Init</c>) — shared
    /// mutable state that would leak between tests running in the same process. That risk was
    /// judged worse than the marginal proof gained, given both call sites are now provably the
    /// same expression object.
    /// </para>
    ///
    /// <para>
    /// Uses a SQLite shared-memory fixture (<c>Microsoft.Data.Sqlite</c>, not EF InMemory)
    /// because the defect is specifically about the <c>Select</c> projection's SQL translation —
    /// InMemory's LINQ-to-objects evaluation of <c>Select</c> would happily "translate" any C#
    /// projection, including ones EF Core's real providers cannot, so a green result there would
    /// not prove the (now-shared) projection expression is one SQLite/SQL Server/Oracle can
    /// actually run.
    /// </para>
    /// </summary>
    [TestClass]
    public class WtmFileProviderHandlerInfoRoundTripTests1028
    {
        private static (Guid Id, string HandlerInfo) SeedFile(string cs, string label)
        {
            var handlerInfo = $"group-{label}-{Guid.NewGuid():N}";
            using var seedCtx = new FrameworkContext(cs, DBTypeEnum.SQLite);
            seedCtx.Database.EnsureCreated();
            var fa = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = $"file-{label}.txt",
                FileExt = "txt",
                SaveMode = "database",
                UploadTime = DateTime.UtcNow,
                HandlerInfo = handlerInfo,
                ExtraInfo = "extra-" + label,
                FileData = Encoding.UTF8.GetBytes("payload-" + label),
                Length = 7,
            };
            seedCtx.Add(fa);
            seedCtx.SaveChanges();
            return (fa.ID, handlerInfo);
        }

        [TestMethod]
        [Description("#1028: GetFile's projection must carry HandlerInfo from the persisted row through to the returned IWtmFile object.")]
        public void GetFile_PersistedHandlerInfo_SurvivesProjectionRoundTrip()
        {
            var cs = $"DataSource=wtmfp1028_get_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, handlerInfo) = SeedFile(cs, "get");

            var wtm = MockWtmContext.CreateWtmContext(null, "user1028");
            using var dc = new FrameworkContext(cs, DBTypeEnum.SQLite);

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFile(fileId.ToString(), withData: false, dc: dc);

            Assert.IsNotNull(result, "#1028: the seeded file must resolve at all — otherwise the HandlerInfo assertion below would be meaningless.");
            Assert.AreEqual(handlerInfo, result!.HandlerInfo,
                "#1028: HandlerInfo must survive GetFileCore's Select projection. Before the fix, " +
                "the hand-maintained projection never copied this column, so HandlerInfo was " +
                "always null regardless of what was persisted — the exact defect that made " +
                "WtmOssFileHandler fall back to the first configured OSS group/bucket instead of " +
                "the one the file actually belongs to.");
        }

        [TestMethod]
        [Description("#1028 positive control: unrelated metadata fields still survive the same projection unchanged.")]
        public void GetFile_PersistedMetadata_OtherFieldsStillSurviveProjection()
        {
            var cs = $"DataSource=wtmfp1028_ctl_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, _) = SeedFile(cs, "ctl");

            var wtm = MockWtmContext.CreateWtmContext(null, "user1028ctl");
            using var dc = new FrameworkContext(cs, DBTypeEnum.SQLite);

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFile(fileId.ToString(), withData: false, dc: dc);

            Assert.IsNotNull(result);
            Assert.AreEqual("file-ctl.txt", result!.FileName,
                "#1028 positive control: fields that were already in the projection before this " +
                "fix must keep working — this fix must not have narrowed the projection.");
            Assert.AreEqual("extra-ctl", result.ExtraInfo);
        }
    }
}
