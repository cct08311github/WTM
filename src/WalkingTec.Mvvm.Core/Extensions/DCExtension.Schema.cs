#nullable enable
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace WalkingTec.Mvvm.Core.Extensions
{
    /// <summary>
    /// DC相关扩展函数
    /// </summary>
    public static partial class DCExtension
    {
        public static string GetTableName<T>(this IDataContext self)
        {
            return self.Model.FindEntityType(typeof(T))?.GetTableName() ?? string.Empty;
        }

        /// <summary>
        /// 通过模型和模型的某个List属性的名称来判断List的字表中关联到主表的主键名称
        /// </summary>
        /// <typeparam name="T">主表Model</typeparam>
        /// <param name="self">DataContext</param>
        /// <param name="listFieldName">主表中的子表List属性名称</param>
        /// <returns>主键名称</returns>
        public static string GetFKName<T>(this IDataContext self, string listFieldName) where T : class
        {
            return GetFKName(self, typeof(T), listFieldName);
        }

        /// <summary>
        /// 通过模型和模型的某个List属性的名称来判断List的字表中关联到主表的主键名称
        /// </summary>
        /// <param name="self">DataContext</param>
        /// <param name="sourceType">主表model类型</param>
        /// <param name="listFieldName">主表中的子表List属性名称</param>
        /// <returns>主键名称</returns>
        public static string GetFKName(this IDataContext self, Type sourceType, string listFieldName)
        {
            try
            {
                var test = self.Model.FindEntityType(sourceType)?.GetReferencingForeignKeys().Where(x => x.PrincipalToDependent?.Name == listFieldName).FirstOrDefault();
                if (test != null && test.Properties.Count > 0)
                {
                    return test.Properties[0].Name;
                }
                else
                {
                    return "";
                }
            }
            catch
            {
                return "";
            }
        }


        /// <summary>
        /// 通过子表模型和模型关联到主表的属性名称来判断该属性对应的主键名称
        /// </summary>
        /// <typeparam name="T">子表Model</typeparam>
        /// <param name="self">DataContext</param>
        /// <param name="FieldName">关联主表的属性名称</param>
        /// <returns>主键名称</returns>
        public static string GetFKName2<T>(this IDataContext self, string FieldName) where T : class
        {
            return GetFKName2(self, typeof(T), FieldName);
        }

        /// <summary>
        /// 通过模型和模型关联到主表的属性名称来判断该属性对应的主键名称
        /// </summary>
        /// <param name="self">DataContext</param>
        /// <param name="sourceType">子表model类型</param>
        /// <param name="FieldName">关联主表的属性名称</param>
        /// <returns>主键名称</returns>
        public static string GetFKName2(this IDataContext self, Type sourceType, string FieldName)
        {
            try
            {
                var pro = sourceType.GetSingleProperty(FieldName!);
                if (pro?.GetCustomAttribute<NotMappedAttribute>() != null)
                {
                    var idpro = sourceType.GetSingleProperty(FieldName + "Id");
                    if (idpro != null)
                    {
                        return idpro.Name;
                    }
                    else
                    {
                        return "";
                    }
                }
                else
                {
                    var test = self.Model.FindEntityType(sourceType)?.GetForeignKeys().Where(x => x.DependentToPrincipal?.Name == FieldName).FirstOrDefault();
                    if (test != null && test.Properties.Count > 0)
                    {
                        return test.Properties[0].Name;
                    }
                    else
                    {
                        return "";
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Issue #824 shared predicate: true if <paramref name="propertyName"/> on
        /// <paramref name="entityType"/> is the FK scalar for a relationship whose PRINCIPAL
        /// entity type is <see cref="FileAttachment"/>, decided purely from EF Core's own
        /// relationship metadata (<see cref="IDataContext.Model"/>) — never a hardcoded
        /// field-name list, so it automatically covers downstream-defined attachment FKs (e.g. a
        /// consumer app's own <c>School.PhotoId</c>) without any change here.
        ///
        /// <para>
        /// This is the ONE decision every #824 write-path gate is meant to share:
        /// <c>_FrameworkController.UpdateModelProperty</c> (Issue #824 Part 1 / B1.1) consumes it
        /// directly. The rest of #824's known write-path sinks —
        /// <c>BasePagedListVM.UpdateEntityList</c>, <c>BaseBatchVM.DoBatchEdit</c>/<c>Async</c>,
        /// <c>BaseImportVM.BatchSaveData</c>'s Excel column mapping, grandchild
        /// <c>IEnumerable&lt;ISubFile&gt;</c> collections, and direct <c>DbSet</c> writers — are
        /// follow-up work on the same issue and are expected to consume this same predicate
        /// rather than re-deriving their own version of it: two independently-written
        /// implementations would inevitably drift, and the drift direction is precisely "one of
        /// them misses a downstream-defined attachment FK" (see Issue #824's architecture
        /// conclusion on why one-off, per-sink field-name checks are structurally doomed).
        /// </para>
        ///
        /// <para>
        /// This predicate answers ONLY "is this property an attachment FK" — it does not resolve
        /// whether a posted id actually exists, or under which tenant. Callers that need that
        /// (e.g. <see cref="BaseCRUDVM{TModel}"/>'s own
        /// <c>RejectUnresolvableFileAttachmentReferences</c>, added by #815) still do that
        /// resolution themselves; this helper is deliberately narrower so it can be shared by
        /// call sites — like <c>UpdateModelProperty</c> — that intentionally never touch
        /// <see cref="FileAttachment"/> resolution at all and instead deny unconditionally.
        /// </para>
        ///
        /// <para>
        /// Failure mode: like <see cref="GetFKName2(IDataContext, Type, string)"/> immediately
        /// above (the existing convention in this file for schema-metadata lookups), an
        /// unexpected exception from the EF metadata APIs is swallowed and treated as "not an
        /// attachment FK" (fails open on this specific predicate), not "block everything on this
        /// entity type". In practice none of <see cref="IDataContext.Model"/>'s metadata read
        /// APIs used here are documented to throw for a valid CLR type — a live exception would
        /// indicate a broken EF model (a startup/build-time defect), which is outside the threat
        /// model of a runtime request-level guard.
        /// </para>
        /// </summary>
        /// <param name="self">The DataContext whose EF model provides relationship metadata.</param>
        /// <param name="entityType">The CLR entity type that declares <paramref name="propertyName"/>.</param>
        /// <param name="propertyName">The FK scalar property name to test (e.g. <c>"PhotoId"</c>).</param>
        public static bool IsFileAttachmentForeignKeyProperty(this IDataContext? self, Type? entityType, string? propertyName)
        {
            if (self?.Model == null || entityType == null || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }
            try
            {
                var efEntityType = self.Model.FindEntityType(entityType);
                if (efEntityType == null)
                {
                    return false;
                }
                foreach (var fk in efEntityType.GetForeignKeys())
                {
                    if (fk.PrincipalEntityType?.ClrType != typeof(FileAttachment))
                    {
                        continue;
                    }
                    foreach (var fkProperty in fk.Properties)
                    {
                        if (string.Equals(fkProperty.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generic-<typeparamref name="T"/> convenience overload of
        /// <see cref="IsFileAttachmentForeignKeyProperty(IDataContext?, Type?, string?)"/>.
        /// </summary>
        public static bool IsFileAttachmentForeignKeyProperty<T>(this IDataContext self, string propertyName) where T : class
        {
            return IsFileAttachmentForeignKeyProperty(self, typeof(T), propertyName);
        }

        public static string GetFieldName<T>(this IDataContext self, Expression<Func<T, object>> field)
        {
            string pname = field.GetPropertyName();
            return self.GetFieldName<T>(pname);
        }


        public static string GetFieldName<T>(this IDataContext self, string fieldname)
        {
            var rv = self.Model.FindEntityType(typeof(T))?.FindProperty(fieldname);
            return rv?.GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(self.GetTableName<T>())) ?? string.Empty;
        }

        public static string GetPropertyNameByFk(this IDataContext self, Type sourceType, string fkname)
        {
            try
            {
                var test = self.Model.FindEntityType(sourceType)?.GetForeignKeys().Where(x => x.DependentToPrincipal?.ForeignKey?.Properties[0]?.Name == fkname).FirstOrDefault();
                if (test != null && test.Properties.Count > 0)
                {
                    return test.DependentToPrincipal?.Name ?? "";
                }
                else
                {
                    return "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
