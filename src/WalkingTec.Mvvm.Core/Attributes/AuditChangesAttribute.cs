#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 標注於 Model 類別，使 BaseCRUDVM 在 DoAdd / DoEdit / DoDelete 時
    /// 自動將變更前後的欄位值序列化並寫入 <see cref="ChangeLog"/> 資料表（#569）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class AuditChangesAttribute : Attribute
    {
    }
}
