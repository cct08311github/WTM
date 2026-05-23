#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    [TestClass]
    public class EntityHelperTests
    {
        // ─── Fixture POCOs ────────────────────────────────────────────────────

        private class SimpleEntity
        {
            public string? Name { get; set; }
            public int Age { get; set; }
            public Guid? ExternalId { get; set; }
        }

        private enum StatusEnum { Active = 1, Inactive = 2 }

        private class EntityWithEnum
        {
            public string? Label { get; set; }
            public StatusEnum Status { get; set; }
            public StatusEnum? NullableStatus { get; set; }
        }

        // ─── GetEntityList<T> ─────────────────────────────────────────────────

        [TestMethod]
        public void GetEntityList_EmptyTable_ReturnsEmptyList()
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Age", typeof(int));

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result.Should().BeEmpty();
        }

        [TestMethod]
        public void GetEntityList_SingleRow_MapsStringAndInt()
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Age", typeof(int));
            table.Rows.Add("Alice", 30);

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("Alice");
            result[0].Age.Should().Be(30);
        }

        [TestMethod]
        public void GetEntityList_MultiRow_MapsAll()
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Age", typeof(int));
            table.Rows.Add("Alice", 30);
            table.Rows.Add("Bob", 25);

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result.Should().HaveCount(2);
            result[0].Name.Should().Be("Alice");
            result[1].Name.Should().Be("Bob");
        }

        [TestMethod]
        public void GetEntityList_NullCell_SetsPropertyToNull()
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Age", typeof(int));
            var row = table.NewRow();
            row["Name"] = DBNull.Value;
            row["Age"] = 20;
            table.Rows.Add(row);

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result[0].Name.Should().BeNull();
            result[0].Age.Should().Be(20);
        }

        [TestMethod]
        public void GetEntityList_GuidColumn_ParsesGuid()
        {
            var id = Guid.NewGuid();
            var table = new DataTable();
            table.Columns.Add("ExternalId", typeof(string));
            table.Rows.Add(id.ToString());

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result[0].ExternalId.Should().Be(id);
        }

        [TestMethod]
        public void GetEntityList_ExtraColumnsInTable_Ignored()
        {
            var table = new DataTable();
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("UnknownColumn", typeof(string));
            table.Rows.Add("Charlie", "ignored");

            var result = EntityHelper.GetEntityList<SimpleEntity>(table);
            result[0].Name.Should().Be("Charlie");
        }

        [TestMethod]
        public void GetEntityList_DynamicData_MapsAllColumns()
        {
            var table = new DataTable();
            table.Columns.Add("Foo", typeof(string));
            table.Columns.Add("Bar", typeof(int));
            table.Rows.Add("hello", 42);

            var result = EntityHelper.GetEntityList<DynamicData>(table);
            result.Should().HaveCount(1);
            result[0].Fields["Foo"].Should().Be("hello");
            result[0].Fields["Bar"].Should().Be(42);
        }

        [TestMethod]
        public void GetEntityList_DynamicData_DBNullBecomesNull()
        {
            var table = new DataTable();
            table.Columns.Add("Key", typeof(string));
            var row = table.NewRow();
            row["Key"] = DBNull.Value;
            table.Rows.Add(row);

            var result = EntityHelper.GetEntityList<DynamicData>(table);
            result[0].Fields["Key"].Should().BeNull();
        }

        // ─── ToDataTable<T> ───────────────────────────────────────────────────

        [TestMethod]
        public void ToDataTable_NullList_ReturnsNull()
        {
            EntityHelper.ToDataTable<SimpleEntity>(null!).Should().BeNull();
        }

        [TestMethod]
        public void ToDataTable_EmptyList_ReturnsNull()
        {
            EntityHelper.ToDataTable(new List<SimpleEntity>()).Should().BeNull();
        }

        [TestMethod]
        public void ToDataTable_SingleItem_CreatesRowWithValues()
        {
            var list = new List<SimpleEntity>
            {
                new SimpleEntity { Name = "Test", Age = 42 }
            };
            var dt = EntityHelper.ToDataTable(list);
            dt.Should().NotBeNull();
            dt!.Rows.Should().HaveCount(1);
            dt.Rows[0]["Name"].Should().Be("Test");
            dt.Rows[0]["Age"].Should().Be(42);
        }

        [TestMethod]
        public void ToDataTable_NullProperty_StoredAsDBNull()
        {
            var list = new List<SimpleEntity>
            {
                new SimpleEntity { Name = null, Age = 1 }
            };
            var dt = EntityHelper.ToDataTable(list);
            dt!.Rows[0]["Name"].Should().Be(DBNull.Value);
        }

        [TestMethod]
        public void ToDataTable_MultipleItems_AllRowsPresent()
        {
            var list = new List<SimpleEntity>
            {
                new SimpleEntity { Name = "A", Age = 1 },
                new SimpleEntity { Name = "B", Age = 2 },
                new SimpleEntity { Name = "C", Age = 3 },
            };
            var dt = EntityHelper.ToDataTable(list);
            dt!.Rows.Should().HaveCount(3);
        }

        // ─── ToDataSet<T> ─────────────────────────────────────────────────────

        [TestMethod]
        public void ToDataSet_NullList_ReturnsNull()
        {
            EntityHelper.ToDataSet<SimpleEntity>(null!).Should().BeNull();
        }

        [TestMethod]
        public void ToDataSet_EmptyList_ReturnsNull()
        {
            EntityHelper.ToDataSet(new List<SimpleEntity>()).Should().BeNull();
        }

        [TestMethod]
        public void ToDataSet_WithData_ContainsOneTable()
        {
            var list = new List<SimpleEntity>
            {
                new SimpleEntity { Name = "X", Age = 5 }
            };
            var ds = EntityHelper.ToDataSet(list);
            ds.Should().NotBeNull();
            ds!.Tables.Should().HaveCount(1);
            ds.Tables[0].Rows.Should().HaveCount(1);
        }
    }
}
