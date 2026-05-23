#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Unit tests for <see cref="JsonResultT{T}"/> and
    /// <see cref="DataTableResult{T}"/> — Issue #36.
    /// Verifies all public properties are readable/writable.
    /// </summary>
    [TestClass]
    public class JsonResultModelTests
    {
        // ─── JsonResultT<T> ────────────────────────────────────────────────────

        [TestMethod]
        public void JsonResultT_DefaultValues_AreExpected()
        {
            var model = new JsonResultT<string>();

            model.Code.Should().Be(0);
            model.Msg.Should().BeNull();
            model.Data.Should().BeNull();
        }

        [TestMethod]
        public void JsonResultT_CodeProperty_CanBeSetAndRead()
        {
            var model = new JsonResultT<int> { Code = 200 };

            model.Code.Should().Be(200);
        }

        [TestMethod]
        public void JsonResultT_MsgProperty_CanBeSetAndRead()
        {
            var model = new JsonResultT<string> { Msg = "ok" };

            model.Msg.Should().Be("ok");
        }

        [TestMethod]
        public void JsonResultT_DataProperty_CanBeSetAndRead()
        {
            var model = new JsonResultT<string> { Data = "payload" };

            model.Data.Should().Be("payload");
        }

        [TestMethod]
        public void JsonResultT_WithComplexData_RoundTrips()
        {
            var list = new List<int> { 1, 2, 3 };
            var model = new JsonResultT<List<int>>
            {
                Code = 200,
                Msg  = "success",
                Data = list
            };

            model.Code.Should().Be(200);
            model.Msg.Should().Be("success");
            model.Data.Should().BeSameAs(list);
        }

        // ─── DataTableResult<T> ────────────────────────────────────────────────

        [TestMethod]
        public void DataTableResult_DefaultValues_AreExpected()
        {
            var model = new DataTableResult<School>();

            model.Code.Should().Be(0);
            model.Msg.Should().BeNull();
            model.Data.Should().BeNull();
            model.Count.Should().Be(0L);
        }

        [TestMethod]
        public void DataTableResult_CountProperty_CanBeSetAndRead()
        {
            var model = new DataTableResult<School> { Count = 42L };

            model.Count.Should().Be(42L);
        }

        [TestMethod]
        public void DataTableResult_DataProperty_CanBeSetAndRead()
        {
            var schools = new List<School>();
            var model = new DataTableResult<School>
            {
                Code  = 200,
                Msg   = "ok",
                Data  = schools,
                Count = 0
            };

            model.Data.Should().BeSameAs(schools);
            model.Code.Should().Be(200);
            model.Msg.Should().Be("ok");
        }

        [TestMethod]
        public void DataTableResult_InheritsFromJsonResultT()
        {
            var model = new DataTableResult<School>();

            model.Should().BeAssignableTo<JsonResultT<IEnumerable<School>>>();
        }
    }
}
