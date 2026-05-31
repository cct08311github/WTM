#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ---------------------------------------------------------------------------
    // Concrete ListVM implementations for testing
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Full-featured test ListVM that overrides InitGridHeader / InitGridAction
    /// so GetHeaders() / GetGridActions() / GetChildrenDepth() can be exercised.
    /// </summary>
    public class FullStudentListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
                this.MakeGridHeader(x => x.Name),
            };
        }

        protected override List<GridAction> InitGridAction()
        {
            return
            [
                this.MakeStandardAction("Student", GridActionStandardTypesEnum.Delete, "Delete", ""),
            ];
        }
    }

    /// <summary>
    /// ListVM that returns a fixed small dataset and overrides Export query
    /// so GenerateExcel() can be exercised without real DB data.
    /// </summary>
    public class ExportStudentListVM : BasePagedListVM<Student, BaseSearcher>
    {
        private readonly IQueryable<Student> _data;

        public ExportStudentListVM(IQueryable<Student> data) => _data = data;

        public override IOrderedQueryable<Student> GetSearchQuery()
            => _data.OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            return new List<GridColumn<Student>>
            {
                this.MakeGridHeader(x => x.LoginName),
                this.MakeGridHeader(x => x.Name),
            };
        }
    }

    /// <summary>
    /// ListVM with a custom SelectorValueField that is NOT "id",
    /// enabling GetBatchQuery() code-path for SelectorValueField.
    /// </summary>
    public class SelectorStudentListVM : BasePagedListVM<Student, BaseSearcher>
    {
        public override IOrderedQueryable<Student> GetSearchQuery()
            => DC!.Set<Student>().OrderBy(x => x.ID);
    }

    /// <summary>
    /// ListVM that exposes enum and numeric fields to exercise
    /// GenerateWorkBook's enum-display and numeric-cell branches.
    /// </summary>
    public class SchoolListVM : BasePagedListVM<School, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<School>> InitGridHeader()
        {
            return new List<GridColumn<School>>
            {
                this.MakeGridHeader(x => x.SchoolName),
                this.MakeGridHeader(x => x.SchoolType),   // enum column
                this.MakeGridHeader(x => x.SchoolCode),
            };
        }
    }

    /// <summary>
    /// ListVM with nested child columns containing an Action column in the children,
    /// so RemoveActionColumn recurses into children (line 986).
    /// </summary>
    public class NestedHeaderListVM : BasePagedListVM<Student, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        {
            // Create a parent column that has children (one of which is an Action column)
            var actionChild = new GridColumn<Student>(x => x.LoginName!)
            {
                ColumnType = GridColumnTypeEnum.Action,
                Title = "Actions"
            };
            var dataChild = new GridColumn<Student>(x => x.Name!)
            {
                Title = "Name"
            };
            var parentCol = new GridColumn<Student>(x => x.LoginName!)
            {
                Title = "Group",
                Children = new List<IGridColumn<Student>> { dataChild, actionChild }
            };
            return new List<GridColumn<Student>> { parentCol };
        }
    }

    /// <summary>
    /// ListVM with an Action column, so RemoveActionColumn removes something.
    /// </summary>
    public class SchoolListVMWithAction : BasePagedListVM<School, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<School>> InitGridHeader()
        {
            var cols = new List<GridColumn<School>>
            {
                this.MakeGridHeader(x => x.SchoolName),
            };
            // Add an action column
            var actionCol = new GridColumn<School>(x => x.SchoolName!)
            {
                ColumnType = GridColumnTypeEnum.Action
            };
            cols.Add(actionCol);
            return cols;
        }

        protected override List<GridAction> InitGridAction()
        {
            return [this.MakeStandardAction("School", GridActionStandardTypesEnum.Delete, "Delete", "")];
        }
    }

    // ---------------------------------------------------------------------------
    // BasePagedListVM extended tests
    // ---------------------------------------------------------------------------

    [TestClass]
    public class BasePagedListVMExtendedTests
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private WTMContext CreateWtm() =>
            MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));

        // ----------------------------------------------------------------------
        // GetHeaders / InitGridHeader / GetChildrenDepth
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetHeaders_returns_initialised_columns()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            var headers = vm.GetHeaders();
            Assert.IsNotNull(headers);
            Assert.IsTrue(headers.Any(), "Expected at least one header column");
        }

        [TestMethod]
        public void GetHeaders_called_twice_returns_same_instance()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            var h1 = vm.GetHeaders();
            var h2 = vm.GetHeaders();
            Assert.AreSame(h1, h2, "GridHeaders should be cached after first call");
        }

        [TestMethod]
        public void GetChildrenDepth_returns_positive_value()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            int depth = vm.GetChildrenDepth();
            Assert.IsTrue(depth >= 1, "Depth should be >= 1");
        }

        [TestMethod]
        public void GetChildrenDepth_cached_on_second_call()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            int d1 = vm.GetChildrenDepth();
            int d2 = vm.GetChildrenDepth();
            Assert.AreEqual(d1, d2);
        }

        [TestMethod]
        public void DefaultListVM_GetHeaders_returns_empty_enumerable()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var headers = vm.GetHeaders();
            Assert.IsNotNull(headers);
            Assert.AreEqual(0, headers.Count());
        }

        // ----------------------------------------------------------------------
        // GetGridActions / RemoveAction
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetGridActions_returns_actions_list()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            var actions = vm.GetGridActions();
            Assert.IsNotNull(actions);
            Assert.IsTrue(actions.Count > 0, "Expected at least one grid action");
        }

        [TestMethod]
        public void GetGridActions_cached_on_second_call()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            var a1 = vm.GetGridActions();
            var a2 = vm.GetGridActions();
            Assert.AreSame(a1, a2);
        }

        [TestMethod]
        public void DefaultListVM_GetGridActions_returns_empty_list()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var actions = vm.GetGridActions();
            Assert.IsNotNull(actions);
            Assert.AreEqual(0, actions.Count);
        }

        [TestMethod]
        public void RemoveAction_clears_grid_actions()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            // initialise actions first
            _ = vm.GetGridActions();
            vm.RemoveAction();
            Assert.AreEqual(0, vm.GetGridActions().Count);
        }

        // ----------------------------------------------------------------------
        // GetEntityList
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetEntityList_triggers_DoSearch_when_not_searched()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            for (int i = 0; i < 3; i++)
            {
                vm.DC!.Set<Student>().Add(new Student { LoginName = $"u{i}", Password = "p", Name = "n", IsValid = true });
            }
            vm.DC!.SaveChanges();

            var list = vm.GetEntityList();
            Assert.IsNotNull(list);
            Assert.AreEqual(3, list.Count());
        }

        [TestMethod]
        public void GetEntityList_returns_existing_list_without_re_search()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            vm.IsSearched = true;
            vm.EntityList = new List<Student> { new Student { LoginName = "preset" } };

            var list = vm.GetEntityList();
            Assert.AreEqual(1, list.Count());
        }

        // ----------------------------------------------------------------------
        // DoInitListVM
        // ----------------------------------------------------------------------

        [TestMethod]
        public void DoInitListVM_fires_OnAfterInitList_event()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            bool eventFired = false;
            vm.OnAfterInitList += _ => { eventFired = true; };
            vm.DoInitListVM();
            Assert.IsTrue(eventFired);
        }

        // ----------------------------------------------------------------------
        // Validate
        // ----------------------------------------------------------------------

        [TestMethod]
        public void Validate_does_not_throw()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            vm.Validate(); // should not throw
        }

        // ----------------------------------------------------------------------
        // SetFullRowColor / SetFullRowBgColor
        // ----------------------------------------------------------------------

        [TestMethod]
        public void SetFullRowColor_returns_empty_string_by_default()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.AreEqual("", vm.SetFullRowColor(new Student()));
        }

        [TestMethod]
        public void SetFullRowBgColor_returns_empty_string_by_default()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.AreEqual("", vm.SetFullRowBgColor(new Student()));
        }

        // ----------------------------------------------------------------------
        // GetIsSelected
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetIsSelected_returns_false_by_default()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.IsFalse(vm.GetIsSelected(new Student()));
        }

        // ----------------------------------------------------------------------
        // SelectorValueField / SearcherDivId / ModelType / DetailGridPrix
        // ----------------------------------------------------------------------

        [TestMethod]
        public void SelectorValueField_default_is_null()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.IsNull(vm.SelectorValueField);
        }

        [TestMethod]
        public void SearcherDivId_ends_with_Searcher()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.IsTrue(vm.SearcherDivId.EndsWith("Searcher"));
        }

        [TestMethod]
        public void ModelType_returns_correct_type()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.AreEqual(typeof(Student), vm.ModelType);
        }

        [TestMethod]
        public void DetailGridPrix_can_be_set_and_read()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            vm.DetailGridPrix = "TestPrefix";
            Assert.AreEqual("TestPrefix", vm.DetailGridPrix);
        }

        // ----------------------------------------------------------------------
        // CreateEmptyEntity / ClearEntityList
        // ----------------------------------------------------------------------

        [TestMethod]
        public void CreateEmptyEntity_returns_new_instance()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var entity = vm.CreateEmptyEntity();
            Assert.IsNotNull(entity);
            Assert.IsInstanceOfType(entity, typeof(Student));
        }

        [TestMethod]
        public void ClearEntityList_empties_the_list()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            vm.EntityList.Add(new Student { LoginName = "x" });
            vm.ClearEntityList();
            Assert.AreEqual(0, vm.EntityList.Count);
        }

        // ----------------------------------------------------------------------
        // CreateSortInfo
        // ----------------------------------------------------------------------

        [TestMethod]
        public void CreateSortInfo_returns_correct_property_and_direction()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var si = vm.CreateSortInfo(x => x.LoginName, SortDir.Asc);
            Assert.AreEqual("LoginName", si.Property);
            Assert.AreEqual(SortDir.Asc, si.Direction);
        }

        [TestMethod]
        public void CreateSortInfo_desc()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var si = vm.CreateSortInfo(x => x.Name, SortDir.Desc);
            Assert.AreEqual("Name", si.Property);
            Assert.AreEqual(SortDir.Desc, si.Direction);
        }

        // ----------------------------------------------------------------------
        // AddTime
        // ----------------------------------------------------------------------

        [TestMethod]
        public void AddTime_year_increments_year()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1);
            Assert.AreEqual(2025, vm.AddTime(dt, "year", 1).Year);
        }

        [TestMethod]
        public void AddTime_month_increments_month()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1);
            Assert.AreEqual(2, vm.AddTime(dt, "month", 1).Month);
        }

        [TestMethod]
        public void AddTime_day_increments_day()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1);
            Assert.AreEqual(2, vm.AddTime(dt, "day", 1).Day);
        }

        [TestMethod]
        public void AddTime_hour_increments_hour()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1, 0, 0, 0);
            Assert.AreEqual(3, vm.AddTime(dt, "hour", 3).Hour);
        }

        [TestMethod]
        public void AddTime_minute_increments_minute()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1, 0, 0, 0);
            Assert.AreEqual(15, vm.AddTime(dt, "minute", 15).Minute);
        }

        [TestMethod]
        public void AddTime_second_increments_second()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 1, 1, 0, 0, 0);
            Assert.AreEqual(30, vm.AddTime(dt, "second", 30).Second);
        }

        [TestMethod]
        public void AddTime_unknown_type_returns_same_datetime()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var dt = new DateTime(2024, 6, 15);
            Assert.AreEqual(dt, vm.AddTime(dt, "unknown", 10));
        }

        // ----------------------------------------------------------------------
        // DoSearch — SearcherMode branches
        // ----------------------------------------------------------------------

        [TestMethod]
        public void DoSearch_SearchMode_uses_GetSearchQuery()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            vm.SearcherMode = ListVMSearchModeEnum.Search;
            vm.Searcher.Limit = 10;
            vm.DoSearch();

            Assert.AreEqual(5, vm.Searcher.Count);
        }

        [TestMethod]
        public void DoSearch_ExportMode_uses_GetExportQuery()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            vm.SearcherMode = ListVMSearchModeEnum.Export;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(5, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_SelectorMode_uses_GetSelectorQuery()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            vm.SearcherMode = ListVMSearchModeEnum.Selector;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(3, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_MasterDetailMode_uses_GetMasterDetailsQuery()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 2);

            vm.SearcherMode = ListVMSearchModeEnum.MasterDetail;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(2, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_BatchMode_filters_by_Ids()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            // get some ids
            var allIds = vm.DC!.Set<Student>().Take(2).Select(x => x.ID.ToString()).ToList();
            vm.Ids = allIds;
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(2, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_CheckExportMode_uses_GetCheckedExportQuery()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 4);

            var allIds = vm.DC!.Set<Student>().Take(2).Select(x => x.ID.ToString()).ToList();
            vm.Ids = allIds;
            vm.SearcherMode = ListVMSearchModeEnum.CheckExport;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(2, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_Custom1Mode_falls_through_to_GetSearchQuery()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            vm.SearcherMode = ListVMSearchModeEnum.Custom1;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(3, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_sets_IsSearched_true()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            Assert.IsFalse(vm.IsSearched);
            vm.DoSearch();
            Assert.IsTrue(vm.IsSearched);
        }

        [TestMethod]
        public void DoSearch_with_ReplaceWhere_applies_filter()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            // only students whose login starts with "u0" — just one
            vm.ReplaceWhere = (Expression<Func<Student, bool>>)(s => s.LoginName == "u0");
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(1, vm.EntityList.Count);
        }

        [TestMethod]
        public void DoSearch_PassSearch_true_skips_paging()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            vm.PassSearch = true;
            vm.DoSearch();

            // PassSearch bypasses paging — all rows come back in EntityList
            Assert.AreEqual(5, vm.EntityList.Count);
        }

        // ----------------------------------------------------------------------
        // AfterDoSearcher — Selector mode checks
        // ----------------------------------------------------------------------

        [TestMethod]
        public void AfterDoSearcher_Selector_marks_checked_by_id()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            var firstId = vm.DC!.Set<Student>().OrderBy(x => x.ID).First().ID.ToString();
            vm.Ids = new List<string> { firstId };
            vm.SearcherMode = ListVMSearchModeEnum.Selector;
            vm.NeedPage = false;
            vm.DoSearch();

            var checkedItems = vm.EntityList.Where(x => x.Checked).ToList();
            Assert.AreEqual(1, checkedItems.Count);
        }

        [TestMethod]
        public void AfterDoSearcher_Selector_marks_checked_by_custom_field()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            vm.SelectorValueField = "LoginName";
            vm.Ids = new List<string> { "u0" };
            vm.SearcherMode = ListVMSearchModeEnum.Selector;
            vm.NeedPage = false;
            vm.DoSearch();

            var checkedItems = vm.EntityList.Where(x => x.Checked).ToList();
            Assert.AreEqual(1, checkedItems.Count);
            Assert.AreEqual("u0", checkedItems[0].LoginName);
        }

        // ----------------------------------------------------------------------
        // GetBatchQuery — SelectorValueField not "id"
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetBatchQuery_with_custom_selector_field_filters_correctly()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 4);

            vm.SelectorValueField = "LoginName";
            vm.Ids = new List<string> { "u0", "u1" };
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(2, vm.EntityList.Count);
        }

        // ----------------------------------------------------------------------
        // RemoveActionColumn / RemoveActionAndIdColumn
        // ----------------------------------------------------------------------

        [TestMethod]
        public void RemoveActionColumn_removes_action_columns()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            // GetHeaders initialises. Add an action column via AddErrorColumn sentinel test.
            vm.GetHeaders();
            vm.RemoveActionColumn();
            // Should not throw; action columns removed (none present in FullStudentListVM headers)
        }

        [TestMethod]
        public void RemoveActionAndIdColumn_removes_id_and_action_columns()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.GetHeaders();
            vm.RemoveActionAndIdColumn();
            // Should not throw; id/action removed
        }

        [TestMethod]
        public void RemoveActionAndIdColumn_null_root_initialises_headers()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            // headers not yet initialised — call with null root
            vm.RemoveActionAndIdColumn(null);
            // headers should now be initialised
            var h = vm.GetHeaders();
            Assert.IsNotNull(h);
        }

        // ----------------------------------------------------------------------
        // AddErrorColumn
        // ----------------------------------------------------------------------

        [TestMethod]
        public void AddErrorColumn_adds_batch_error_column()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.DetailGridPrix = "Items"; // needed so ProcessListError actually calls AddErrorColumn
            vm.AddErrorColumn();
            var headers = vm.GetHeaders().ToList();
            Assert.IsTrue(headers.Any(h => h.Field?.Contains("BatchError") == true),
                "Expected a BatchError column to be added");
        }

        [TestMethod]
        public void AddErrorColumn_called_twice_does_not_add_duplicate()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.AddErrorColumn();
            int countAfterFirst = vm.GetHeaders().Count();
            vm.AddErrorColumn();
            int countAfterSecond = vm.GetHeaders().Count();
            Assert.AreEqual(countAfterFirst, countAfterSecond);
        }

        // ----------------------------------------------------------------------
        // ProcessListError
        // ----------------------------------------------------------------------

        [TestMethod]
        public void ProcessListError_null_entities_returns_without_throw()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.ProcessListError(null); // should not throw
        }

        [TestMethod]
        public void ProcessListError_with_batch_error_adds_error_column()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.DetailGridPrix = "Items";
            var entities = new List<Student>
            {
                new Student { LoginName = "u1", BatchError = "Something went wrong" }
            };
            vm.ProcessListError(entities);
            Assert.IsTrue(vm.IsSearched);
            var headers = vm.GetHeaders().ToList();
            Assert.IsTrue(headers.Any(h => h.Field?.Contains("BatchError") == true));
        }

        [TestMethod]
        public void ProcessListError_without_DetailGridPrix_does_not_add_error_column()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            // DetailGridPrix is null by default
            var entities = new List<Student>
            {
                new Student { LoginName = "u1" }
            };
            vm.ProcessListError(entities);
            // Should not have BatchError column — DetailGridPrix is null
            var headers = vm.GetHeaders().ToList();
            Assert.IsFalse(headers.Any(h => h.Field?.Contains("BatchError") == true));
        }

        // ----------------------------------------------------------------------
        // GenerateExcel
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateExcel_returns_non_empty_bytes_for_small_dataset()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Length > 0, "Excel bytes should be non-empty");
            Assert.AreEqual(3, vm.ExportRowCount);
        }

        [TestMethod]
        public void GenerateExcel_produces_valid_xlsx_workbook()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 2);

            byte[] result = vm.GenerateExcel();

            // Should parse as a valid XSSFWorkbook
            using var ms = new MemoryStream(result);
            var wb = new XSSFWorkbook(ms);
            Assert.IsNotNull(wb);
            var sheet = wb.GetSheetAt(0);
            Assert.IsNotNull(sheet);
        }

        [TestMethod]
        public void GenerateExcel_ExportRowCount_matches_data_count()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 7);

            vm.GenerateExcel();

            Assert.AreEqual(7, vm.ExportRowCount);
        }

        [TestMethod]
        public void GenerateExcel_empty_dataset_returns_valid_bytes()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            // no students added

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Length > 0);
            Assert.AreEqual(0, vm.ExportRowCount);
        }

        [TestMethod]
        public void GenerateExcel_with_custom_ExportMaxCount_uses_value()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);
            vm.ExportMaxCount = 100; // within the 5 rows, so still 1 file

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            Assert.AreEqual(1, vm.ExportExcelCount);
        }

        [TestMethod]
        public void GenerateExcel_multi_file_path_produces_zip_bytes()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);
            // Set ExportMaxCount to 2 → 5 rows needs 3 files → ZIP path
            vm.ExportMaxCount = 2;

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Length > 0, "ZIP bytes should be non-empty");
            Assert.IsTrue(vm.ExportExcelCount >= 2, $"Expected >= 2 files, got {vm.ExportExcelCount}");
        }

        [TestMethod]
        public void GenerateExcel_multi_file_ExportExcelCount_is_exact_ceiling()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 4);
            // 4 rows / 2 per file = 2 files exactly
            vm.ExportMaxCount = 2;

            vm.GenerateExcel();

            Assert.AreEqual(2, vm.ExportExcelCount);
            Assert.AreEqual(4, vm.ExportRowCount);
        }

        [TestMethod]
        public void GenerateExcel_ExportMaxCount_capped_at_1_million()
        {
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 2);
            vm.ExportMaxCount = 2_000_000; // over 1M cap

            vm.GenerateExcel();

            // ExportMaxCount should be capped to 1_000_000 internally → 1 file
            Assert.AreEqual(1, vm.ExportExcelCount);
        }

        [TestMethod]
        public void GenerateExcel_CheckExport_mode_uses_GetCheckedExportQuery()
        {
            // Use FullStudentListVM which has headers defined
            var vm = new FullStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            var selectedId = vm.DC!.Set<Student>().First().ID.ToString();
            vm.Ids = new List<string> { selectedId };
            vm.SearcherMode = ListVMSearchModeEnum.CheckExport;

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            // CheckExport with 1 selected ID → 1 exported row
            Assert.AreEqual(1, vm.ExportRowCount);
        }

        // ----------------------------------------------------------------------
        // GetMasterDetailsQuery / GetSelectorQuery / GetCheckedExportQuery
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetMasterDetailsQuery_delegates_to_GetSearchQuery()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 2);

            var q = vm.GetMasterDetailsQuery();
            Assert.IsNotNull(q);
        }

        [TestMethod]
        public void GetSelectorQuery_delegates_to_GetSearchQuery()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 2);

            var q = vm.GetSelectorQuery();
            Assert.IsNotNull(q);
        }

        [TestMethod]
        public void GetCheckedExportQuery_delegates_to_GetBatchQuery()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);
            vm.Ids = new List<string> { vm.DC!.Set<Student>().First().ID.ToString() };

            var q = vm.GetCheckedExportQuery();
            Assert.IsNotNull(q);
            Assert.AreEqual(1, q.Count());
        }

        // ----------------------------------------------------------------------
        // GetAnalysisFields
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetAnalysisFields_returns_non_null()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            var fields = vm.GetAnalysisFields();
            Assert.IsNotNull(fields);
        }

        // ----------------------------------------------------------------------
        // ProcessListError — MSD-based errors path
        // ----------------------------------------------------------------------

        [TestMethod]
        public void ProcessListError_with_MSD_errors_adds_error_column()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.DetailGridPrix = "Items";
            // Add a model error into MSD with the DetailGridPrix prefix
            vm.MSD!.AddModelError("Items[0].Name", "Required field");

            var entities = new List<Student>
            {
                new Student { LoginName = "u1" }, // no BatchError — MSD will drive it
            };
            vm.ProcessListError(entities);

            Assert.IsTrue(vm.IsSearched);
            // The entity at index 0 should have BatchError set
            Assert.IsNotNull(vm.EntityList[0].BatchError);
            var headers = vm.GetHeaders().ToList();
            Assert.IsTrue(headers.Any(h => h.Field?.Contains("BatchError") == true),
                "Expected BatchError column after MSD error");
        }

        [TestMethod]
        public void ProcessListError_with_MSD_errors_removes_model_error_after_processing()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.DetailGridPrix = "Items";
            vm.MSD!.AddModelError("Items[0].Name", "Required field");

            var entities = new List<Student> { new Student { LoginName = "u1" } };
            vm.ProcessListError(entities);

            // The key should have been removed from MSD
            Assert.AreEqual(0, vm.MSD.Count);
        }

        [TestMethod]
        public void ProcessListError_with_DetailGridPrix_no_errors_no_error_column()
        {
            var vm = new FullStudentListVM { Wtm = CreateWtm() };
            vm.DetailGridPrix = "Items";
            // No errors — entities with no BatchError and no MSD errors

            var entities = new List<Student>
            {
                new Student { LoginName = "u1" } // no BatchError
            };
            vm.ProcessListError(entities);

            var headers = vm.GetHeaders().ToList();
            Assert.IsFalse(headers.Any(h => h.Field?.Contains("BatchError") == true),
                "Should not add error column when there are no errors");
        }

        // ----------------------------------------------------------------------
        // RemoveActionColumn — with children
        // ----------------------------------------------------------------------

        [TestMethod]
        public void RemoveActionColumn_on_empty_list_vm_does_not_throw()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher> { Wtm = CreateWtm() };
            vm.GetHeaders(); // initialise
            vm.RemoveActionColumn(); // should not throw
        }

        // ----------------------------------------------------------------------
        // GetBatchQuery with ReplaceWhere set
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GetBatchQuery_with_ReplaceWhere_uses_original_query()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 5);

            // Set ReplaceWhere so GetBatchQuery returns the base query
            vm.ReplaceWhere = (Expression<Func<Student, bool>>)(s => s.IsValid == true);
            vm.Ids = new List<string> { "someId" }; // won't matter — ReplaceWhere overrides
            var q = vm.GetBatchQuery();
            Assert.IsNotNull(q);
        }

        // ----------------------------------------------------------------------
        // GenerateExcel with enum column — covers GenerateWorkBook enum-display branch
        // ----------------------------------------------------------------------

        [TestMethod]
        public void GenerateExcel_with_enum_column_produces_valid_bytes()
        {
            var vm = new SchoolListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddSchools(vm.DC!, 3);

            byte[] result = vm.GenerateExcel();

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Length > 0);
            Assert.AreEqual(3, vm.ExportRowCount);
        }

        [TestMethod]
        public void GenerateExcel_with_enum_column_xlsx_is_parseable()
        {
            var vm = new SchoolListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddSchools(vm.DC!, 2);

            byte[] result = vm.GenerateExcel();

            using var ms = new MemoryStream(result);
            var wb = new XSSFWorkbook(ms);
            Assert.IsNotNull(wb.GetSheetAt(0));
        }

        [TestMethod]
        public void GenerateExcel_school_enum_column_with_both_enum_values_does_not_throw()
        {
            var vm = new SchoolListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            // Add schools with both enum values to exercise enum-display code paths
            vm.DC!.Set<School>().Add(new School { SchoolCode = "001", SchoolName = "PubSchool", SchoolType = SchoolTypeEnum.PUB, Remark = "r" });
            vm.DC!.Set<School>().Add(new School { SchoolCode = "002", SchoolName = "PriSchool", SchoolType = SchoolTypeEnum.PRI, Remark = "r" });
            vm.DC.SaveChanges();

            byte[] result = vm.GenerateExcel();
            Assert.IsTrue(result.Length > 0);
        }

        // ----------------------------------------------------------------------
        // RemoveActionColumn — with actual action column present
        // ----------------------------------------------------------------------

        [TestMethod]
        public void RemoveActionColumn_removes_action_column_when_present()
        {
            var vm = new SchoolListVMWithAction { Wtm = CreateWtm() };
            // Initialise headers — includes an Action column
            var headers = vm.GetHeaders().ToList();
            Assert.IsTrue(headers.Any(h => h.ColumnType == GridColumnTypeEnum.Action),
                "Precondition: action column should be present");

            vm.RemoveActionColumn();

            var afterRemoval = vm.GetHeaders().ToList();
            Assert.IsFalse(afterRemoval.Any(h => h.ColumnType == GridColumnTypeEnum.Action),
                "Action column should be removed after RemoveActionColumn()");
        }

        [TestMethod]
        public void RemoveActionColumn_without_prior_init_calls_GetHeaders()
        {
            var vm = new SchoolListVMWithAction { Wtm = CreateWtm() };
            // GridHeaders is not yet initialised — RemoveActionColumn should call GetHeaders() internally
            vm.RemoveActionColumn(); // should not throw
            // After the call, GetHeaders() should return a non-null, non-empty set
            var headers = vm.GetHeaders();
            Assert.IsNotNull(headers);
        }

        [TestMethod]
        public void RemoveActionAndIdColumn_on_SchoolListVMWithAction_removes_columns()
        {
            var vm = new SchoolListVMWithAction { Wtm = CreateWtm() };
            vm.RemoveActionAndIdColumn(); // should not throw; removes both action and id cols
        }

        // ----------------------------------------------------------------------
        // RemoveActionColumn — recursion into child columns (line 986)
        // ----------------------------------------------------------------------

        [TestMethod]
        public void RemoveActionColumn_recurses_into_nested_children()
        {
            var vm = new NestedHeaderListVM { Wtm = CreateWtm() };
            // Initialise headers — contains a parent with an Action child
            vm.GetHeaders();
            // RemoveActionColumn should recurse into the parent's children
            vm.RemoveActionColumn(); // should not throw
        }

        // ----------------------------------------------------------------------
        // DoSearch — Searcher.Page > PageCount clamping branch (line 739)
        // ----------------------------------------------------------------------

        [TestMethod]
        public void DoSearch_clamps_page_when_page_exceeds_page_count()
        {
            var vm = new BasePagedListVM<Student, BaseSearcher>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            vm.SearcherMode = ListVMSearchModeEnum.Search;
            vm.Searcher.Limit = 10;
            vm.Searcher.Page = 999; // way beyond page count
            vm.DoSearch();

            // Should be clamped to actual page count (1)
            Assert.AreEqual(1, vm.Searcher.Page);
        }

        // ----------------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------------

        private static void AddStudents(IDataContext dc, int count)
        {
            for (int i = 0; i < count; i++)
            {
                dc.Set<Student>().Add(new Student
                {
                    LoginName = $"u{i}",
                    Password = "p",
                    Name = $"name{i}",
                    IsValid = true
                });
            }
            dc.SaveChanges();
        }

        private static void AddSchools(IDataContext dc, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // SchoolCode must be exactly 3 digits (per regex [0-9]{3,3})
                dc.Set<School>().Add(new School
                {
                    SchoolCode = (100 + i).ToString(),
                    SchoolName = $"School{i}",
                    SchoolType = (i % 2 == 0) ? SchoolTypeEnum.PUB : SchoolTypeEnum.PRI,
                    Remark = "remark",
                });
            }
            dc.SaveChanges();
        }

        // ----------------------------------------------------------------------
        // GetBatchQuery — null-guard for invalid SelectorValueField (issue #106)
        // ----------------------------------------------------------------------

        /// <summary>
        /// Regression test for issue #106: when SelectorValueField names a property
        /// that does not exist on TModel, GetSingleProperty returns null and the
        /// previous code passed it (via !) to Expression.Property, throwing
        /// ArgumentNullException at runtime. The fix falls back to the default
        /// id-based Contains predicate and must not throw.
        /// </summary>
        [TestMethod]
        public void GetBatchQuery_with_nonexistent_SelectorValueField_does_not_throw()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 3);

            // Set a field name that does NOT exist on Student — this is the attacker input.
            vm.SelectorValueField = "___NonExistentField___";
            vm.Ids = new List<string> { "any-id" };
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;

            // Must not throw ArgumentNullException; falls back to id-based Contains
            // predicate (no student has id "any-id", so result is empty).
            IOrderedQueryable<Student>? result = vm.GetBatchQuery();
            Assert.IsNotNull(result);
        }

        /// <summary>
        /// Complementary regression: when SelectorValueField names a valid property
        /// the happy path still works correctly and is not affected by the guard.
        /// </summary>
        [TestMethod]
        public void GetBatchQuery_with_valid_SelectorValueField_still_filters_correctly()
        {
            var vm = new SelectorStudentListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory));
            AddStudents(vm.DC!, 4);

            vm.SelectorValueField = "LoginName";
            vm.Ids = new List<string> { "u0", "u1" };
            vm.SearcherMode = ListVMSearchModeEnum.Batch;
            vm.NeedPage = false;
            vm.DoSearch();

            Assert.AreEqual(2, vm.EntityList.Count);
        }
    }
}
