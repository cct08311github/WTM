using Microsoft.AspNetCore.DataProtection;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// What the upload endpoints do for an import: the user who uploaded the file gets a reference (<see cref="FilePurpose.Import"/>) that goes
    /// into <c>UploadFileRef</c> next to <c>UploadFileId</c>.
    /// </summary>
    internal static class ImportUploadRef
    {
        public static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

        /// <summary>Sets <c>UploadFileId</c> and the matching <c>UploadFileRef</c> on an import view model.</summary>
        public static void Apply(WTMContext wtm, object vm, string fileId)
        {
            vm.GetType().GetProperty("UploadFileId").SetValue(vm, fileId);
            vm.GetType().GetProperty("UploadFileRef").SetValue(vm, wtm.CreateFileReference(fileId, FilePurpose.Import));
        }
    }
}
