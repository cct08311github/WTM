using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.TagHelpers.LayUI;
using WalkingTec.Mvvm.TagHelpers.LayUI.Form;
using static WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI.TagHelperTestHarness;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// The pages that show a view model write the reference the generic data endpoints ask for: next to _DONOT_USE_VMNAME in the grid
    /// filter, in the selector filter and in the template link. The reference admits exactly the request the page makes.
    /// </summary>
    [TestClass]
    public class GenericEndpointReferenceTagHelperTests : LayUITagHelperTestBase
    {
        private static readonly string ListVmName = GenericEndpointGuard.GetViewModelName(typeof(LayuiSchoolListVM));
        private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

        private LayuiSchoolListVM NewListVm(bool protectedContext = true)
        {
            return new LayuiSchoolListVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: protectedContext ? _provider : null) };
        }

        private static string ReferenceIn(string text)
        {
            var m = Regex.Match(text, GenericEndpointGuard.ReferenceParameter + @"[""']?\s*[:=]\s*[""']?([A-Za-z0-9_\-%]+)");
            Assert.IsTrue(m.Success, "No reference found in: " + text);
            return Uri.UnescapeDataString(m.Groups[1].Value);
        }

        private T NewVm<T>(params string[] accessibleUrls) where T : LayuiSchoolListVM, new()
        {
            var vm = new T { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: _provider) };
            foreach (var url in accessibleUrls)
            {
                vm.Wtm.GlobaInfo.AllAccessUrls.Add(url);
            }
            return vm;
        }

        private static string RenderGrid(LayuiSchoolListVM listVm, bool inSelector = false)
        {
            return Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/_Framework/GetPagingData", IsInSelector = inSelector }, "wt:grid");
        }

        [TestMethod]
        public void Grid_WithoutAnExportButton_WritesAReferenceForPagingOnly()
        {
            var listVm = NewListVm();

            var reference = ReferenceIn(RenderGrid(listVm));

            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
            Assert.IsFalse(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
            Assert.IsFalse(listVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Update, false, field: "SchoolName").Allowed);
        }

        [TestMethod]
        public void Grid_WithTheStandardExportButton_WritesAReferenceThatAdmitsPagingAndExport()
        {
            var listVm = NewVm<LayuiExportSchoolListVM>("/School/ExportExcel");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiExportSchoolListVM));

            var reference = ReferenceIn(RenderGrid(listVm));

            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void Grid_WithTheExportButtonInsideAButtonGroup_WritesAReferenceThatAdmitsExport()
        {
            var listVm = NewVm<LayuiGroupedExportSchoolListVM>("/School/ExportExcel");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiGroupedExportSchoolListVM));

            var reference = ReferenceIn(RenderGrid(listVm));

            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void Grid_WhereTheUserCannotSeeTheExportButton_WritesAReferenceForPagingOnly()
        {
            // the button is hidden for a user without the privilege of its action, in a group as well as at the top
            foreach (var vm in new LayuiSchoolListVM[] { NewVm<LayuiExportSchoolListVM>(), NewVm<LayuiGroupedExportSchoolListVM>(), NewVm<LayuiFrameworkExportSchoolListVM>() })
            {
                var name = GenericEndpointGuard.GetViewModelName(vm.GetType());

                var reference = ReferenceIn(RenderGrid(vm));

                Assert.IsTrue(vm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.List, true, vm.CurrentCS).Allowed, name);
                Assert.IsFalse(vm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Export, true, vm.CurrentCS).Allowed, name);
            }
        }

        [TestMethod]
        public void Grid_WithTheFrameworkExportButton_WritesAReferenceThatAdmitsPagingAndExport()
        {
            var listVm = NewVm<LayuiFrameworkExportSchoolListVM>("/_Framework/GetExportExcel");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiFrameworkExportSchoolListVM));

            var reference = ReferenceIn(RenderGrid(listVm));

            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void GridInTheSelectorPopup_WritesAReferenceForPagingOnly_EvenWhenTheViewModelHasExportButtons()
        {
            var listVm = NewVm<LayuiExportSchoolListVM>("/School/ExportExcel");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiExportSchoolListVM));

            var reference = ReferenceIn(RenderGrid(listVm, inSelector: true));

            Assert.IsTrue(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
            Assert.IsFalse(listVm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Export, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void Grid_ReferenceBelongsToTheUserWhoSawThePage()
        {
            var listVm = NewListVm();
            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/_Framework/GetPagingData" }, "wt:grid");
            var reference = ReferenceIn(html);
            var bob = MockWtmContext.CreateWtmContext(usercode: "bob", dataProtection: _provider);

            Assert.IsFalse(bob.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, listVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void Grid_WithoutDataProtection_WritesNoReference_AndStillRenders()
        {
            var listVm = NewListVm(protectedContext: false);

            var html = Render(new DataTableTagHelper { Vm = Self(listVm, "Vm"), Url = "/School/Search" }, "wt:grid");

            AssertContains(html, "_DONOT_USE_VMNAME");
            AssertNotContains(html, GenericEndpointGuard.ReferenceParameter);
        }

        [TestMethod]
        public void Selector_WritesAReferenceForItsListViewModel()
        {
            var form = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: _provider) };
            var schoolVm = NewListVm();
            var helper = new SelectorTagHelper
            {
                Field = Field(form, x => x.SchoolId),
                ListVM = Self(schoolVm, "Selector"),
                TextBind = Field(new School(), x => x.SchoolName),
            };

            var html = Render(helper, "wt:selector", FormItems(form, "wtForm_x"));

            var reference = ReferenceIn(html);
            Assert.IsTrue(schoolVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.Selector, true, schoolVm.CurrentCS).Allowed);
            Assert.IsFalse(schoolVm.Wtm.AuthorizeGenericEndpoint(ListVmName, reference, GenericEndpointPurpose.List, true, schoolVm.CurrentCS).Allowed);
        }

        [TestMethod]
        public void TemplateButton_LinkCarriesAReferenceForTheTemplate()
        {
            var vm = new LayuiTestVM { Wtm = MockWtmContext.CreateWtmContext(usercode: "alice", dataProtection: _provider) };

            var html = Render(new DownloadTemplateButtonTagHelper { Id = "dt1", Vm = Self(vm, "Vm") }, "wt:downloadTemplateButton");

            AssertContains(html, "/_Framework/GetExcelTemplate?_DONOT_USE_VMNAME=", "LayuiTestVM", "&amp;" + GenericEndpointGuard.ReferenceParameter + "=");
            var name = GenericEndpointGuard.GetViewModelName(typeof(LayuiTestVM));
            var reference = ReferenceIn(html);
            Assert.IsTrue(vm.Wtm.AuthorizeGenericEndpoint(name, reference, GenericEndpointPurpose.Template).Allowed);
        }
    }
}
