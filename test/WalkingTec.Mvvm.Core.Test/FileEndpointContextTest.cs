using System;
using System.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>The file endpoints through the context of a request: references, links, modes, logging and the access policy.</summary>
    [TestClass]
    public class FileEndpointContextTest
    {
        private static readonly Guid FileId = Guid.NewGuid();

        private sealed class DenyingPolicy : IFileAccessPolicy
        {
            public int Calls;
            public FileAccessRequest Last;
            public bool IsAllowed(FileAccessRequest request)
            {
                Calls++;
                Last = request;
                return false;
            }
        }

        private static WTMContext Create(string user, IDataProtectionProvider provider, FileEndpointMode mode = FileEndpointMode.Audit, CapturingLoggerFactory logs = null, IFileAccessPolicy policy = null, Action<Configs> configure = null)
        {
            var configs = new Configs();
            configs.FileEndpoints.Mode = mode;
            configure?.Invoke(configs);
            return MockWtmContext.CreateWtmContext(usercode: user, dataProtection: provider, loggerFactory: logs, configs: configs, fileAccessPolicy: policy);
        }

        private static FileEndpointFileInfo Plain() => new FileEndpointFileInfo { ExtraInfo = null, UploadTime = new DateTime(2026, 1, 1) };

        private static string ReferenceOf(string url)
        {
            var marker = FileEndpointGuard.ReferenceParameter + "=";
            var start = url.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(start > 0, "no reference in " + url);
            var value = url.Substring(start + marker.Length);
            var end = value.IndexOf('&');
            return Uri.UnescapeDataString(end < 0 ? value : value.Substring(0, end));
        }

        // ---- links -------------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GetFileUrl_CarriesAReferenceThatAdmitsTheFileForThePurpose()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider, FileEndpointMode.Enforce);

            var view = wtm.GetFileUrl(FileId, FilePurpose.View);
            var download = wtm.GetFileUrl(FileId, FilePurpose.Download);
            var name = wtm.GetFileUrl(FileId, FilePurpose.Name);

            StringAssert.StartsWith(view, $"/_Framework/GetFile/{FileId}?");
            StringAssert.Contains(view, "stream=true");
            Assert.IsFalse(download.Contains("stream=true"));
            StringAssert.StartsWith(name, $"/_Framework/GetFileName/{FileId}?");
            Assert.IsTrue(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.View, ReferenceOf(view), () => Plain()).Allowed);
            Assert.IsTrue(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, ReferenceOf(download), () => Plain()).Allowed);
            Assert.IsFalse(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, ReferenceOf(view), () => Plain()).Allowed, "the reference of a view link does not download");
            Assert.IsTrue(wtm.AuthorizeFileRequest("GetFileName", FileId.ToString(), FilePurpose.Name, ReferenceOf(name), () => Plain()).Allowed);
        }

        [TestMethod]
        public void AReference_IsNotUsableBySomeoneElse()
        {
            var provider = new EphemeralDataProtectionProvider();
            var alice = Create("alice", provider, FileEndpointMode.Enforce);
            var bob = Create("bob", provider, FileEndpointMode.Enforce);
            var reference = ReferenceOf(alice.GetFileUrl(FileId, FilePurpose.Download));

            Assert.IsTrue(alice.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.IsFalse(bob.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
        }

        [TestMethod]
        public void GetFileUrl_WithTheConnectionOfThePage_NamesItAndBindsIt()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider);
            var url = wtm.GetFileUrl(FileId, FilePurpose.Download, "other");
            StringAssert.Contains(url, "_DONOT_USE_CS=other");
            var reference = FileEndpointGuard.Unprotect(FileEndpointGuard.CreateProtector(provider), ReferenceOf(url));
            Assert.AreEqual("other", reference.Connection);
            Assert.AreEqual("alice", reference.User);
        }

        [TestMethod]
        public void WithoutDataProtection_NoReferenceIsIssued_AndTheLinkIsThePlainOne()
        {
            var wtm = Create("alice", null);
            Assert.IsNull(wtm.CreateFileReference(FileId));
            var url = wtm.GetFileUrl(FileId, FilePurpose.Download);
            Assert.IsFalse(url.Contains(FileEndpointGuard.ReferenceParameter));
            StringAssert.StartsWith(url, $"/_Framework/GetFile/{FileId}");
        }

        [TestMethod]
        public void ALinkWrittenForAnAnonymousVisitor_CarriesAReferenceForNobody()
        {
            var provider = new EphemeralDataProtectionProvider();
            var anonymous = Create("alice", provider, FileEndpointMode.Enforce);
            anonymous.LoginUserInfo = null;
            var reference = ReferenceOf(anonymous.GetFileUrl(FileId, FilePurpose.Download));

            var visitor = Create("alice", provider, FileEndpointMode.Enforce);
            visitor.LoginUserInfo = null;
            Assert.IsTrue(visitor.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.IsFalse(visitor.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain()).Allowed, "the bare id of the file is refused");
        }

        [TestMethod]
        public void UtilsOverload_AddsTheReference_AndTheOldOneIsUnchanged()
        {
            var wtm = Create("alice", new EphemeralDataProtectionProvider());
            var seed = Guid.NewGuid().ToString();
            using var dc = new DataContext(seed, DBTypeEnum.Memory);
            // no row: the old overload returns an empty url and so does the new one
            Assert.AreEqual(string.Empty, Utils.GetUrlByFileAttachmentId(dc, FileId));
            Assert.AreEqual(string.Empty, Utils.GetUrlByFileAttachmentId(wtm, dc, FileId));

            dc.Set<FileAttachment>().Add(new FileAttachment { ID = FileId, FileName = "a.txt", FileExt = "txt" });
            dc.SaveChanges();
            var plain = Utils.GetUrlByFileAttachmentId(dc, FileId);
            Assert.AreEqual("/_Framework/GetFile/" + FileId, plain);
            var withReference = Utils.GetUrlByFileAttachmentId(wtm, dc, FileId);
            StringAssert.StartsWith(withReference, plain + "?" + FileEndpointGuard.ReferenceParameter + "=");
            Assert.IsTrue(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, ReferenceOf(withReference), () => null).Allowed);
        }

        // ---- modes and logging -------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Audit_ServesABareId_AndLogsAWarningWithoutTheFileContent()
        {
            var logs = new CapturingLoggerFactory();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Audit, logs);

            var decision = wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain());

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Audited, decision.Admission);
            var warning = logs.Entries.Single(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Message;
            StringAssert.Contains(warning, "GetFile");
            StringAssert.Contains(warning, "alice");
            StringAssert.Contains(warning, "MissingReference");
        }

        [TestMethod]
        public void Audit_WithAValidReference_LogsNothing()
        {
            var logs = new CapturingLoggerFactory();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Audit, logs);
            var reference = ReferenceOf(wtm.GetFileUrl(FileId, FilePurpose.Download));

            Assert.IsTrue(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.AreEqual(0, logs.Entries.Count);
        }

        [TestMethod]
        public void Enforce_RefusesABareId_AndLogsIt()
        {
            var logs = new CapturingLoggerFactory();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Enforce, logs);

            var decision = wtm.AuthorizeFileRequest("ViewFile", FileId.ToString(), FilePurpose.View, null, () => Plain());

            Assert.IsFalse(decision.Allowed);
            StringAssert.Contains(logs.Entries.Single().Message, "ViewFile");
        }

        [TestMethod]
        public void Legacy_ServesWithoutLookingAtTheFile_AndWithoutAskingThePolicy()
        {
            var policy = new DenyingPolicy();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Legacy, policy: policy);

            var decision = wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => throw new InvalidOperationException("must not be read"));

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(0, policy.Calls);
        }

        // ---- the policy --------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ThePolicy_IsAskedAfterTheReferenceCheck_AndCanRefuse()
        {
            var policy = new DenyingPolicy();
            var provider = new EphemeralDataProtectionProvider();
            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var wtm = Create("alice", provider, mode, policy: policy);
                var reference = ReferenceOf(wtm.GetFileUrl(FileId, FilePurpose.Download));
                var decision = wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain());

                Assert.IsFalse(decision.Allowed, mode.ToString());
                Assert.AreEqual(FileEndpointRefusal.PolicyDenied, decision.Refusal);
            }
            Assert.AreEqual(FileEndpointAdmission.Reference, policy.Last.Admission);
            Assert.AreEqual("alice", policy.Last.User);
            Assert.AreEqual(FilePurpose.Download, policy.Last.Purpose);
        }

        [TestMethod]
        public void ThePolicy_IsNotAskedForARequestThatIsRefusedAnyway()
        {
            var policy = new DenyingPolicy();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Enforce, policy: policy);
            Assert.IsFalse(wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain()).Allowed);
            Assert.AreEqual(0, policy.Calls);
        }

        private sealed class ThrowingPolicy : IFileAccessPolicy
        {
            public bool IsAllowed(FileAccessRequest request) => throw new InvalidOperationException("the rule failed");
        }

        [TestMethod]
        public void AThrowingPolicy_RefusesTheRequest_InAuditAndEnforce_AndIsLogged()
        {
            var provider = new EphemeralDataProtectionProvider();
            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var logs = new CapturingLoggerFactory();
                var wtm = Create("alice", provider, mode, logs, new ThrowingPolicy());
                var reference = ReferenceOf(wtm.GetFileUrl(FileId, FilePurpose.Download));

                var decision = wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain());

                Assert.IsFalse(decision.Allowed, mode.ToString());
                Assert.AreEqual(FileEndpointRefusal.PolicyDenied, decision.Refusal);
                Assert.IsTrue(logs.Entries.Any(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Error && x.Message.Contains("InvalidOperationException")));
            }
        }

        [TestMethod]
        public void WithoutADataProtectionProvider_AReferenceCannotBeOpened_SoEnforceRefusesAndAuditServes()
        {
            var issuer = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Enforce);
            var reference = ReferenceOf(issuer.GetFileUrl(FileId, FilePurpose.Download));

            var enforce = Create("alice", null, FileEndpointMode.Enforce);
            Assert.IsFalse(enforce.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.IsFalse(enforce.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain()).Allowed);

            var audit = Create("alice", null, FileEndpointMode.Audit);
            var decision = audit.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain());
            Assert.AreEqual(FileEndpointAdmission.Audited, decision.Admission);
            Assert.AreEqual(FileEndpointRefusal.InvalidReference, decision.Refusal);
        }

        [TestMethod]
        public void TwoUsersWithTheSameName_InDifferentHomeTenants_DoNotShareReferences()
        {
            var provider = new EphemeralDataProtectionProvider();
            WTMContext As(string tenant)
            {
                var wtm = Create("alice", provider, FileEndpointMode.Enforce);
                wtm.LoginUserInfo = new LoginUserInfo { ITCode = "alice", TenantCode = tenant };
                return wtm;
            }
            var inT1 = As("t1");
            var reference = ReferenceOf(inT1.GetFileUrl(FileId, FilePurpose.Download));

            Assert.IsTrue(As("t1").AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.IsFalse(As("t2").AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
            Assert.IsFalse(As(null).AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, reference, () => Plain()).Allowed);
        }

        [TestMethod]
        public void TheRefererInTheLog_IsShortenedAndHasNoControlCharacters()
        {
            var logs = new CapturingLoggerFactory();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Audit, logs);
            var referer = "http://x/" + new string('a', 600) + "\r\nforged: line\u2028more";
            Moq.Mock.Get(wtm.HttpContext.Request).Setup(x => x.Headers).Returns(new Microsoft.AspNetCore.Http.HeaderDictionary { ["Referer"] = referer });

            wtm.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain());

            var message = logs.Entries.Single().Message;
            Assert.IsFalse(message.Contains("\r") || message.Contains("\n") || message.Contains("\u2028"));
            Assert.IsTrue(message.Length < 700, message.Length.ToString());
            StringAssert.Contains(message, "http://x/aaa");
        }

        [TestMethod]
        public void ARequestOfACallerWhoIsNotSignedIn_IsLoggedAtInformation_ASignedInOneAtWarning()
        {
            var provider = new EphemeralDataProtectionProvider();
            var anonymousLogs = new CapturingLoggerFactory();
            var anonymous = Create("alice", provider, FileEndpointMode.Audit, anonymousLogs);
            anonymous.LoginUserInfo = null;
            anonymous.AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain());
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, anonymousLogs.Entries.Single().Level);

            var signedInLogs = new CapturingLoggerFactory();
            Create("alice", provider, FileEndpointMode.Audit, signedInLogs).AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.Download, null, () => Plain());
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, signedInLogs.Entries.Single().Level);
        }

        [TestMethod]
        public void LogText_ShortensAndCleans()
        {
            Assert.AreEqual("", LogText.Sanitize(null));
            Assert.AreEqual("a b c d", LogText.Sanitize("a\nb\rc\td"));
            Assert.AreEqual("abc...", LogText.Sanitize("abcdef", 3));
            Assert.AreEqual("abc", LogText.Sanitize("abc", 3));
            Assert.AreEqual("中文 text", LogText.Sanitize("中文 text"));
        }

        [TestMethod]
        public void TheDefaultPolicy_AllowsEverything()
        {
            Assert.IsTrue(new AllowAllFileAccessPolicy().IsAllowed(new FileAccessRequest()));
        }

        // ---- connection through the context --------------------------------------------------------------------------------------------

        private static Configs TwoConnections(Configs c)
        {
            c.Connections = new System.Collections.Generic.List<CS>
            {
                new CS { Key = "default", Value = "x", DbType = DBTypeEnum.SQLite },
                new CS { Key = "other", Value = "y", DbType = DBTypeEnum.SQLite },
            };
            return c;
        }

        [TestMethod]
        public void ResolveFileConnection_UsesTheConnectionOfAValidReference_AndIgnoresTheRequestOtherwise()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider, configure: c => TwoConnections(c));
            var upload = wtm.CreateFileReference(string.Empty, FilePurpose.Upload, "other");
            var fileRef = wtm.CreateFileReference(FileId, FilePurpose.Read, "other");

            Assert.AreEqual("other", wtm.ResolveFileConnection(null, null, FilePurpose.Upload, upload).Key);
            Assert.AreEqual("other", wtm.ResolveFileConnection("default", FileId.ToString(), FilePurpose.View, fileRef).Key);
            Assert.IsNull(wtm.ResolveFileConnection("other", null, FilePurpose.Upload, null).Key, "a request cannot choose a connection");
            Assert.IsNull(wtm.ResolveFileConnection("other", FileId.ToString(), FilePurpose.View, "garbage").Key);
            Assert.IsFalse(wtm.ResolveFileConnection("nowhere", null, FilePurpose.Upload, null).Allowed);

            var bob = Create("bob", provider, configure: c => TwoConnections(c));
            Assert.IsNull(bob.ResolveFileConnection(null, null, FilePurpose.Upload, upload).Key, "the reference of another user does not decide");
        }

        [TestMethod]
        public void ResolveFileConnection_AListedKeyWorksWithoutAReference()
        {
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), configure: c => { TwoConnections(c); c.FileEndpoints.RequestConnectionKeys.Add("other"); });
            Assert.AreEqual("other", wtm.ResolveFileConnection("other", FileId.ToString(), FilePurpose.View, null).Key);
        }

        // ---- capabilities, deletion, avatars and the file API of a single page application ------------------------------------------------

        [TestMethod]
        public void ACapability_IsNotBoundToAUser_AndAdmitsAnAnonymousCaller()
        {
            var provider = new EphemeralDataProtectionProvider();
            var alice = Create("alice", provider, FileEndpointMode.Enforce);
            var capability = alice.CreateFileCapability(FileId);
            Assert.IsNotNull(capability);
            var opened = FileEndpointGuard.Unprotect(FileEndpointGuard.CreateProtector(provider), capability);
            Assert.AreEqual(string.Empty, opened.User);
            Assert.AreEqual(FilePurpose.Read, opened.Purposes);
            Assert.AreEqual(FileId.ToString(), opened.FileId);

            var anonymous = Create("alice", provider, FileEndpointMode.Enforce);
            anonymous.LoginUserInfo = null;
            foreach (var purpose in new[] { FilePurpose.View, FilePurpose.Download, FilePurpose.Name })
            {
                Assert.IsTrue(anonymous.AuthorizeFileRequest("GetFile", FileId.ToString(), purpose, capability, () => Plain()).Allowed, purpose.ToString());
            }
            Assert.IsTrue(Create("bob", provider, FileEndpointMode.Enforce).AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.View, capability, () => Plain()).Allowed, "any signed-in user too");
            Assert.IsFalse(anonymous.AuthorizeFileRequest("GetFile", Guid.NewGuid().ToString(), FilePurpose.View, capability, () => Plain()).Allowed, "but for that one file only");
        }

        [TestMethod]
        public void ACapability_NeverGrantsDeleteOrUpload_AndIsBoundToTheConnectionAndTenant()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider, configure: c => TwoConnections(c));
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "alice", TenantCode = "t1", CurrentTenant = "t1" };
            var protector = FileEndpointGuard.CreateProtector(provider);

            var all = FileEndpointGuard.Unprotect(protector, wtm.CreateFileCapability(FileId, FilePurpose.Read | FilePurpose.Delete | FilePurpose.Upload, "other"));
            Assert.AreEqual(FilePurpose.Read, all.Purposes);
            Assert.AreEqual("other", all.Connection);
            Assert.AreEqual("t1", all.Tenant);
            Assert.IsNull(wtm.CreateFileCapability(FileId, FilePurpose.Delete));
            Assert.IsNull(wtm.CreateFileCapability(FileId, FilePurpose.None));
            Assert.IsNull(Create("alice", null).CreateFileCapability(FileId), "no data protection, no capability");
            Assert.IsNotNull(wtm.CreateFileCapability(FileId.ToString(), FilePurpose.View));
        }

        [TestMethod]
        public void ACapability_OfOneTenant_IsRefusedForASignedInUserOfAnother()
        {
            var provider = new EphemeralDataProtectionProvider();
            WTMContext In(string tenant)
            {
                var wtm = Create("alice", provider, FileEndpointMode.Enforce);
                wtm.LoginUserInfo = new LoginUserInfo { ITCode = "alice", TenantCode = tenant, CurrentTenant = tenant };
                return wtm;
            }
            var capability = In("t1").CreateFileCapability(FileId);
            Assert.IsTrue(In("t1").AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.View, capability, () => Plain()).Allowed);
            Assert.IsFalse(In("t2").AuthorizeFileRequest("GetFile", FileId.ToString(), FilePurpose.View, capability, () => Plain()).Allowed);
        }

        [TestMethod]
        public void GetFileUrl_WithAPath_BuildsTheAddressOfAnotherEndpoint()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider, FileEndpointMode.Enforce, configure: c => TwoConnections(c));
            var protector = FileEndpointGuard.CreateProtector(provider);

            var bound = wtm.GetFileUrl(FileId.ToString(), FilePurpose.View, "other", "/api/_file/GetFile", "csName");
            StringAssert.StartsWith(bound, $"/api/_file/GetFile/{FileId}?1=1&csName=other&{FileEndpointGuard.ReferenceParameter}=");
            Assert.AreEqual("alice", FileEndpointGuard.Unprotect(protector, ReferenceOf(bound)).User);

            var capability = wtm.GetFileUrl(FileId.ToString(), FilePurpose.View, null, "/api/_file/GetFile", "csName", capability: true);
            Assert.IsFalse(capability.Contains("csName="), "no connection key: none in the address");
            var opened = FileEndpointGuard.Unprotect(protector, ReferenceOf(capability));
            Assert.AreEqual(string.Empty, opened.User);
            Assert.AreEqual(FilePurpose.View, opened.Purposes);

            // the plain framework address is untouched
            StringAssert.StartsWith(wtm.GetFileUrl(FileId, FilePurpose.Download), $"/_Framework/GetFile/{FileId}?1=1");
            Assert.IsFalse(Create("alice", null).GetFileUrl(FileId.ToString(), FilePurpose.View, null, "/x").Contains(FileEndpointGuard.ReferenceParameter));
        }

        [TestMethod]
        public void Deletion_NeedsADeleteReferenceIssuedToTheSameUser_InEveryModeButLegacy()
        {
            var provider = new EphemeralDataProtectionProvider();
            var alice = Create("alice", provider, FileEndpointMode.Audit);
            var deleteRef = alice.CreateFileReference(FileId, FilePurpose.Delete);
            var readRef = alice.CreateFileReference(FileId, FilePurpose.Read);
            Assert.IsNotNull(deleteRef);

            foreach (var mode in new[] { FileEndpointMode.Audit, FileEndpointMode.Enforce })
            {
                var logs = new CapturingLoggerFactory();
                var me = Create("alice", provider, mode, logs);
                Assert.IsTrue(me.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), deleteRef).Allowed, mode.ToString());
                Assert.IsFalse(me.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), null).Allowed, mode + " without a reference");
                Assert.IsFalse(me.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), readRef).Allowed, mode + " with a read reference");
                Assert.IsFalse(me.AuthorizeFileDeletion("DeletedFile", Guid.NewGuid().ToString(), deleteRef).Allowed, mode + " for another file");
                Assert.IsFalse(Create("bob", provider, mode).AuthorizeFileDeletion("DeletedFile", FileId.ToString(), deleteRef).Allowed, mode + " for another user");
                Assert.AreEqual(3, logs.Entries.Count(x => x.Message.Contains("DeletedFile") && x.Message.Contains("refused")), "every refusal is logged");
            }

            var legacy = Create("alice", provider, FileEndpointMode.Legacy);
            Assert.IsTrue(legacy.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), null).Allowed);
            legacy.LoginUserInfo = null;
            Assert.IsFalse(legacy.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), null).Allowed);
        }

        [TestMethod]
        public void Deletion_AsksThePolicy_AndAnAnonymousCallerHasNoDeleteReference()
        {
            var provider = new EphemeralDataProtectionProvider();
            var policy = new DenyingPolicy();
            var wtm = Create("alice", provider, FileEndpointMode.Enforce, policy: policy);
            var deleteRef = wtm.CreateFileReference(FileId, FilePurpose.Delete);
            var decision = wtm.AuthorizeFileDeletion("DeletedFile", FileId.ToString(), deleteRef);
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FileEndpointRefusal.PolicyDenied, decision.Refusal);
            Assert.AreEqual(FilePurpose.Delete, policy.Last.Purpose);

            wtm.LoginUserInfo = null;
            Assert.IsNull(wtm.CreateFileReference(FileId, FilePurpose.Delete), "nobody is signed in, so nobody can be given the right");
        }

        [TestMethod]
        public void AnAvatarCheck_AdmitsARequestWithoutAReference_AndTheOldOverloadIsUnchanged()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = Create("alice", provider, FileEndpointMode.Enforce);
            wtm.LoginUserInfo = null;
            var withAvatar = wtm.AuthorizeFileRequest("GetUserPhoto", FileId.ToString(), FilePurpose.View, null, () => Plain(), () => true);
            Assert.IsTrue(withAvatar.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Avatar, withAvatar.Admission);
            Assert.IsFalse(wtm.AuthorizeFileRequest("GetUserPhoto", FileId.ToString(), FilePurpose.View, null, () => Plain(), () => false).Allowed);
            Assert.IsFalse(wtm.AuthorizeFileRequest("GetUserPhoto", FileId.ToString(), FilePurpose.View, null, () => Plain()).Allowed);
        }

        [TestMethod]
        public void TheAvatarAdmission_StillGoesThroughThePolicy()
        {
            var policy = new DenyingPolicy();
            var wtm = Create("alice", new EphemeralDataProtectionProvider(), FileEndpointMode.Enforce, policy: policy);
            wtm.LoginUserInfo = null;
            var decision = wtm.AuthorizeFileRequest("GetUserPhoto", FileId.ToString(), FilePurpose.View, null, () => Plain(), () => true);
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FileEndpointAdmission.Avatar, policy.Last.Admission);
        }

        private static WTMContext CreateRouting(IDataContext dc, bool signedIn, params FrameworkTenant[] tenants)
        {
            var wtm = MockWtmContext.CreateWtmContext(usercode: "alice", createDc: key => dc);
            wtm.GlobaInfo.SetTenantGetFunc(() => tenants.ToList());
            if (signedIn == false)
            {
                wtm.LoginUserInfo = null;
            }
            return wtm;
        }

        [TestMethod]
        public void CreateFileDC_ForACapabilityOfAnAnonymousCaller_ReadsInTheTenantOfTheCapability()
        {
            var provider = new EphemeralDataProtectionProvider();
            var issuer = Create("alice", provider, FileEndpointMode.Enforce, configure: c => TwoConnections(c));
            issuer.LoginUserInfo = new LoginUserInfo { ITCode = "alice", TenantCode = "t1", CurrentTenant = "t1" };
            var capability = issuer.CreateFileCapability(FileId);

            var anonymousCheck = Create("alice", provider, FileEndpointMode.Enforce, configure: c => TwoConnections(c));
            anonymousCheck.LoginUserInfo = null;
            var connection = anonymousCheck.ResolveFileConnection(null, FileId.ToString(), FilePurpose.View, capability);
            Assert.AreEqual("t1", connection.Tenant);

            var dc = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            var anonymous = CreateRouting(dc, false, new FrameworkTenant { TCode = "t1" });
            Assert.AreSame(dc, anonymous.CreateFileDC(connection));
            Assert.AreEqual("t1", dc.TenantCode, "the data is read in the tenant of the capability");
        }

        [TestMethod]
        public void CreateFileDC_ForASignedInCallerOrWithoutATenant_IsTheOrdinaryOne()
        {
            var dc = new EmptyContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
            var signedIn = CreateRouting(dc, true, new FrameworkTenant { TCode = "t1" });
            var provider = new EphemeralDataProtectionProvider();
            var issuer = Create("alice", provider, FileEndpointMode.Enforce, configure: c => TwoConnections(c));
            issuer.LoginUserInfo = new LoginUserInfo { ITCode = "alice", TenantCode = "t1", CurrentTenant = "t1" };
            var connection = issuer.ResolveFileConnection(null, FileId.ToString(), FilePurpose.View, issuer.CreateFileCapability(FileId));
            Assert.AreEqual("t1", connection.Tenant);
            Assert.AreSame(dc, signedIn.CreateFileDC(connection));
            Assert.IsNull(dc.TenantCode, "a signed-in caller keeps the tenant its own context gives");

            var anonymous = CreateRouting(dc, false, new FrameworkTenant { TCode = "t1" });
            Assert.AreSame(dc, anonymous.CreateFileDC(null));
            Assert.AreSame(dc, anonymous.CreateFileDC(FileConnectionDecision.Server()));
            Assert.IsNull(dc.TenantCode);
        }

        // ---- the link buttons ----------------------------------------------------------------------------------------------------------

        private sealed class OldUiService : IUIService
        {
            public string Last;
            public string MakeButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string title = null, string buttonID = null, bool resizable = true, bool max = false, string currentdivid = "", string buttonClass = null, string style = null, RedirectTypesEnum rtype = RedirectTypesEnum.Layer) => "";
            public string MakeDialogButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string title = null, string buttonID = null, bool showDialog = true, bool resizable = true, bool max = false, string buttonClass = null, string style = null) => "";
            public string MakeDownloadButton(ButtonTypesEnum buttonType, Guid fileID, string buttonText = null, string _DONOT_USE_CS = "default", string buttonClass = null, string style = null) => Last = "download:" + buttonText;
            public string MakeViewButton(ButtonTypesEnum buttonType, Guid fileID, string buttonText = null, int? width = null, int? height = null, string title = null, bool resizable = true, string _DONOT_USE_CS = "default", bool maxed = false, string buttonClass = null, string style = null) => Last = "view:" + buttonText;
            public string MakeScriptButton(ButtonTypesEnum buttonType, string buttonText, string script = "", string buttonID = null, string url = null, string buttonClass = null, string style = null) => "";
            public string MakeCheckBox(bool ischeck, string text = null, string name = null, string value = null, bool isReadOnly = false) => "";
            public string MakeRadio(bool ischeck, string text = null, string name = null, string value = null, bool isReadOnly = false) => "";
            public string MakeCombo(string name = null, System.Collections.Generic.List<ComboSelectListItem> value = null, string selectedValue = null, string emptyText = null, bool isReadOnly = false) => "";
            public string MakeTextBox(string name = null, string value = null, string emptyText = null, bool isReadOnly = false) => "";
            public string MakeDateTime(string name = null, string value = null, string emptyText = null, bool isReadOnly = false, DateTimeTypeEnum? dateType = DateTimeTypeEnum.DateTime) => "";
        }

        [TestMethod]
        public void AnExistingUiService_StillWorks_TheReferenceVariantsFallBackToTheOldMethods()
        {
            IUIService ui = new OldUiService();
            Assert.AreEqual("download:T", ui.MakeDownloadButtonWithReference(ButtonTypesEnum.Link, FileId, "ref", "T"));
            Assert.AreEqual("view:V", ui.MakeViewButtonWithReference(ButtonTypesEnum.Link, FileId, "ref", "V"));
        }

        [TestMethod]
        public void LayUiButtons_CarryTheReferenceInTheirLink_AndWithoutOneLookAsBefore()
        {
            var ui = new LayuiUIService();
            var plain = ui.MakeDownloadButton(ButtonTypesEnum.Link, FileId, "T", "default");
            var withRef = ui.MakeDownloadButtonWithReference(ButtonTypesEnum.Link, FileId, "AbC-_9", "T", "default");

            Assert.AreEqual(plain.Replace("'>T</a>", "&amp;_DONOT_USE_FREF=AbC-_9'>T</a>"), withRef);
            StringAssert.Contains(ui.MakeViewButtonWithReference(ButtonTypesEnum.Img, FileId, "AbC-_9", "T"), "&amp;_DONOT_USE_FREF=AbC-_9");
            Assert.IsFalse(ui.MakeViewButton(ButtonTypesEnum.Img, FileId, "T").Contains("_DONOT_USE_FREF"));
        }
    }
}
