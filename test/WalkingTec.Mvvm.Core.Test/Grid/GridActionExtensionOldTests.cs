#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Grid
{
    /// <summary>
    /// Tests for GridActionExtension (`Old.cs) — covers all 15 extension methods.
    /// </summary>
    [TestClass]
    public class GridActionExtensionOldTests
    {
        private static GridAction Make() => new GridAction();

        [TestMethod]
        public void SetButtonId_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetButtonId("btn1");
            result.Should().BeSameAs(action);
            action.ButtonId.Should().Be("btn1");
        }

        [TestMethod]
        public void SetButtonId_Null_ClearsValue()
        {
            var action = Make();
            action.SetButtonId("btn1").SetButtonId(null);
            action.ButtonId.Should().BeNull();
        }

        [TestMethod]
        public void SetName_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetName("Edit");
            result.Should().BeSameAs(action);
            action.Name.Should().Be("Edit");
        }

        [TestMethod]
        public void SetDialogTitle_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetDialogTitle("Confirm");
            result.Should().BeSameAs(action);
            action.DialogTitle.Should().Be("Confirm");
        }

        [TestMethod]
        public void SetIconCls_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetIconCls("layui-icon-edit");
            result.Should().BeSameAs(action);
            action.IconCls.Should().Be("layui-icon-edit");
        }

        [TestMethod]
        public void SetArea_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetArea("Admin");
            result.Should().BeSameAs(action);
            action.Area.Should().Be("Admin");
        }

        [TestMethod]
        public void SetControllerName_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetControllerName("Student");
            result.Should().BeSameAs(action);
            action.ControllerName.Should().Be("Student");
        }

        [TestMethod]
        public void SetActionName_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetActionName("Edit");
            result.Should().BeSameAs(action);
            action.ActionName.Should().Be("Edit");
        }

        [TestMethod]
        public void SetQueryString_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetQueryString("id=1");
            result.Should().BeSameAs(action);
            action.QueryString.Should().Be("id=1");
        }

        [TestMethod]
        public void SetSize_Sets_Width_And_Height_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetSize(800, 600);
            result.Should().BeSameAs(action);
            action.DialogWidth.Should().Be(800);
            action.DialogHeight.Should().Be(600);
        }

        [TestMethod]
        public void SetSize_Null_Values_AreAllowed()
        {
            var action = Make();
            action.SetSize(null, null);
            action.DialogWidth.Should().BeNull();
            action.DialogHeight.Should().BeNull();
        }

        [TestMethod]
        public void SetShowDialog_Default_True_Sets_ShowDialog()
        {
            var action = Make();
            var result = action.SetShowDialog();
            result.Should().BeSameAs(action);
            action.ShowDialog.Should().BeTrue();
        }

        [TestMethod]
        public void SetShowDialog_False_Clears_ShowDialog()
        {
            var action = Make();
            action.SetShowDialog(false);
            action.ShowDialog.Should().BeFalse();
        }

        [TestMethod]
        public void SetForcePost_Default_True()
        {
            var action = Make();
            var result = action.SetForcePost();
            result.Should().BeSameAs(action);
            action.ForcePost.Should().BeTrue();
        }

        [TestMethod]
        public void SetIsRedirect_Default_True()
        {
            var action = Make();
            var result = action.SetIsRedirect();
            result.Should().BeSameAs(action);
            action.IsRedirect.Should().BeTrue();
        }

        [TestMethod]
        public void SetIsRedirect_False()
        {
            var action = Make();
            action.SetIsRedirect(false);
            action.IsRedirect.Should().BeFalse();
        }

        [TestMethod]
        public void SetParameterType_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetParameterType(GridActionParameterTypesEnum.MultiIds);
            result.Should().BeSameAs(action);
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.MultiIds);
        }

        [TestMethod]
        public void SetOnClickScript_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetOnClickScript("myFunc");
            result.Should().BeSameAs(action);
            action.OnClickFunc.Should().Be("myFunc");
        }

        [TestMethod]
        public void SetSubAction_AppendsActions()
        {
            var parent = Make();
            var child1 = Make().SetName("Child1");
            var child2 = Make().SetName("Child2");

            var result = parent.SetSubAction(child1, child2);
            result.Should().BeSameAs(parent);
            parent.SubActions.Should().HaveCount(2);
            parent.SubActions.Should().Contain(child1);
            parent.SubActions.Should().Contain(child2);
        }

        [TestMethod]
        public void SetSubAction_CalledTwice_Accumulates()
        {
            var parent = Make();
            parent.SetSubAction(Make().SetName("A"));
            parent.SetSubAction(Make().SetName("B"));
            parent.SubActions.Should().HaveCount(2);
        }

        [TestMethod]
        public void SetNotResizable_Default_False_Sets_Resizable_False()
        {
            var action = Make();
            var result = action.SetNotResizable();
            result.Should().BeSameAs(action);
            action.Resizable.Should().BeFalse();
        }

        [TestMethod]
        public void SetNotResizable_True_Sets_Resizable_True()
        {
            var action = Make();
            action.SetNotResizable(true);
            action.Resizable.Should().BeTrue();
        }

        [TestMethod]
        public void SetBindVisiableColName_Sets_And_Returns_Self()
        {
            var action = Make();
            var result = action.SetBindVisiableColName("IsActive");
            result.Should().BeSameAs(action);
            action.BindVisiableColName.Should().Be("IsActive");
        }

        [TestMethod]
        public void SetBindVisiableColName_Null_Clears()
        {
            var action = Make();
            action.SetBindVisiableColName("col").SetBindVisiableColName(null);
            action.BindVisiableColName.Should().BeNull();
        }

        [TestMethod]
        public void FluentChain_SetMultiple_Works()
        {
            var action = Make()
                .SetName("Create")
                .SetControllerName("Student")
                .SetActionName("Create")
                .SetShowDialog()
                .SetSize(800, 600)
                .SetParameterType(GridActionParameterTypesEnum.NoId);

            action.Name.Should().Be("Create");
            action.ControllerName.Should().Be("Student");
            action.ActionName.Should().Be("Create");
            action.ShowDialog.Should().BeTrue();
            action.DialogWidth.Should().Be(800);
            action.ParameterType.Should().Be(GridActionParameterTypesEnum.NoId);
        }
    }
}
