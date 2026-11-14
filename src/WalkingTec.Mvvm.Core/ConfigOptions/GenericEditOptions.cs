namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Settings of the generic edit paths (<c>BaseCRUDVM</c> edit, the list edit of <c>BasePagedListVM.UpdateEntityList</c>, <c>BaseBatchVM</c>
    /// batch edit and the overwrite of existing records by <c>BaseImportVM</c>). Configuration section <c>GenericEdit</c>.
    /// </summary>
    public class GenericEditOptions
    {
        /// <summary>
        /// By default the generic edit paths do not write the members that the framework owns, even when a request carries them:
        /// <c>TenantCode</c> of an <see cref="ITenant"/>, <c>IsValid</c> of an <see cref="IPersistPoco"/>, and <c>CreateTime</c> and <c>CreateBy</c>
        /// of an <see cref="IBasePoco"/> (an import that overwrites existing records refuses only <c>TenantCode</c> and <c>IsValid</c>).
        /// Such a member is skipped silently, like a member marked <see cref="CanNotEditAttribute"/>. Set to <c>true</c> to restore the earlier
        /// behavior, in which those members are written when they are posted. The update stamps and the tenant stamp of rows added by the
        /// list edit do not depend on this setting. Default <c>false</c>.
        /// </summary>
        public bool LegacyProtectedMemberWrites { get; set; } = false;
    }
}
