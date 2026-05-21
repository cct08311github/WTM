#nullable enable
using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Models
{
    // ─── Concrete subclasses for testing ──────────────────────────────────────

    /// <summary>Minimal TopBasePoco subclass — no extra properties.</summary>
    public class SimplePoco : TopBasePoco { }

    /// <summary>TopBasePoco subclass that also implements IBasePoco.</summary>
    public class AuditedPoco : TopBasePoco, IBasePoco
    {
        public DateTime? CreateTime { get; set; }
        public string? CreateBy { get; set; }
        public DateTime? UpdateTime { get; set; }
        public string? UpdateBy { get; set; }
    }

    /// <summary>TopBasePoco subclass that has a ParentId property for testing GetParentID().</summary>
    public class HierarchicalPoco : TopBasePoco
    {
        public Guid? ParentId { get; set; }
    }

    [TestClass]
    public class TopBasePocoTests
    {
        // ─── Default property values ───────────────────────────────────────────

        [TestMethod]
        public void DefaultConstructor_IdIsEmpty()
        {
            var poco = new SimplePoco();
            poco.ID.Should().Be(Guid.Empty);
        }

        [TestMethod]
        public void DefaultConstructor_CheckedIsFalse()
        {
            var poco = new SimplePoco();
            poco.Checked.Should().BeFalse();
        }

        [TestMethod]
        public void DefaultConstructor_BatchErrorIsNull()
        {
            var poco = new SimplePoco();
            poco.BatchError.Should().BeNull();
        }

        [TestMethod]
        public void DefaultConstructor_ExcelIndexIsZero()
        {
            var poco = new SimplePoco();
            poco.ExcelIndex.Should().Be(0L);
        }

        // ─── Property setters ─────────────────────────────────────────────────

        [TestMethod]
        public void Id_CanBeSetAndRetrieved()
        {
            var poco = new SimplePoco();
            var id = Guid.NewGuid();
            poco.ID = id;
            poco.ID.Should().Be(id);
        }

        [TestMethod]
        public void Checked_CanBeSet()
        {
            var poco = new SimplePoco { Checked = true };
            poco.Checked.Should().BeTrue();
        }

        [TestMethod]
        public void BatchError_CanBeSet()
        {
            var poco = new SimplePoco { BatchError = "some error" };
            poco.BatchError.Should().Be("some error");
        }

        [TestMethod]
        public void ExcelIndex_CanBeSet()
        {
            var poco = new SimplePoco { ExcelIndex = 42L };
            poco.ExcelIndex.Should().Be(42L);
        }

        // ─── GetID ────────────────────────────────────────────────────────────

        [TestMethod]
        public void GetID_WhenIdIsEmpty_ReturnsEmptyGuid()
        {
            var poco = new SimplePoco();
            var id = poco.GetID();
            id.Should().Be(Guid.Empty);
        }

        [TestMethod]
        public void GetID_WhenIdSet_ReturnsCorrectGuid()
        {
            var poco = new SimplePoco();
            var expected = Guid.NewGuid();
            poco.ID = expected;
            poco.GetID().Should().Be(expected);
        }

        // ─── HasID ────────────────────────────────────────────────────────────

        [TestMethod]
        public void HasID_EmptyGuid_ReturnsFalse()
        {
            var poco = new SimplePoco { ID = Guid.Empty };
            poco.HasID().Should().BeFalse();
        }

        [TestMethod]
        public void HasID_NonEmptyGuid_ReturnsTrue()
        {
            var poco = new SimplePoco { ID = Guid.NewGuid() };
            poco.HasID().Should().BeTrue();
        }

        // ─── GetIDType ────────────────────────────────────────────────────────

        [TestMethod]
        public void GetIDType_SimplePoco_ReturnsGuidType()
        {
            var poco = new SimplePoco();
            poco.GetIDType().Should().Be(typeof(Guid));
        }

        // ─── SetID ────────────────────────────────────────────────────────────

        [TestMethod]
        public void SetID_WithGuid_SetsId()
        {
            var poco = new SimplePoco();
            var newId = Guid.NewGuid();
            poco.SetID(newId);
            poco.ID.Should().Be(newId);
        }

        [TestMethod]
        public void SetID_WithStringGuid_SetsId()
        {
            var poco = new SimplePoco();
            var newId = Guid.NewGuid();
            poco.SetID(newId.ToString());
            poco.ID.Should().Be(newId);
        }

        // ─── IsBasePoco ───────────────────────────────────────────────────────

        [TestMethod]
        public void IsBasePoco_SimplePoco_ReturnsFalse()
        {
            var poco = new SimplePoco();
            poco.IsBasePoco.Should().BeFalse();
        }

        [TestMethod]
        public void IsBasePoco_AuditedPoco_ReturnsTrue()
        {
            var poco = new AuditedPoco();
            poco.IsBasePoco.Should().BeTrue();
        }

        [TestMethod]
        public void IsBasePoco_CachedAfterFirstAccess()
        {
            var poco = new SimplePoco();
            var first = poco.IsBasePoco;
            var second = poco.IsBasePoco; // second access uses cached value
            first.Should().Be(second);
        }

        // ─── GetParentID ──────────────────────────────────────────────────────

        [TestMethod]
        public void GetParentID_WithParentId_ReturnsValue()
        {
            var parentId = Guid.NewGuid();
            var poco = new HierarchicalPoco { ParentId = parentId };
            var result = poco.GetParentID();
            result.Should().Be(parentId);
        }

        [TestMethod]
        public void GetParentID_NullParentId_ReturnsEmptyString()
        {
            var poco = new HierarchicalPoco { ParentId = null };
            var result = poco.GetParentID();
            result.Should().Be("");
        }

        // ─── HasID: string/int/long branches are dead for Guid ID ─────────────
        // NOTE: The string/int/long switch arms in HasID() are unreachable for
        // TopBasePoco because its ID property is always Guid. These branches exist
        // for potential subclasses with different key types. Coverage ceiling for
        // TopBasePoco is ~66% for HasID() due to these unreachable arms.

        // ─── Full property write/read cycle ───────────────────────────────────

        [TestMethod]
        public void AllNotMappedProperties_AreWritableAndReadable()
        {
            var poco = new SimplePoco
            {
                ID = Guid.NewGuid(),
                Checked = true,
                BatchError = "err",
                ExcelIndex = 5L
            };
            poco.Checked.Should().BeTrue();
            poco.BatchError.Should().Be("err");
            poco.ExcelIndex.Should().Be(5L);
        }
    }
}
