#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    [TestClass]
    public class ListExtensionTests
    {
        private class Person
        {
            public string? Name { get; set; }
            public int Age { get; set; }
        }

        // ─── ToListItems ──────────────────────────────────────────────────────

        [TestMethod]
        public void ToListItems_NullList_ReturnsEmptyList()
        {
            ((List<Person>)null!).ToListItems(x => x.Name!, x => x.Age)
                .Should().BeEmpty();
        }

        [TestMethod]
        public void ToListItems_EmptyList_ReturnsEmptyList()
        {
            new List<Person>().ToListItems(x => x.Name!, x => x.Age)
                .Should().BeEmpty();
        }

        [TestMethod]
        public void ToListItems_SingleItem_MapsTextAndValue()
        {
            var list = new List<Person> { new Person { Name = "Alice", Age = 30 } };
            var items = list.ToListItems(x => x.Name!, x => x.Age);
            items.Should().HaveCount(1);
            items[0].Text.Should().Be("Alice");
            items[0].Value.Should().Be("30");
            items[0].Selected.Should().BeFalse();
        }

        [TestMethod]
        public void ToListItems_MultipleItems_AllMapped()
        {
            var list = new List<Person>
            {
                new Person { Name = "Alice", Age = 30 },
                new Person { Name = "Bob", Age = 25 },
            };
            var items = list.ToListItems(x => x.Name!, x => x.Age);
            items.Should().HaveCount(2);
            items.Select(i => i.Text).Should().BeEquivalentTo(new[] { "Alice", "Bob" });
            items.Select(i => i.Value).Should().BeEquivalentTo(new[] { "30", "25" });
        }

        [TestMethod]
        public void ToListItems_WithSelectedCondition_MarksSomeSelected()
        {
            var list = new List<Person>
            {
                new Person { Name = "Alice", Age = 30 },
                new Person { Name = "Bob", Age = 25 },
            };
            var items = list.ToListItems(x => x.Name!, x => x.Age,
                selectedCondition: x => x.Age > 26);

            items[0].Selected.Should().BeTrue();  // Alice age 30 > 26
            items[1].Selected.Should().BeFalse(); // Bob age 25 <= 26
        }

        [TestMethod]
        public void ToListItems_NullTextFieldValue_ReturnsEmptyString()
        {
            var list = new List<Person> { new Person { Name = null, Age = 1 } };
            var items = list.ToListItems(x => x.Name!, x => x.Age);
            items[0].Text.Should().Be("");
        }

        // ─── ToChartData ──────────────────────────────────────────────────────

        [TestMethod]
        public void ToChartData_NullOrEmptyList_ReturnsEmptyDataset()
        {
            var emptyResult = ((List<ChartData>)null!).ToChartData();
            var dynEmpty = emptyResult as dynamic;
            // Should return object with empty series/legend/dataset
            emptyResult.Should().NotBeNull();

            var emptyList = new List<ChartData>();
            var emptyResult2 = emptyList.ToChartData();
            emptyResult2.Should().NotBeNull();
        }

        [TestMethod]
        public void ToChartData_SingleSeriesSingleCategory_ProducesDataset()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "A", Value = 10, Series = "S1" }
            };
            var result = data.ToChartData();
            result.Should().NotBeNull();
            // Verify the result has dataset/series/legend properties
            var type = result.GetType();
            type.GetProperty("dataset").Should().NotBeNull();
            type.GetProperty("series").Should().NotBeNull();
            type.GetProperty("legend").Should().NotBeNull();
        }

        [TestMethod]
        public void ToChartData_MultiSeriesMultiCategory_ProducesCorrectStructure()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "Q1", Value = 100, Series = "Revenue" },
                new ChartData { Category = "Q2", Value = 200, Series = "Revenue" },
                new ChartData { Category = "Q1", Value = 50, Series = "Costs" },
                new ChartData { Category = "Q2", Value = 75, Series = "Costs" },
            };
            var result = data.ToChartData();
            result.Should().NotBeNull();

            var dataset = result.GetType().GetProperty("dataset")!.GetValue(result) as string;
            dataset.Should().NotBeNullOrEmpty();
            dataset.Should().Contain("Revenue").And.Contain("Costs");
        }

        [TestMethod]
        public void ToChartData_ScatterData_ProducesScatterFormat()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "P1", Value = 5, ValueX = 10, Addition = 3, Series = "Set1" },
                new ChartData { Category = "P2", Value = 8, ValueX = 20, Addition = 5, Series = "Set1" },
            };
            var result = data.ToChartData();
            var dataset = result.GetType().GetProperty("dataset")!.GetValue(result) as string;
            dataset.Should().StartWith("[{\"source\":[");
        }

        [TestMethod]
        public void ToChartData_NullSeries_DefaultsToData()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "A", Value = 5, Series = null }
            };
            var result = data.ToChartData();
            var dataset = result.GetType().GetProperty("dataset")!.GetValue(result) as string;
            dataset.Should().Contain("Data");
        }

        [TestMethod]
        public void ToChartData_CustomSeriesName_AppearsInDataset()
        {
            var data = new List<ChartData>
            {
                new ChartData { Category = "A", Value = 5, Series = "Alpha" }
            };
            var result = data.ToChartData(seriesname: "MyInfo");
            var dataset = result.GetType().GetProperty("dataset")!.GetValue(result) as string;
            dataset.Should().Contain("MyInfo");
        }
    }
}
