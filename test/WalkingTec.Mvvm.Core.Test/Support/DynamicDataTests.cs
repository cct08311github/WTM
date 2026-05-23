#nullable enable
using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    [TestClass]
    public class DynamicDataTests
    {
        // ─── Construction ─────────────────────────────────────────────────────

        [TestMethod]
        public void Constructor_NewInstance_IsEmpty()
        {
            var dd = new DynamicData();
            dd.Count.Should().Be(0);
            dd.Fields.Should().BeEmpty();
        }

        // ─── Count ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Count_ReflectsNumberOfFields()
        {
            var dd = new DynamicData();
            dd.Add("a", 1);
            dd.Add("b", 2);
            dd.Count.Should().Be(2);
        }

        // ─── Add ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void Add_NewKey_AddsField()
        {
            var dd = new DynamicData();
            dd.Add("name", "Alice");
            dd.Fields.Should().ContainKey("name");
            dd.Fields["name"].Should().Be("Alice");
        }

        [TestMethod]
        public void Add_ExistingKey_OverwritesValue()
        {
            var dd = new DynamicData();
            dd.Add("name", "Alice");
            dd.Add("name", "Bob");
            dd.Fields["name"].Should().Be("Bob");
            dd.Count.Should().Be(1);
        }

        [TestMethod]
        public void Add_NullValue_StoresNull()
        {
            var dd = new DynamicData();
            dd.Add("key", null);
            dd.Fields.Should().ContainKey("key");
            dd.Fields["key"].Should().BeNull();
        }

        [TestMethod]
        public void Add_WithoutValue_StoresNull()
        {
            var dd = new DynamicData();
            dd.Add("key");
            dd.Fields.Should().ContainKey("key");
        }

        // ─── TryGetMember (dynamic access) ───────────────────────────────────

        [TestMethod]
        public void DynamicGet_ExistingKey_ReturnsValue()
        {
            dynamic dd = new DynamicData();
            dd.Foo = "bar";
            string? result = dd.Foo;
            result.Should().Be("bar");
        }

        [TestMethod]
        public void DynamicGet_NonExistingKey_ThrowsOrReturnsDefault()
        {
            dynamic dd = new DynamicData();
            // DynamicObject.TryGetMember falls back to base when key not found,
            // which throws RuntimeBinderException for missing members.
            Action act = () => { var _ = dd.NonExistent; };
            act.Should().Throw<Exception>();
        }

        // ─── TrySetMember (dynamic assignment) ───────────────────────────────

        [TestMethod]
        public void DynamicSet_NewMember_AddsToFields()
        {
            dynamic dd = new DynamicData();
            dd.Score = 42;
            ((DynamicData)dd).Fields.Should().ContainKey("Score");
            ((DynamicData)dd).Fields["Score"].Should().Be(42);
        }

        [TestMethod]
        public void DynamicSet_ExistingMember_OverwritesValue()
        {
            dynamic dd = new DynamicData();
            dd.Score = 42;
            dd.Score = 100;
            ((DynamicData)dd).Fields["Score"].Should().Be(100);
        }

        [TestMethod]
        public void DynamicSet_NullValue_StoresNull()
        {
            dynamic dd = new DynamicData();
            dd.Value = (object?)null;
            ((DynamicData)dd).Fields.Should().ContainKey("Value");
        }

        // ─── TryInvokeMember (dynamic method invocation) ─────────────────────

        [TestMethod]
        public void DynamicInvoke_StoredDelegate_InvokesAndReturnsResult()
        {
            dynamic dd = new DynamicData();
            Func<int, int> doubler = x => x * 2;
            dd.Double = doubler;
            int result = dd.Double(5);
            result.Should().Be(10);
        }

        [TestMethod]
        public void DynamicInvoke_NonDelegateField_ThrowsOrFails()
        {
            dynamic dd = new DynamicData();
            dd.NotADelegate = "hello";
            // Calling a non-delegate stored value as a method should throw
            Action act = () => { var _ = dd.NotADelegate(); };
            act.Should().Throw<Exception>();
        }

        [TestMethod]
        public void DynamicInvoke_MissingMethod_ThrowsOrFails()
        {
            dynamic dd = new DynamicData();
            Action act = () => { var _ = dd.MissingMethod(); };
            act.Should().Throw<Exception>();
        }

        // ─── Mixed usage ──────────────────────────────────────────────────────

        [TestMethod]
        public void Add_And_DynamicGet_AreEquivalent()
        {
            var dd = new DynamicData();
            dd.Add("X", 99);
            dynamic dyn = dd;
            int val = dyn.X;
            val.Should().Be(99);
        }

        [TestMethod]
        public void DynamicSet_And_AddOverwrite_AreEquivalent()
        {
            dynamic dd = new DynamicData();
            dd.Key = "original";
            ((DynamicData)dd).Add("Key", "updated");
            string val = dd.Key;
            val.Should().Be("updated");
        }

        [TestMethod]
        public void Add_MultipleFields_CountIsCorrect()
        {
            var dd = new DynamicData();
            for (int i = 0; i < 5; i++)
            {
                dd.Add($"field{i}", i);
            }
            dd.Count.Should().Be(5);
        }
    }
}
