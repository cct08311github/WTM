namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Settings of the data privilege conditions in list queries. Configuration section <c>DataPrivilegeOptions</c>.
    /// </summary>
    public class DataPrivilegeOptions
    {
        /// <summary>
        /// Whether the data privilege conditions (<c>DPWhere</c>, and conditions added with <c>WhereProtected</c>) are kept when a list
        /// query is narrowed to the selected records (<c>SearcherMode</c> <c>Batch</c> and <c>CheckExport</c>, and the query used when
        /// <c>ReplaceWhere</c> is set). The search conditions are still replaced, so a selected record is found whatever the search
        /// form holds. Default <c>true</c>. Set to <c>false</c> to restore the earlier behavior, in which the whole run of
        /// <c>Where</c> conditions is replaced. A single list view model can choose differently by overriding
        /// <c>BasePagedListVM.KeepDataPrivilegeInBatchQuery</c>.
        /// </summary>
        public bool KeepInBatchQuery { get; set; } = true;
    }
}
