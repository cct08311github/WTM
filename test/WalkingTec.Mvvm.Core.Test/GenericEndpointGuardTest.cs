using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The decision the generic data endpoints take for the view model a request names: served for a request that carries the reference
    /// the page wrote (same user, tenant, view model, purpose and connection, not expired, page privilege still held), refused otherwise.
    /// </summary>
    [TestClass]
    public class GenericEndpointGuardTest
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        private static readonly Type Vm = typeof(LayuiSchoolListVM);
        private static readonly string VmName = GenericEndpointGuard.GetViewModelName(typeof(LayuiSchoolListVM));

        private readonly IDataProtector _protector = GenericEndpointGuard.CreateProtector(new EphemeralDataProtectionProvider());

        private string Issue(GenericEndpointPurpose purposes = GenericEndpointPurpose.List, string cs = "default", string user = "alice", string tenant = null,
            string privilegeUrl = "/School/Index", GenericEndpointOptions options = null, Type type = null, DateTimeOffset? now = null, IEnumerable<string> fields = null)
        {
            return GenericEndpointGuard.Issue(_protector, options, type ?? Vm, purposes, cs, user, tenant, privilegeUrl, now ?? Now, fields);
        }

        private GenericEndpointDecision Authorize(string reference, GenericEndpointPurpose purpose = GenericEndpointPurpose.List, string user = "alice", string tenant = null,
            string vmName = null, bool checkConnection = true, string cs = "default", Func<string, bool> canOpen = null, GenericEndpointOptions options = null,
            DateTimeOffset? now = null, string field = null, IDataProtector protector = null)
        {
            protector ??= _protector;
            return GenericEndpointGuard.Authorize(new GenericEndpointRequestContext
            {
                Options = options,
                ViewModelName = vmName ?? VmName,
                Purpose = purpose,
                Reference = reference,
                CheckConnection = checkConnection,
                ConnectionKey = cs,
                Field = field,
                User = user,
                Tenant = tenant,
                Now = now ?? Now,
                OpenReference = r => GenericEndpointGuard.Unprotect(protector, r),
                CanOpenPage = canOpen ?? (_ => true),
                ResolveType = n => Type.GetType(n)
            });
        }

        [TestMethod]
        public void ReferenceOfThePage_AdmitsTheRequest()
        {
            var decision = Authorize(Issue());

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(GenericEndpointAdmission.Reference, decision.Admission);
            Assert.AreEqual("/School/Index", decision.PrivilegeUrl);
        }

        [TestMethod]
        public void RequestWithoutReference_IsRefused_ForEveryUser()
        {
            foreach (var user in new[] { "alice", "admin", null })
            {
                var decision = Authorize(null, user: user);
                Assert.IsFalse(decision.Allowed, user);
                Assert.AreEqual(GenericEndpointRefusal.MissingReference, decision.Refusal);
            }
        }

        [TestMethod]
        public void RequestWithoutViewModelName_IsRefused()
        {
            var decision = Authorize(Issue(), vmName: "");
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(GenericEndpointRefusal.UnknownViewModel, decision.Refusal);
        }

        [TestMethod]
        public void ReferenceOfAnotherViewModel_IsRefused()
        {
            var reference = Issue(type: typeof(LayuiTestVM));

            var decision = Authorize(reference);

            Assert.AreEqual(GenericEndpointRefusal.ViewModelMismatch, decision.Refusal);
        }

        [TestMethod]
        public void ReferenceOfAnotherUser_IsRefused()
        {
            Assert.AreEqual(GenericEndpointRefusal.UserMismatch, Authorize(Issue(user: "alice"), user: "bob").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.UserMismatch, Authorize(Issue(user: "alice"), user: null).Refusal);
        }

        [TestMethod]
        public void ReferenceOfAnotherTenant_IsRefused()
        {
            Assert.AreEqual(GenericEndpointRefusal.UserMismatch, Authorize(Issue(tenant: "t1"), tenant: "t2").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.UserMismatch, Authorize(Issue(tenant: "t1"), tenant: null).Refusal);
            Assert.IsTrue(Authorize(Issue(tenant: "t1"), tenant: "t1").Allowed);
        }

        [TestMethod]
        public void AnonymousPage_ReferenceServesAnonymousCallersOnly()
        {
            var reference = Issue(user: null, privilegeUrl: null);

            Assert.IsTrue(Authorize(reference, user: null).Allowed);
            Assert.IsFalse(Authorize(reference, user: "alice").Allowed);
        }

        [TestMethod]
        public void ReferenceExpires()
        {
            var options = new GenericEndpointOptions { ReferenceLifetimeMinutes = 10 };
            var reference = Issue(options: options);

            Assert.IsTrue(Authorize(reference, now: Now.AddMinutes(9)).Allowed);
            Assert.AreEqual(GenericEndpointRefusal.ExpiredReference, Authorize(reference, now: Now.AddMinutes(11)).Refusal);
        }

        [TestMethod]
        public void LifetimeBelowOne_UsesTheDefault()
        {
            Assert.AreEqual(TimeSpan.FromMinutes(GenericEndpointOptions.DefaultReferenceLifetimeMinutes), new GenericEndpointOptions { ReferenceLifetimeMinutes = 0 }.EffectiveLifetime);
            Assert.AreEqual(480, GenericEndpointOptions.DefaultReferenceLifetimeMinutes);
        }

        [TestMethod]
        public void ReferenceOnlyServesItsPurposes()
        {
            var listOnly = Issue(GenericEndpointPurpose.List);

            Assert.IsTrue(Authorize(listOnly, GenericEndpointPurpose.List).Allowed);
            Assert.AreEqual(GenericEndpointRefusal.PurposeMismatch, Authorize(listOnly, GenericEndpointPurpose.Export).Refusal);
            Assert.AreEqual(GenericEndpointRefusal.PurposeMismatch, Authorize(listOnly, GenericEndpointPurpose.Template, checkConnection: false).Refusal);
            Assert.AreEqual(GenericEndpointRefusal.PurposeMismatch, Authorize(listOnly, GenericEndpointPurpose.Update, checkConnection: false, field: "Name").Refusal);

            var gridReference = Issue(GenericEndpointPurpose.List | GenericEndpointPurpose.Export);
            Assert.IsTrue(Authorize(gridReference, GenericEndpointPurpose.Export).Allowed);
            Assert.AreEqual(GenericEndpointRefusal.PurposeMismatch, Authorize(gridReference, GenericEndpointPurpose.Selector).Refusal);
        }

        [TestMethod]
        public void ConnectionKey_MustBeTheOneOfThePage_WhenTheEndpointTakesOne()
        {
            var reference = Issue(cs: "reports");

            Assert.IsTrue(Authorize(reference, cs: "reports").Allowed);
            Assert.AreEqual(GenericEndpointRefusal.ConnectionMismatch, Authorize(reference, cs: "default").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.ConnectionMismatch, Authorize(reference, cs: null).Refusal);
            // endpoints without a connection parameter do not compare it
            Assert.IsTrue(Authorize(reference, checkConnection: false, cs: null).Allowed);
        }

        [TestMethod]
        public void NullAndEmptyConnectionKey_AreTheSame()
        {
            var reference = Issue(cs: null);
            Assert.IsTrue(Authorize(reference, cs: "").Allowed);
            Assert.IsTrue(Authorize(reference, cs: null).Allowed);
        }

        [TestMethod]
        public void ReferenceStopsWorking_WhenTheUserLosesThePagePrivilege()
        {
            var reference = Issue(privilegeUrl: "/School/Index");
            var asked = new List<string>();

            Assert.IsTrue(Authorize(reference, canOpen: u => { asked.Add(u); return true; }).Allowed);
            var decision = Authorize(reference, canOpen: u => false);

            CollectionAssert.AreEqual(new[] { "/School/Index" }, asked);
            Assert.AreEqual(GenericEndpointRefusal.PrivilegeRevoked, decision.Refusal);
        }

        [TestMethod]
        public void PageOpenToEverySignedInUser_NeedsNoPrivilegeCheck()
        {
            var reference = Issue(privilegeUrl: null);

            Assert.IsTrue(Authorize(reference, canOpen: u => throw new InvalidOperationException("no page privilege to check")).Allowed);
        }

        [TestMethod]
        public void TamperedOrForeignReference_IsRefused()
        {
            var reference = Issue();
            var tampered = reference.Substring(0, reference.Length - 4) + (reference.EndsWith("AAAA") ? "BBBB" : "AAAA");
            var otherApp = GenericEndpointGuard.CreateProtector(new EphemeralDataProtectionProvider());

            Assert.AreEqual(GenericEndpointRefusal.InvalidReference, Authorize(tampered).Refusal);
            Assert.AreEqual(GenericEndpointRefusal.InvalidReference, Authorize(reference, protector: otherApp).Refusal);
            Assert.AreEqual(GenericEndpointRefusal.InvalidReference, Authorize("not a reference").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.InvalidReference, Authorize(new string('A', 5000)).Refusal);
        }

        [TestMethod]
        public void ReferenceProtectedForAnotherPurposeString_IsRefused()
        {
            var provider = new EphemeralDataProtectionProvider();
            var foreign = provider.CreateProtector("some other use");
            var reference = GenericEndpointGuard.Protect(foreign, new GenericEndpointReference { ViewModel = VmName, Purposes = GenericEndpointPurpose.List, User = "alice", ExpiresAt = Now.AddHours(1).ToUnixTimeSeconds() });

            Assert.AreEqual(GenericEndpointRefusal.InvalidReference, Authorize(reference, protector: GenericEndpointGuard.CreateProtector(provider)).Refusal);
        }

        [TestMethod]
        public void NoDataProtection_NoReferenceCanBeIssuedOrOpened()
        {
            Assert.IsNull(GenericEndpointGuard.Issue(null, null, Vm, GenericEndpointPurpose.List, null, "alice", null, null, Now));
            Assert.IsNull(GenericEndpointGuard.Unprotect(null, "x"));
        }

        [TestMethod]
        public void Update_NeedsTheFieldToBeListedByThePage()
        {
            var reference = Issue(GenericEndpointPurpose.Update, fields: new[] { "Name" });

            Assert.IsTrue(Authorize(reference, GenericEndpointPurpose.Update, checkConnection: false, field: "Name").Allowed);
            Assert.AreEqual(GenericEndpointRefusal.FieldNotAllowed, Authorize(reference, GenericEndpointPurpose.Update, checkConnection: false, field: "IsValid").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.FieldNotAllowed, Authorize(reference, GenericEndpointPurpose.Update, checkConnection: false, field: "name").Refusal);
            Assert.AreEqual(GenericEndpointRefusal.FieldNotAllowed, Authorize(reference, GenericEndpointPurpose.Update, checkConnection: false, field: null).Refusal);
        }

        [TestMethod]
        public void Update_WithoutListedFields_AdmitsNoField()
        {
            var reference = Issue(GenericEndpointPurpose.Update);

            Assert.AreEqual(GenericEndpointRefusal.FieldNotAllowed, Authorize(reference, GenericEndpointPurpose.Update, checkConnection: false, field: "Name").Refusal);
        }

        [TestMethod]
        public void LegacyMode_ServesWithoutReference()
        {
            var decision = Authorize(null, options: new GenericEndpointOptions { Mode = GenericEndpointMode.Legacy }, user: null);

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(GenericEndpointAdmission.Legacy, decision.Admission);
        }

        [TestMethod]
        public void Default_IsEnforce()
        {
            Assert.AreEqual(GenericEndpointMode.Enforce, new GenericEndpointOptions().Mode);
            Assert.AreEqual(GenericEndpointMode.Enforce, new Configs().GenericEndpoints.Mode);
            Assert.AreEqual(0, new Configs().GenericEndpoints.ExemptViewModels.Count);
        }

        [TestMethod]
        public void ExemptViewModel_IsServedToSignedInUsersWithoutReference()
        {
            var options = new GenericEndpointOptions { ExemptViewModels = { typeof(LayuiSchoolListVM).FullName } };

            var signedIn = Authorize(null, options: options);
            var anonymous = Authorize(null, options: options, user: null);

            Assert.AreEqual(GenericEndpointAdmission.Exempt, signedIn.Admission);
            Assert.IsFalse(anonymous.Allowed);
            Assert.AreEqual(GenericEndpointRefusal.MissingReference, anonymous.Refusal);
        }

        [TestMethod]
        public void ExemptViewModels_MatchByExactNameOrPrefix_AndOnlyListedOnes()
        {
            var ns = typeof(LayuiSchoolListVM).Namespace;
            Assert.IsTrue(Authorize(null, options: new GenericEndpointOptions { ExemptViewModels = { ns + ".*" } }).Allowed);
            Assert.IsFalse(Authorize(null, options: new GenericEndpointOptions { ExemptViewModels = { ns + ".LayuiTestVM" } }).Allowed);
            Assert.IsFalse(Authorize(null, options: new GenericEndpointOptions { ExemptViewModels = { "*" } }).Allowed);
            Assert.IsFalse(Authorize(null, options: new GenericEndpointOptions { ExemptViewModels = { "" } }).Allowed);
        }

        [TestMethod]
        public void ExemptName_ThatIsNotAViewModelType_IsRefused()
        {
            var options = new GenericEndpointOptions { ExemptViewModels = { "System.*" } };

            var decision = Authorize(null, options: options, vmName: "System.IO.FileInfo, System.Private.CoreLib");

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(GenericEndpointRefusal.UnknownViewModel, decision.Refusal);
        }

        [TestMethod]
        public void Exempt_NeverCoversUpdates()
        {
            var options = new GenericEndpointOptions { ExemptViewModels = { "*" , typeof(LayuiSchoolListVM).FullName } };

            Assert.IsFalse(Authorize(null, GenericEndpointPurpose.Update, options: options, checkConnection: false, field: "Name").Allowed);
        }

        [TestMethod]
        public void InvalidReference_FallsBackToTheExemption()
        {
            var options = new GenericEndpointOptions { ExemptViewModels = { typeof(LayuiSchoolListVM).FullName } };

            Assert.AreEqual(GenericEndpointAdmission.Exempt, Authorize("stale", options: options).Admission);
        }

        [TestMethod]
        public void ViewModelName_IsTheSpellingOfTheTagHelpers()
        {
            // the tag helpers write the assembly-qualified name without the version, culture and token parts of the assembly
            var qualified = typeof(LayuiSchoolListVM).AssemblyQualifiedName;
            Assert.AreEqual(qualified.Substring(0, qualified.LastIndexOf(", Version=", StringComparison.CurrentCulture)), VmName);
            Assert.AreEqual(typeof(LayuiSchoolListVM), Type.GetType(VmName));
        }

        [TestMethod]
        public void TypeFullName_IsTheNamePartOfTheSpelling()
        {
            Assert.AreEqual("A.B.C", GenericEndpointGuard.GetTypeFullName("A.B.C, My.Assembly"));
            Assert.AreEqual("A.B.C", GenericEndpointGuard.GetTypeFullName("A.B.C"));
            Assert.AreEqual("A.G`1[[X.Y, Asm, Version=1.0.0.0]]", GenericEndpointGuard.GetTypeFullName("A.G`1[[X.Y, Asm, Version=1.0.0.0]], Asm"));
        }

        [TestMethod]
        public void LongPageUrl_LosesItsQuery_AndAnAbsurdOneGetsNoReference()
        {
            var url = "/School/Index?" + new string('q', 400);
            var reference = Issue(privilegeUrl: url);
            Assert.AreEqual("/School/Index", Authorize(reference).PrivilegeUrl);

            Assert.IsNull(Issue(privilegeUrl: "/" + new string('p', 600)));
        }

        #region through the context of a request

        [TestMethod]
        public void Context_IssuesReferencesThatItAdmits_ForItsOwnUserOnly()
        {
            var alice = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: new EphemeralDataProtectionProvider());
            alice.PagePrivilegeUrl = null;

            var reference = alice.CreateGenericEndpointReference(Vm, GenericEndpointPurpose.List | GenericEndpointPurpose.Export, "default");

            Assert.IsNotNull(reference);
            Assert.IsTrue(alice.AuthorizeGenericEndpoint(VmName, reference, GenericEndpointPurpose.List, true, "default").Allowed);
            Assert.IsFalse(alice.AuthorizeGenericEndpoint(VmName, reference, GenericEndpointPurpose.Selector).Allowed);
            Assert.IsFalse(alice.AuthorizeGenericEndpoint(VmName, null, GenericEndpointPurpose.List).Allowed);
        }

        [TestMethod]
        public void Context_WithoutDataProtection_IssuesNothing_AndRefuses()
        {
            var wtm = MockWtmContext.CreateWtmContext(usercode: "alice");

            Assert.IsNull(wtm.CreateGenericEndpointReference(Vm, GenericEndpointPurpose.List));
            var decision = wtm.AuthorizeGenericEndpoint(VmName, "anything", GenericEndpointPurpose.List);
            Assert.IsFalse(decision.Allowed);
        }

        [TestMethod]
        public void Context_PageRequiringAPrivilege_BindsTheReferenceToIt()
        {
            var provider = new EphemeralDataProtectionProvider();
            var wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: provider);
            wtm.PagePrivilegeUrl = "/School/Index";

            var reference = wtm.CreateGenericEndpointReference(Vm, GenericEndpointPurpose.List);
            var decision = wtm.AuthorizeGenericEndpoint(VmName, reference, GenericEndpointPurpose.List);

            // the mock user has no function privileges, so the page cannot be opened and the reference is not honoured
            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(GenericEndpointRefusal.PrivilegeRevoked, decision.Refusal);
        }

        #endregion
    }
}
