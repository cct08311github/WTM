using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>The decision of the file endpoints: references, modes, embedded files, the cut-off date and the choice of the connection.</summary>
    [TestClass]
    public class FileEndpointGuardTest
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        private static readonly string FileId = Guid.NewGuid().ToString();

        private readonly IDataProtector _protector = FileEndpointGuard.CreateProtector(new EphemeralDataProtectionProvider());

        private string Issue(string id = null, FilePurpose purposes = FilePurpose.Read, string connection = null, string user = "alice", string tenant = null, DateTimeOffset? now = null, FileEndpointOptions options = null)
        {
            return FileEndpointGuard.Issue(_protector, options ?? new FileEndpointOptions(), id ?? FileId, purposes, connection, user, tenant, now ?? Now);
        }

        private FileEndpointRequestContext Request(string reference, FilePurpose purpose = FilePurpose.View, string user = "alice", string tenant = null,
            FileEndpointMode mode = FileEndpointMode.Enforce, FileEndpointFileInfo file = null, Action<FileEndpointOptions> configure = null, string id = null)
        {
            var options = new FileEndpointOptions { Mode = mode };
            configure?.Invoke(options);
            return new FileEndpointRequestContext
            {
                Options = options,
                FileId = id ?? FileId,
                Purpose = purpose,
                Reference = reference,
                User = user,
                Tenant = tenant,
                Now = Now,
                OpenReference = r => FileEndpointGuard.Unprotect(_protector, r),
                GetFileInfo = () => file
            };
        }

        private static FileEndpointFileInfo Plain(DateTime? uploaded = null) => new FileEndpointFileInfo { ExtraInfo = "x", UploadTime = uploaded ?? new DateTime(2026, 1, 1) };

        // ---- references ---------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AValidReference_IsAdmitted_InEveryPurposeItWasIssuedFor()
        {
            var reference = Issue(purposes: FilePurpose.View | FilePurpose.Name);
            foreach (var purpose in new[] { FilePurpose.View, FilePurpose.Name })
            {
                var decision = FileEndpointGuard.Authorize(Request(reference, purpose, file: Plain()));
                Assert.IsTrue(decision.Allowed);
                Assert.AreEqual(FileEndpointAdmission.Reference, decision.Admission);
            }
        }

        [TestMethod]
        public void ThePurposeMustBeOneTheReferenceWasIssuedFor()
        {
            var decision = FileEndpointGuard.Authorize(Request(Issue(purposes: FilePurpose.View), FilePurpose.Download, file: Plain()));
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FileEndpointRefusal.PurposeMismatch, decision.Refusal);
        }

        [TestMethod]
        public void TheReferenceIsForOneFile_AndTheSpellingOfTheIdDoesNotMatter()
        {
            var reference = Issue(id: FileId.ToUpperInvariant());
            Assert.IsTrue(FileEndpointGuard.Authorize(Request(reference, id: FileId.ToLowerInvariant(), file: Plain())).Allowed);

            var other = FileEndpointGuard.Authorize(Request(reference, id: Guid.NewGuid().ToString(), file: Plain()));
            Assert.IsFalse(other.Allowed);
            Assert.AreEqual(FileEndpointRefusal.FileMismatch, other.Refusal);
        }

        [TestMethod]
        public void TheReferenceIsForOneUserAndTenant()
        {
            var reference = Issue(user: "alice", tenant: "t1");
            Assert.IsTrue(FileEndpointGuard.Authorize(Request(reference, user: "alice", tenant: "t1", file: Plain())).Allowed);

            var bob = FileEndpointGuard.Authorize(Request(reference, user: "bob", tenant: "t1", file: Plain()));
            Assert.IsFalse(bob.Allowed);
            Assert.AreEqual(FileEndpointRefusal.UserMismatch, bob.Refusal);

            var otherTenant = FileEndpointGuard.Authorize(Request(reference, user: "alice", tenant: "t2", file: Plain()));
            Assert.IsFalse(otherTenant.Allowed);
            Assert.AreEqual(FileEndpointRefusal.UserMismatch, otherTenant.Refusal);

            var anonymous = FileEndpointGuard.Authorize(Request(reference, user: null, tenant: "t1", file: Plain()));
            Assert.IsFalse(anonymous.Allowed);
        }

        [TestMethod]
        public void AReferenceWrittenForNobody_ServesTheAudienceOfThatPage()
        {
            var reference = Issue(user: null);
            Assert.IsTrue(FileEndpointGuard.Authorize(Request(reference, user: null, file: Plain())).Allowed);
            Assert.IsTrue(FileEndpointGuard.Authorize(Request(reference, user: "bob", file: Plain())).Allowed);
        }

        [TestMethod]
        public void AReferenceExpires()
        {
            var reference = Issue(options: new FileEndpointOptions { ReferenceLifetimeMinutes = 10 });
            var ctx = Request(reference, file: Plain());
            Assert.IsTrue(FileEndpointGuard.Authorize(ctx).Allowed);

            ctx.Now = Now.AddMinutes(11);
            var decision = FileEndpointGuard.Authorize(ctx);
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FileEndpointRefusal.ExpiredReference, decision.Refusal);
        }

        [TestMethod]
        public void TheDefaultLifetimeIs480Minutes_AndAValueBelowOneFallsBackToIt()
        {
            Assert.AreEqual(TimeSpan.FromMinutes(480), new FileEndpointOptions().EffectiveLifetime);
            Assert.AreEqual(TimeSpan.FromMinutes(480), new FileEndpointOptions { ReferenceLifetimeMinutes = 0 }.EffectiveLifetime);
            var reference = Issue();
            var ctx = Request(reference, file: Plain());
            ctx.Now = Now.AddMinutes(479);
            Assert.IsTrue(FileEndpointGuard.Authorize(ctx).Allowed);
            ctx.Now = Now.AddMinutes(481);
            Assert.IsFalse(FileEndpointGuard.Authorize(ctx).Allowed);
        }

        [TestMethod]
        public void ATamperedReference_OrOneOfAnotherApplication_IsRefused()
        {
            var reference = Issue();
            var tampered = reference.Substring(0, reference.Length - 4) + (reference.EndsWith("AAAA") ? "BBBB" : "AAAA");
            var decision = FileEndpointGuard.Authorize(Request(tampered, file: Plain()));
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FileEndpointRefusal.InvalidReference, decision.Refusal);

            var foreign = FileEndpointGuard.Issue(FileEndpointGuard.CreateProtector(new EphemeralDataProtectionProvider()), new FileEndpointOptions(), FileId, FilePurpose.Read, null, "alice", null, Now);
            Assert.AreEqual(FileEndpointRefusal.InvalidReference, FileEndpointGuard.Authorize(Request(foreign, file: Plain())).Refusal);
            Assert.AreEqual(FileEndpointRefusal.InvalidReference, FileEndpointGuard.Authorize(Request(new string('x', 5000), file: Plain())).Refusal);
        }

        [TestMethod]
        public void AReferenceCannotBeIssuedWithoutAProtector_OrForNothing()
        {
            Assert.IsNull(FileEndpointGuard.Issue(null, new FileEndpointOptions(), FileId, FilePurpose.Read, null, "a", null, Now));
            Assert.IsNull(FileEndpointGuard.Issue(_protector, new FileEndpointOptions(), FileId, FilePurpose.None, null, "a", null, Now));
            Assert.IsNull(FileEndpointGuard.Issue(_protector, new FileEndpointOptions(), "", FilePurpose.Read, null, "a", null, Now));
            Assert.IsNotNull(FileEndpointGuard.Issue(_protector, new FileEndpointOptions(), "", FilePurpose.Upload, "other", "a", null, Now));
        }

        // ---- modes ----------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Legacy_ServesWithoutAReference_AndDoesNotReadTheFile()
        {
            var ctx = Request(null, mode: FileEndpointMode.Legacy);
            ctx.GetFileInfo = () => throw new InvalidOperationException("the file must not be read");
            var decision = FileEndpointGuard.Authorize(ctx);
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Legacy, decision.Admission);
        }

        [TestMethod]
        public void Audit_ServesARequestWithoutAValidReference_AndSaysWhy()
        {
            var bare = FileEndpointGuard.Authorize(Request(null, mode: FileEndpointMode.Audit, file: Plain()));
            Assert.IsTrue(bare.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Audited, bare.Admission);
            Assert.AreEqual(FileEndpointRefusal.MissingReference, bare.Refusal);

            var expired = Issue();
            var ctx = Request(expired, mode: FileEndpointMode.Audit, file: Plain());
            ctx.Now = Now.AddDays(2);
            var decision = FileEndpointGuard.Authorize(ctx);
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Audited, decision.Admission);
            Assert.AreEqual(FileEndpointRefusal.ExpiredReference, decision.Refusal);
        }

        [TestMethod]
        public void Audit_AdmitsAValidReferenceAsAReference()
        {
            var decision = FileEndpointGuard.Authorize(Request(Issue(), mode: FileEndpointMode.Audit, file: Plain()));
            Assert.AreEqual(FileEndpointAdmission.Reference, decision.Admission);
            Assert.AreEqual(FileEndpointRefusal.None, decision.Refusal);
        }

        [TestMethod]
        public void Enforce_RefusesARequestWithoutAValidReference()
        {
            var bare = FileEndpointGuard.Authorize(Request(null, file: Plain()));
            Assert.IsFalse(bare.Allowed);
            Assert.AreEqual(FileEndpointRefusal.MissingReference, bare.Refusal);

            var wrongUser = FileEndpointGuard.Authorize(Request(Issue(user: "alice"), user: "bob", file: Plain()));
            Assert.IsFalse(wrongUser.Allowed);
        }

        [TestMethod]
        public void TheDefaultModeIsAudit()
        {
            Assert.AreEqual(FileEndpointMode.Audit, new FileEndpointOptions().Mode);
            Assert.AreEqual(FileEndpointMode.Audit, new Configs().FileEndpoints.Mode);
        }

        [TestMethod]
        public void AFileThatDoesNotExist_IsLeftToTheEndpoint()
        {
            var decision = FileEndpointGuard.Authorize(Request(null, file: null));
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.FileMissing, decision.Admission);
        }

        // ---- embedded files and the cut-off date ------------------------------------------------------------------------------------

        [TestMethod]
        public void AnEmbeddedFile_IsServedByItsId_InEnforce()
        {
            var info = new FileEndpointFileInfo { ExtraInfo = FileEndpointGuard.MarkEmbedded("note"), UploadTime = DateTime.Now };
            var decision = FileEndpointGuard.Authorize(Request(null, file: info));
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Embedded, decision.Admission);
        }

        [TestMethod]
        public void TheEmbeddedMarker_CannotBeSetByAClient()
        {
            Assert.AreEqual("note", FileEndpointGuard.StripEmbeddedMarker(FileEndpointGuard.MarkEmbedded("note")));
            Assert.AreEqual("note", FileEndpointGuard.StripEmbeddedMarker(FileEndpointGuard.MarkEmbedded(FileEndpointGuard.MarkEmbedded("note"))));
            Assert.AreEqual("note", FileEndpointGuard.StripEmbeddedMarker(FileEndpointGuard.EmbeddedMarker.ToUpperInvariant() + "note"));
            Assert.AreEqual("", FileEndpointGuard.StripEmbeddedMarker(FileEndpointGuard.EmbeddedMarker));
            Assert.IsNull(FileEndpointGuard.StripEmbeddedMarker(null));
            Assert.AreEqual("a" + FileEndpointGuard.EmbeddedMarker, FileEndpointGuard.StripEmbeddedMarker("a" + FileEndpointGuard.EmbeddedMarker), "only a leading marker counts");
            Assert.IsFalse(FileEndpointGuard.IsEmbedded(FileEndpointGuard.StripEmbeddedMarker(FileEndpointGuard.MarkEmbedded("x"))));
        }

        [TestMethod]
        public void FilesUploadedBeforeTheCutOff_KeepIdBasedAccess_InEnforce()
        {
            Action<FileEndpointOptions> cutoff = o => o.LegacyUploadedBefore = new DateTime(2026, 6, 1);

            var old = FileEndpointGuard.Authorize(Request(null, file: Plain(new DateTime(2026, 5, 31, 23, 59, 59)), configure: cutoff));
            Assert.IsTrue(old.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Grandfathered, old.Admission);

            var recent = FileEndpointGuard.Authorize(Request(null, file: Plain(new DateTime(2026, 6, 1)), configure: cutoff));
            Assert.IsFalse(recent.Allowed, "the cut-off moment itself is not before the cut-off");

            Assert.IsFalse(FileEndpointGuard.Authorize(Request(null, file: Plain(new DateTime(2020, 1, 1)))).Allowed, "without a cut-off nothing is grandfathered");
        }

        // ---- the connection -----------------------------------------------------------------------------------------------------------

        private static Configs Configs(Action<Configs> configure = null)
        {
            var configs = new Configs
            {
                Connections = new List<CS>
                {
                    new CS { Key = "default", Value = "x", DbType = DBTypeEnum.SQLite },
                    new CS { Key = "default_1", Value = "x", DbType = DBTypeEnum.SQLite },
                    new CS { Key = "other", Value = "y", DbType = DBTypeEnum.SQLite },
                }
            };
            configure?.Invoke(configs);
            return configs;
        }

        private static FileEndpointReference RefWith(string connection) => new FileEndpointReference { Connection = connection };

        [TestMethod]
        public void TheConnectionKeyOfARequest_DoesNotChooseTheConnection()
        {
            foreach (var key in new[] { null, "", "default", "DEFAULT", "default_1" })
            {
                var decision = FileEndpointGuard.ResolveConnection(key, null, Configs(), null);
                Assert.IsTrue(decision.Allowed);
                Assert.IsNull(decision.Key, "the server chooses for '" + key + "'");
            }
            var other = FileEndpointGuard.ResolveConnection("other", null, Configs(), null);
            Assert.IsTrue(other.Allowed);
            Assert.IsNull(other.Key, "a configured connection that is not listed is ignored");
        }

        [TestMethod]
        public void TheKeyOfTheServerIsAccepted()
        {
            var decision = FileEndpointGuard.ResolveConnection("other_1", null, Configs(), "other");
            Assert.IsTrue(decision.Allowed);
            Assert.IsNull(decision.Key);
        }

        [TestMethod]
        public void AListedKey_IsUsed_WithoutAReference()
        {
            var configs = Configs(c => c.FileEndpoints.RequestConnectionKeys.Add("Other"));
            var decision = FileEndpointGuard.ResolveConnection("other", null, configs, null);
            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual("other", decision.Key);
            Assert.IsNull(FileEndpointGuard.ResolveConnection("default", null, configs, null).Key);
        }

        [TestMethod]
        public void AllowRequestConnectionKey_RestoresTheOldChoice()
        {
            var configs = Configs(c => c.GenericEndpoints.AllowRequestConnectionKey = true);
            Assert.AreEqual("other", FileEndpointGuard.ResolveConnection("other", null, configs, null).Key);
        }

        [TestMethod]
        public void AValidReferenceDecidesTheConnection()
        {
            var decision = FileEndpointGuard.ResolveConnection(null, RefWith("other"), Configs(), null);
            Assert.AreEqual("other", decision.Key);

            var overridden = FileEndpointGuard.ResolveConnection("default", RefWith("other_1"), Configs(), null);
            Assert.AreEqual("other", overridden.Key, "the connection of the reference wins over the request");

            var forDefault = FileEndpointGuard.ResolveConnection("other", RefWith(""), Configs(), null);
            Assert.IsNull(forDefault.Key, "a reference for the default connection keeps the server's choice");
        }

        [TestMethod]
        public void ANotConfiguredConnection_IsRefusedCleanly()
        {
            Assert.IsFalse(FileEndpointGuard.ResolveConnection("nowhere", null, Configs(), null).Allowed);
            Assert.IsFalse(FileEndpointGuard.ResolveConnection("nowhere", null, Configs(c => c.GenericEndpoints.AllowRequestConnectionKey = true), null).Allowed);
            Assert.IsFalse(FileEndpointGuard.ResolveConnection(null, RefWith("gone"), Configs(), null).Allowed);
            Assert.IsFalse(FileEndpointGuard.ResolveConnection("nowhere", RefWith("other"), Configs(), null).Allowed, "even a valid reference does not excuse a key that names no connection");
        }
    }
}
