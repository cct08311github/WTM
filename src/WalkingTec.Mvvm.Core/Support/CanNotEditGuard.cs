using System;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Members marked <see cref="CanNotEditAttribute"/> keep the stored value when a record is saved by the generic edit, also when the whole record
    /// is written (<c>updateAllFields: true</c>): the attribute is the declaration that the generic edit never changes them.
    /// </summary>
    internal static class CanNotEditGuard
    {
        /// <summary>
        /// For a record that was set to the modified state as a whole: marks every <see cref="CanNotEditAttribute"/> column as not modified, so that the
        /// save writes the other columns and leaves these as stored. A column that cannot be marked (the data context is not an EF context) is not written
        /// by this code path either.
        /// </summary>
        public static void KeepStoredValues(IDataContext dc, object entity)
        {
            if (entity == null || dc is not DbContext db)
            {
                return;
            }
            var entry = db.Entry(entity);
            foreach (var property in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.GetCustomAttribute<CanNotEditAttribute>() != null))
            {
                var column = entry.Metadata.FindProperty(property.Name);
                if (column != null && column.IsPrimaryKey() == false)
                {
                    entry.Property(property.Name).IsModified = false;
                }
            }
        }
    }
}
