#nullable enable
using System;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    [TestClass]
    public class DataPrivilegeInfoTests
    {
        // ─── Minimal TopBasePoco stub for testing ─────────────────────────────

        private class Department : TopBasePoco
        {
            public string? DeptName { get; set; }
            public bool IsActive { get; set; }
        }

        // ─── Constructor ──────────────────────────────────────────────────────

        [TestMethod]
        public void Constructor_SetsModelType_ToTypeOfT()
        {
            var info = new DataPrivilegeInfo<Department>(
                "Test",
                x => x.DeptName!);

            info.ModelType.Should().Be(typeof(Department));
        }

        [TestMethod]
        public void Constructor_SetsModelName_ToTypeName()
        {
            var info = new DataPrivilegeInfo<Department>(
                "Test",
                x => x.DeptName!);

            info.ModelName.Should().Be("Department");
        }

        [TestMethod]
        public void Constructor_SetsPrivillegeName_FromParam()
        {
            var info = new DataPrivilegeInfo<Department>(
                "DeptPrivilege",
                x => x.DeptName!);

            info.PrivillegeName.Should().Be("DeptPrivilege");
        }

        [TestMethod]
        public void Constructor_WithoutWhere_DoesNotThrow()
        {
            Action act = () => new DataPrivilegeInfo<Department>(
                "NoWhere",
                x => x.DeptName!);

            act.Should().NotThrow();
        }

        [TestMethod]
        public void Constructor_WithWhere_DoesNotThrow()
        {
            Action act = () => new DataPrivilegeInfo<Department>(
                "WithWhere",
                x => x.DeptName!,
                x => x.IsActive == true);

            act.Should().NotThrow();
        }

        // ─── IDataPrivilege interface ──────────────────────────────────────────

        [TestMethod]
        public void IDataPrivilege_ModelName_Settable()
        {
            IDataPrivilege info = new DataPrivilegeInfo<Department>(
                "Test",
                x => x.DeptName!);

            info.ModelName = "CustomName";
            info.ModelName.Should().Be("CustomName");
        }

        [TestMethod]
        public void IDataPrivilege_PrivillegeName_Settable()
        {
            IDataPrivilege info = new DataPrivilegeInfo<Department>(
                "Test",
                x => x.DeptName!);

            info.PrivillegeName = "NewName";
            info.PrivillegeName.Should().Be("NewName");
        }

        [TestMethod]
        public void IDataPrivilege_ModelType_Settable()
        {
            IDataPrivilege info = new DataPrivilegeInfo<Department>(
                "Test",
                x => x.DeptName!);

            info.ModelType = typeof(string);
            info.ModelType.Should().Be(typeof(string));
        }
    }
}
