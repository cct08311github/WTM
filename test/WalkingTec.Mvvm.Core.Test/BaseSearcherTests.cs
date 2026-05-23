#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Tests for BaseSearcher — covers pagination properties, DoInit/DoReInit events,
    /// VMFullName, DC fallback, UniqueId, CopyContext, and Validate.
    /// </summary>
    [TestClass]
    public class BaseSearcherTests
    {
        // ─── Concrete subclass ────────────────────────────────────────────────

        private sealed class ConcreteSearcher : BaseSearcher
        {
            public int InitCount { get; private set; }
            public int ReInitCount { get; private set; }

            protected override void InitVM()
            {
                InitCount++;
            }

            protected override void ReInitVM()
            {
                ReInitCount++;
                // deliberately NOT calling base so we can isolate the counter
            }
        }

        // ─── Pagination properties ────────────────────────────────────────────

        [TestMethod]
        public void Page_And_Limit_DefaultZero()
        {
            var s = new ConcreteSearcher();
            s.Page.Should().Be(0);
            s.Limit.Should().Be(0);
        }

        [TestMethod]
        public void Page_And_Limit_SetAndGet()
        {
            var s = new ConcreteSearcher { Page = 3, Limit = 25 };
            s.Page.Should().Be(3);
            s.Limit.Should().Be(25);
        }

        [TestMethod]
        public void Count_And_PageCount_SetAndGet()
        {
            var s = new ConcreteSearcher { Count = 100, PageCount = 4 };
            s.Count.Should().Be(100);
            s.PageCount.Should().Be(4);
        }

        // ─── FC dictionary ────────────────────────────────────────────────────

        [TestMethod]
        public void FC_IsInitialisedToEmptyDictionary()
        {
            var s = new ConcreteSearcher();
            s.FC.Should().NotBeNull();
            s.FC.Should().BeEmpty();
        }

        // ─── IsPlainText / IsEnumToString ─────────────────────────────────────

        [TestMethod]
        public void IsPlainText_DefaultNull_CanBeSet()
        {
            var s = new ConcreteSearcher();
            s.IsPlainText.Should().BeNull();
            s.IsPlainText = true;
            s.IsPlainText.Should().BeTrue();
        }

        [TestMethod]
        public void IsEnumToString_DefaultNull_CanBeSet()
        {
            var s = new ConcreteSearcher();
            s.IsEnumToString.Should().BeNull();
            s.IsEnumToString = false;
            s.IsEnumToString.Should().BeFalse();
        }

        // ─── VMFullName ───────────────────────────────────────────────────────

        [TestMethod]
        public void VMFullName_ContainsTypeAndAssembly()
        {
            var s = new ConcreteSearcher();
            var name = s.VMFullName;
            name.Should().Contain("ConcreteSearcher");
            // Must not contain ", Version=" — that's trimmed off
            name.Should().NotContain(", Version=");
        }

        // ─── DC fallback ──────────────────────────────────────────────────────

        [TestMethod]
        public void DC_FallsBackToWtm_When_LocalDcIsNull()
        {
            var s = new ConcreteSearcher();
            var wtm = MockWtmContext.CreateWtmContext();
            s.Wtm = wtm;

            s.DC.Should().BeSameAs(wtm.DC);
        }

        [TestMethod]
        public void DC_ReturnsLocalDc_When_Set()
        {
            var s = new ConcreteSearcher();
            var wtm = MockWtmContext.CreateWtmContext();
            s.Wtm = wtm;
            s.DC = wtm.DC; // set local
            s.DC.Should().BeSameAs(wtm.DC);
        }

        [TestMethod]
        public void DC_NullWithoutWtm_ReturnsNull()
        {
            var s = new ConcreteSearcher();
            // Wtm not set, DC not set
            s.DC.Should().BeNull();
        }

        // ─── UniqueId ─────────────────────────────────────────────────────────

        [TestMethod]
        public void UniqueId_NotEmpty()
        {
            var s = new ConcreteSearcher();
            s.UniqueId.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public void UniqueId_Stable_AcrossMultipleCalls()
        {
            var s = new ConcreteSearcher();
            var id1 = s.UniqueId;
            var id2 = s.UniqueId;
            id1.Should().Be(id2);
        }

        [TestMethod]
        public void UniqueId_DifferentInstances_AreDifferent()
        {
            var id1 = new ConcreteSearcher().UniqueId;
            var id2 = new ConcreteSearcher().UniqueId;
            id1.Should().NotBe(id2);
        }

        // ─── SortInfo ─────────────────────────────────────────────────────────

        [TestMethod]
        public void SortInfo_DefaultNull()
        {
            var s = new ConcreteSearcher();
            s.SortInfo.Should().BeNull();
        }

        [TestMethod]
        public void SortInfo_CanBeSet()
        {
            var s = new ConcreteSearcher();
            var si = new SortInfo { Property = "Name", Direction = SortDir.Asc };
            s.SortInfo = si;
            s.SortInfo.Should().BeSameAs(si);
        }

        // ─── IsExpanded ───────────────────────────────────────────────────────

        [TestMethod]
        public void IsExpanded_DefaultNull()
        {
            var s = new ConcreteSearcher();
            s.IsExpanded.Should().BeNull();
        }

        // ─── DoInit / OnAfterInit ─────────────────────────────────────────────

        [TestMethod]
        public void DoInit_CallsInitVM()
        {
            var s = new ConcreteSearcher();
            s.DoInit();
            s.InitCount.Should().Be(1);
        }

        [TestMethod]
        public void DoInit_FiresOnAfterInit_Event()
        {
            var s = new ConcreteSearcher();
            ISearcher? captured = null;
            s.OnAfterInit += sr => captured = sr;

            s.DoInit();

            captured.Should().BeSameAs(s);
        }

        [TestMethod]
        public void DoInit_WithNoHandler_DoesNotThrow()
        {
            var s = new ConcreteSearcher();
            Action act = () => s.DoInit();
            act.Should().NotThrow();
        }

        // ─── DoReInit / OnAfterReInit ─────────────────────────────────────────

        [TestMethod]
        public void DoReInit_CallsReInitVM()
        {
            var s = new ConcreteSearcher();
            s.DoReInit();
            s.ReInitCount.Should().Be(1);
        }

        [TestMethod]
        public void DoReInit_FiresOnAfterReInit_Event()
        {
            var s = new ConcreteSearcher();
            ISearcher? captured = null;
            s.OnAfterReInit += sr => captured = sr;

            s.DoReInit();

            captured.Should().BeSameAs(s);
        }

        // ─── Validate (virtual no-op) ─────────────────────────────────────────

        [TestMethod]
        public void Validate_DoesNotThrow()
        {
            var s = new ConcreteSearcher();
            Action act = () => s.Validate();
            act.Should().NotThrow();
        }

        // ─── CopyContext ──────────────────────────────────────────────────────

        [TestMethod]
        public void CopyContext_CopiesFC_Wtm_And_ViewDivId()
        {
            var wtm = MockWtmContext.CreateWtmContext();
            var sourceVm = new BaseVM { Wtm = wtm };
            sourceVm.FC["key"] = "value";
            sourceVm.ViewDivId = "div1";

            var target = new ConcreteSearcher();
            target.CopyContext(sourceVm);

            target.Wtm.Should().BeSameAs(wtm);
            target.FC.Should().ContainKey("key");
            target.ViewDivId.Should().Be("div1");
        }

        // ─── MSD / Session / LoginUserInfo via Wtm ────────────────────────────

        [TestMethod]
        public void MSD_NullWithoutWtm()
        {
            var s = new ConcreteSearcher();
            s.MSD.Should().BeNull();
        }

        [TestMethod]
        public void Session_NullWithoutWtm()
        {
            var s = new ConcreteSearcher();
            s.Session.Should().BeNull();
        }

        [TestMethod]
        public void LoginUserInfo_NullWithoutWtm()
        {
            var s = new ConcreteSearcher();
            s.LoginUserInfo.Should().BeNull();
        }

        [TestMethod]
        public void MSD_ReturnsFromWtm_WhenSet()
        {
            var s = new ConcreteSearcher();
            s.Wtm = MockWtmContext.CreateWtmContext();
            s.MSD.Should().NotBeNull();
        }
    }
}
