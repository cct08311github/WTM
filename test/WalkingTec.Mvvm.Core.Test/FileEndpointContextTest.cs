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
