#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmUIOptionsTests
    {
        [TestMethod]
        public void Defaults_AreLayUICompatible()
        {
            var opts = new WtmUIOptions();

            opts.ButtonPrimaryClass.Should().Be("layui-btn");
            opts.ButtonSmallClass.Should().Be("layui-btn layui-btn-xs");
            opts.ButtonDangerClass.Should().Be("layui-btn layui-btn-danger");
            opts.FormItemClass.Should().Be("layui-form-item");
            opts.FormLabelClass.Should().Be("layui-form-label");
            opts.InputClass.Should().Be("layui-input");
            opts.DisabledClass.Should().Be("layui-disabled");
            opts.TableClass.Should().Be("layui-table");
            opts.RowClass.Should().Be("layui-row");
        }

        [TestMethod]
        public void Defaults_GridColumns_Is12()
        {
            var opts = new WtmUIOptions();
            opts.GridColumns.Should().Be(12);
            opts.ColumnMdPrefix.Should().Be("layui-col-md");
        }

        [TestMethod]
        public void Defaults_RequiredMarker_ContainsRedAsterisk()
        {
            var opts = new WtmUIOptions();
            opts.RequiredMarkerHtml.Should().Contain("color:red");
            opts.RequiredMarkerHtml.Should().Contain("*");
        }

        [TestMethod]
        public void Defaults_LabelMarginOffset_Is30()
        {
            var opts = new WtmUIOptions();
            opts.LabelMarginOffset.Should().Be(30);
        }

        [TestMethod]
        public void Defaults_AriaRequired_IsEnabled()
        {
            var opts = new WtmUIOptions();
            opts.EnableAriaRequired.Should().BeTrue();
        }

        [TestMethod]
        public void CanOverride_AllProperties()
        {
            var opts = new WtmUIOptions
            {
                ButtonPrimaryClass = "btn-primary",
                FormItemClass = "form-group",
                InputClass = "form-control",
                DisabledClass = "disabled",
                GridColumns = 24,
                ColumnMdPrefix = "col-md-",
                LabelMarginOffset = 15,
                RequiredMarkerHtml = "<span class=\"required\">*</span>",
                EnableAriaRequired = false
            };

            opts.ButtonPrimaryClass.Should().Be("btn-primary");
            opts.FormItemClass.Should().Be("form-group");
            opts.InputClass.Should().Be("form-control");
            opts.GridColumns.Should().Be(24);
            opts.ColumnMdPrefix.Should().Be("col-md-");
            opts.LabelMarginOffset.Should().Be(15);
            opts.RequiredMarkerHtml.Should().Contain("required");
            opts.EnableAriaRequired.Should().BeFalse();
        }
    }
}
