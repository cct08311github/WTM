namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// What <c>DPWhere</c> does for a field whose owner type is not a mapped entity of the data model and has no mapped base type
    /// (for example a view type that is not derived from an entity), so the data privilege setting that applies to the field cannot
    /// be determined.
    /// </summary>
    public enum DataPrivilegeUnresolvedTypeMode
    {
        /// <summary>The field is skipped silently and the query is returned without the condition (the behavior before this setting existed).</summary>
        Legacy = 0,

        /// <summary>
        /// Default. The field is skipped and the query is returned without the condition, but a warning naming the type and the field is
        /// logged. A user who can access all data of every configured privilege table, and an application without any configured
        /// privilege, are not affected and nothing is logged.
        /// </summary>
        Audit = 1,

        /// <summary>
        /// The condition is always false, so the query returns no rows, and a warning is logged. A user who can access all data of every
        /// configured privilege table, and an application without any configured privilege, are not affected.
        /// </summary>
        Enforce = 2
    }

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

        /// <summary>
        /// Whether <c>DPWhere</c> resolves a field of a projected view type (a type derived from an entity, not mapped itself, as produced
        /// by <c>Select(x => new X_View { ... })</c>) through its closest mapped base type. Default <c>true</c>. Set to <c>false</c> to
        /// restore the earlier behavior, in which such a field matched no privilege setting and the query was returned without the condition
        /// (see <see cref="UnresolvedTypeMode"/>, which then also applies to these types).
        /// </summary>
        public bool ResolveProjectedTypes { get; set; } = true;

        /// <summary>
        /// See <see cref="DataPrivilegeUnresolvedTypeMode"/>. Default <see cref="DataPrivilegeUnresolvedTypeMode.Audit"/>.
        /// </summary>
        public DataPrivilegeUnresolvedTypeMode UnresolvedTypeMode
        {
            get { return _unresolvedTypeMode; }
            set
            {
                // a number that is not one of the modes (the configuration binder accepts any number for an enum) is rejected
                if (!System.Enum.IsDefined(typeof(DataPrivilegeUnresolvedTypeMode), value))
                {
                    throw new System.ArgumentOutOfRangeException(nameof(value), value, "UnresolvedTypeMode must be Legacy, Audit or Enforce.");
                }
                _unresolvedTypeMode = value;
            }
        }

        private DataPrivilegeUnresolvedTypeMode _unresolvedTypeMode = DataPrivilegeUnresolvedTypeMode.Audit;
    }
}
