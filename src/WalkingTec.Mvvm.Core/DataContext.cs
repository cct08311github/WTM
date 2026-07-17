#nullable enable
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Npgsql;
using MySql.EntityFrameworkCore.Extensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// FrameworkContext
    /// </summary>
    public partial class FrameworkContext : EmptyContext, IDataContext
    {
        public DbSet<FrameworkMenu> BaseFrameworkMenus { get; set; } = null!;
        public DbSet<FunctionPrivilege> BaseFunctionPrivileges { get; set; } = null!;
        public DbSet<DataPrivilege> BaseDataPrivileges { get; set; } = null!;
        public DbSet<FileAttachment> BaseFileAttachments { get; set; } = null!;
        public DbSet<FrameworkGroup> BaseFrameworkGroups { get; set; } = null!;
        public DbSet<FrameworkRole> BaseFrameworkRoles { get; set; } = null!;
        public DbSet<FrameworkUserRole> BaseFrameworkUserRoles { get; set; } = null!;
        public DbSet<FrameworkUserGroup> BaseFrameworkUserGroups { get; set; } = null!;
        public DbSet<ActionLog> BaseActionLogs { get; set; } = null!;
        public DbSet<ChangeLog> BaseChangeLogs { get; set; } = null!;
        public DbSet<FrameworkTenant> FrameworkTenants { get; set; } = null!;
        public DbSet<RefreshTokenEntity> FrameworkRefreshTokens { get; set; } = null!;
        public DbSet<AnalysisSavedQuery> AnalysisSavedQueries { get; set; } = null!;

        /// <summary>
        /// FrameworkContext
        /// </summary>
        public FrameworkContext() : base()
        {
        }

        /// <summary>
        /// FrameworkContext
        /// </summary>
        /// <param name="cs"></param>
        public FrameworkContext(string cs) : base(cs)
        {
        }

        public FrameworkContext(string cs, DBTypeEnum dbtype, string? version = null) : base(cs, dbtype, version)
        {
        }

        public FrameworkContext(CS cs) : base(cs)
        {
        }
        public FrameworkContext(DbContextOptions options) : base(options) { }

        /// <summary>
        /// 由 WTMContext 在建立 DataContext 後透過 property injection 注入。
        /// 設定後 SaveChanges 將在寫入含 [CacheLookup] 實體時自動失效快取；未設定時不影響正常 SaveChanges 行為。
        /// </summary>
        public WalkingTec.Mvvm.Core.Cache.ILookupCacheService? LookupCacheService { get; set; }

        public override int SaveChanges()
        {
            var dirtyLookups = CollectDirtyLookupTypes();
            var result = base.SaveChanges();
            InvalidateLookups(dirtyLookups);
            return result;
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            var dirtyLookups = CollectDirtyLookupTypes();
            var result = base.SaveChanges(acceptAllChangesOnSuccess);
            InvalidateLookups(dirtyLookups);
            return result;
        }

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var dirtyLookups = CollectDirtyLookupTypes();
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            InvalidateLookups(dirtyLookups);
            return result;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var dirtyLookups = CollectDirtyLookupTypes();
            var result = await base.SaveChangesAsync(cancellationToken);
            InvalidateLookups(dirtyLookups);
            return result;
        }

        private List<Type> CollectDirtyLookupTypes()
        {
            if (LookupCacheService == null) return new List<Type>();
            return [.. ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => e.Entity.GetType())
                .Where(t => LookupCacheService.IsCacheable(t))
                .Distinct()];
        }

        private void InvalidateLookups(List<Type> types)
        {
            foreach (var type in types)
                LookupCacheService?.InvalidateType(type);
        }

        /// <summary>
        /// OnModelCreating
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            //菜单和菜单权限的级联删除
            //wtmtodo: 删除菜单同步删除页面权限
            //modelBuilder.Entity<FunctionPrivilege>().HasOne(x => x.MenuItem).WithMany(x => x.Privileges).HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);

            // ── Scope all model-builder work to THIS context's own DbSet<T> entity types
            // (declared on this concrete context and its base types up to but not including
            // DbContext).  This is exactly the set EF discovers naturally.
            //
            // IMPORTANT (#450/#452 regression fix): we must NOT use Utils.GetAllModels()
            // (the global set) here.  That set is populated from ALL DbContext subclasses
            // visible in the loaded assemblies.  In an app with a secondary context, foreign
            // entity types would be force-registered into THIS context's model, causing
            // spurious tables in migrations and potentially EF's ValidateNonNullPrimaryKeys
            // crash for keyless/no-PK view entities.
            //
            // Pass 1 registration uses this same scoped set — #452 completes the fix started by #450.
            // #458: The file-attachment FK loop was moved to AFTER Pass 1 and now iterates
            // modelBuilder.Model.GetEntityTypes() (the fully-discovered EF model superset) so that
            // navigation-discovered entities (not declared as DbSet<T>) also get FK Restrict.
            var thisContextDbSetTypes = new HashSet<Type>(
                this.GetType()
                    .GetProperties()
                    .Where(p => p.PropertyType.IsGenericType &&
                                p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                    .Select(p => p.PropertyType.GenericTypeArguments[0]));

            // #458: FileAttachment FK-restrict loop moved to after Pass 1 (see below)
            // so it runs on the fully-discovered EF model (not just DbSet<T> types).

            // ── Pass 1: ensure all TopBasePoco types that belong to THIS context are
            // registered in EF metadata so that the hierarchy (including concrete
            // intermediates) is fully known before we apply query filters.
            foreach (var regType in thisContextDbSetTypes)
            {
                if (typeof(TopBasePoco).IsAssignableFrom(regType))
                {
                    typeof(ModelBuilder).GetMethod("Entity", Type.EmptyTypes)!
                        .MakeGenericMethod(regType).Invoke(modelBuilder, null);
                }
            }

            // ── #458: FileAttachment FK-restrict loop (moved from before Pass 1).
            // Iterates the EF-discovered model (superset of DbSets) so navigation-only
            // entities also get FK Restrict instead of EF's default Cascade.
            foreach (var discoveredType in modelBuilder.Model.GetEntityTypes()
                                               .Select(e => e.ClrType)
                                               .Distinct())
            {
                if (discoveredType.IsAbstract)
                {
                    continue; // EF errors on abstract types in Entity<T>()
                }
                if (!typeof(TopBasePoco).IsAssignableFrom(discoveredType))
                {
                    continue;
                }
                if (typeof(ISubFile).IsAssignableFrom(discoveredType))
                {
                    continue;
                }
                // Only look at properties declared on THIS exact type (not inherited),
                // so we don't configure the same FK twice via a base class.
                var fileProps = discoveredType.GetProperties(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly)
                    .Where(x => x.PropertyType == typeof(FileAttachment))
                    .ToArray();
                foreach (var fileProp in fileProps)
                {
                    var eb = typeof(ModelBuilder)
                        .GetMethod("Entity", Type.EmptyTypes)!
                        .MakeGenericMethod(discoveredType)
                        .Invoke(modelBuilder, null) as EntityTypeBuilder;
                    eb!.HasOne(fileProp.Name).WithMany().OnDelete(DeleteBehavior.Restrict);
                }
            }

            // ── Pass 2: apply global query filters only on the EF metadata root type
            // (the type whose EF BaseType == null in the model).  This covers concrete
            // intermediate types (Child : ConcreteParent : BasePoco) that the old
            // CLR BaseType check missed, while avoiding the EF crash from applying a
            // filter to a derived entity type.
            // NOTE: GetEntityTypes() is available during OnModelCreating on the mutable
            // model; BaseType here is the EF metadata parent (null ⟺ EF root).
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.BaseType != null)
                {
                    continue; // not an EF root — filter is inherited
                }

                var clrType = entityType.ClrType;
                if (!typeof(TopBasePoco).IsAssignableFrom(clrType))
                {
                    continue;
                }

                List<Expression> list = [];
                ParameterExpression pe = Expression.Parameter(clrType);

                if (typeof(IPersistPoco).IsAssignableFrom(clrType))
                {
                    var exp = Expression.Equal(Expression.Property(pe, "IsValid"), Expression.Constant(true));
                    list.Add(exp);
                }
                if (typeof(ITenant).IsAssignableFrom(clrType))
                {
                    var exp = Expression.Equal(Expression.Property(pe, "TenantCode"), Expression.PropertyOrField(Expression.Constant(this), "TenantCode"));
                    list.Add(exp);
                }
                if (list.Count > 0)
                {
                    var finalexp = list[0];
                    for (int i = 1; i < list.Count; i++)
                    {
                        finalexp = Expression.AndAlso(finalexp, list[i]);
                    }
                    var builder = typeof(ModelBuilder).GetMethod("Entity", Type.EmptyTypes)!
                        .MakeGenericMethod(clrType).Invoke(modelBuilder, null) as EntityTypeBuilder;
                    builder!.HasQueryFilter(Expression.Lambda(finalexp, pe));
                }
            }
        }


        /// <summary>
        /// 数据初始化
        /// </summary>
        /// <param name="allModules"></param>
        /// <param name="IsSpa"></param>
        /// <returns>返回true表示需要进行初始化数据操作，返回false即数据库已经存在或不需要初始化数据</returns>
        public async override Task<bool> DataInit(object? allModules, bool IsSpa)
        {
            bool rv = await Database.EnsureCreatedAsync();
            //判断是否存在初始数据
            bool emptydb = false;
            try
            {
                emptydb = Set<FrameworkUserRole>().Count() == 0 && Set<FrameworkMenu>().Count() == 0;
            }
            catch (Exception ex)
            {
                CoreProgram.GetLogger("DataContext")?.LogDebug(ex, "DataInit: FrameworkUserRole/FrameworkMenu count probe failed; assuming seeded DB");
            }

            if (emptydb == true)
            {
                var AllModules = allModules as List<SimpleModule>;
                var roles = new FrameworkRole[]
                {
                    new FrameworkRole{ ID = Guid.NewGuid(), RoleCode = "001", RoleName = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Admin"] : "Sys.Admin", TenantCode=TenantCode},
                    new FrameworkRole{ ID = Guid.NewGuid(), RoleCode = "002", RoleName = CoreProgram._localizer != null ? (string)CoreProgram._localizer["_Admin.User"] : "_Admin.User", TenantCode=TenantCode},
                };

                var adminRole = roles[0];
                if (Set<FrameworkMenu>().Any() == false && TenantCode == null)
                {
                    var systemManagement = GetFolderMenu("SystemManagement");
                    var logList = IsSpa ? GetMenu2(AllModules, "ActionLog", "MenuKey.ActionLog", 1) : GetMenu(AllModules, "_Admin", "ActionLog", "Index", "MenuKey.ActionLog", 1);
                    var userList = IsSpa ? GetMenu2(AllModules, "FrameworkUser", "MenuKey.UserManagement", 2) : GetMenu(AllModules, "_Admin", "FrameworkUser", "Index", "MenuKey.UserManagement", 2);
                    var roleList = IsSpa ? GetMenu2(AllModules, "FrameworkRole", "MenuKey.RoleManagement", 3) : GetMenu(AllModules, "_Admin", "FrameworkRole", "Index", "MenuKey.RoleManagement", 3);
                    var groupList = IsSpa ? GetMenu2(AllModules, "FrameworkGroup", "MenuKey.GroupManagement", 4) : GetMenu(AllModules, "_Admin", "FrameworkGroup", "Index", "MenuKey.GroupManagement", 4);
                    var menuList = IsSpa ? GetMenu2(AllModules, "FrameworkMenu", "MenuKey.MenuMangement", 5) : GetMenu(AllModules, "_Admin", "FrameworkMenu", "Index", "MenuKey.MenuMangement", 5);
                    var dpList = IsSpa ? GetMenu2(AllModules, "DataPrivilege", "MenuKey.DataPrivilege", 6) : GetMenu(AllModules, "_Admin", "DataPrivilege", "Index", "MenuKey.DataPrivilege", 6);
                    var tenantList = IsSpa ? GetMenu2(AllModules, "FrameworkTenant", "MenuKey.FrameworkTenant", 6) : GetMenu(AllModules, "_Admin", "FrameworkTenant", "Index", "MenuKey.FrameworkTenant", 7);
                    if (logList != null)
                    {
                        var menus = new FrameworkMenu?[] { logList, userList, roleList, groupList, menuList, dpList, tenantList };
                        foreach (var item in menus)
                        {
                            if (item != null)
                            {
                                systemManagement.Children?.Add(item);
                            }
                        }
                        Set<FrameworkMenu>().Add(systemManagement);
                        Set<FunctionPrivilege>().AddRange(systemManagement.FlatTree().Select(x => new FunctionPrivilege { RoleCode = "001", MenuItemId = x.ID, Allowed = true }));

                        if (IsSpa == false)
                        {
                            systemManagement.Icon = "layui-icon layui-icon-set";
                            logList?.SetPropertyValue("Icon", "layui-icon layui-icon-form");
                            userList?.SetPropertyValue("Icon", "layui-icon layui-icon-friends");
                            roleList?.SetPropertyValue("Icon", "layui-icon layui-icon-user");
                            groupList?.SetPropertyValue("Icon", "layui-icon layui-icon-group");
                            menuList?.SetPropertyValue("Icon", "layui-icon layui-icon-menu-fill");
                            dpList?.SetPropertyValue("Icon", "layui-icon layui-icon-auz");
                            tenantList?.SetPropertyValue("Icon", " layui-icon layui-icon-share");

                            var apifolder = GetFolderMenu("Api");
                            apifolder.ShowOnMenu = false;
                            apifolder.DisplayOrder = 100;
                            var logList2 = GetMenu2(AllModules, "ActionLog", "MenuKey.ActionLog", 1);
                            var userList2 = GetMenu2(AllModules, "FrameworkUser", "MenuKey.UserManagement", 2);
                            var roleList2 = GetMenu2(AllModules, "FrameworkRole", "MenuKey.RoleManagement", 3);
                            var groupList2 = GetMenu2(AllModules, "FrameworkGroup", "MenuKey.GroupManagement", 4);
                            var menuList2 = GetMenu2(AllModules, "FrameworkMenu", "MenuKey.MenuMangement", 5);
                            var dpList2 = GetMenu2(AllModules, "DataPrivilege", "MenuKey.DataPrivilege", 6);
                            var tenantList2 = GetMenu2(AllModules, "FrameworkTenant", "MenuKey.FrameworkTenant", 7);
                            var apis = new FrameworkMenu?[] { logList2, userList2, roleList2, groupList2, menuList2, dpList2, tenantList2 };
                            //apis.ToList().ForEach(x => { x.ShowOnMenu = false;x.PageName += $"({Program._localizer["BuildinApi"]})"; });
                            foreach (var item in apis)
                            {
                                if (item != null)
                                {
                                    item.ModuleName += "Api";
                                    item.ShowOnMenu = false;
                                    apifolder.Children?.Add(item);

                                }
                            }
                            Set<FrameworkMenu>().Add(apifolder);
                            Set<FunctionPrivilege>().AddRange(apifolder.FlatTree().Select(x => new FunctionPrivilege { RoleCode = "001", MenuItemId = x.ID, Allowed = true }));
                        }
                        else
                        {
                            systemManagement.Icon = " _wtmicon _wtmicon-icon_shezhi";
                            logList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-chaxun");
                            userList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-zhanghaoquanxianguanli");
                            roleList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-quanxianshenpi");
                            groupList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-zuzhiqunzu");
                            menuList?.SetPropertyValue("Icon", " _wtmicon _wtmicon--lumingpai");
                            dpList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-anquan");
                            tenantList?.SetPropertyValue("Icon", " _wtmicon _wtmicon-fenxiangfangshi");
                        }
                    }
                }
                Set<FrameworkRole>().AddRange(roles);
                await SaveChangesAsync();
            }
            return rv;
        }


        private FrameworkMenu GetFolderMenu(string FolderText, bool isShowOnMenu = true, bool isInherite = false)
        {
            FrameworkMenu menu = new FrameworkMenu
            {
                PageName = "MenuKey." + FolderText,
                Children = [],
                ShowOnMenu = isShowOnMenu,
                IsInside = true,
                FolderOnly = true,
                IsPublic = false,
                TenantAllowed = true,
                DisplayOrder = 1
            };
            return menu;
        }

        private FrameworkMenu? GetMenu(List<SimpleModule>? allModules, string? areaName, string controllerName, string actionName, string pageKey, int displayOrder)
        {
            if (allModules == null) return null;
            List<SimpleAction> acts = [.. allModules.Where(x => x.ClassName == controllerName && (areaName == null || x.Area?.Prefix?.ToLower() == areaName.ToLower())).SelectMany(x => x.Actions ?? [])];
            var act = acts.Where(x => x.MethodName == actionName).SingleOrDefault();
            List<SimpleAction> rest = [.. acts.Where(x => x.MethodName != actionName && x.IgnorePrivillege == false)];
            bool allowtenant = controllerName != "FrameworkMenu";
            FrameworkMenu? menu = GetMenuFromAction(act, true, displayOrder, allowtenant);
            if (act?.Module?.IsApi == true && menu != null)
            {
                menu.ModuleName += "Api";
            }
            if (menu != null)
            {
                menu.PageName = pageKey;
                for (int i = 0; i < rest.Count; i++)
                {
                    if (rest[i] != null)
                    {
                        var sub = GetMenuFromAction(rest[i], false, (i + 1), allowtenant);
                        if (sub != null)
                        {
                            sub.PageName = pageKey;
                            if (rest[i].Module?.IsApi == true)
                            {
                                sub.ModuleName += "Api";
                            }
                            menu.Children?.Add(sub);
                        }
                    }
                }
            }
            return menu;
        }

        private FrameworkMenu? GetMenu2(List<SimpleModule>? allModules, string controllerName, string pageKey, int displayOrder)
        {
            if (allModules == null) return null;
            bool allowtenant = controllerName != "FrameworkMenu";
            List<SimpleAction> acts = [.. allModules.Where(x => (x.FullName == $"WalkingTec.Mvvm.Admin.Api,{controllerName}") && x.IsApi == true).SelectMany(x => x.Actions ?? [])];
            List<SimpleAction> rest = [.. acts.Where(x => x.IgnorePrivillege == false)];
            SimpleAction? act = null;
            if (acts.Count > 0)
            {
                act = acts[0];
            }
            FrameworkMenu? menu = GetMenuFromAction(act, true, displayOrder, allowtenant);
            if (menu != null)
            {
                menu.PageName = pageKey;
                menu.Url = "/" + acts[0].Module?.ClassName?.ToLower();
                menu.ActionName = "MainPage";
                menu.ClassName = acts[0].Module?.FullName;
                menu.MethodName = null;
                for (int i = 0; i < rest.Count; i++)
                {
                    if (rest[i] != null)
                    {
                        var sub = GetMenuFromAction(rest[i], false, (i + 1), allowtenant);
                        if (sub != null)
                        {
                            sub.PageName = pageKey;
                            menu.Children?.Add(sub);
                        }
                    }
                }
            }
            return menu;
        }

        private FrameworkMenu? GetMenuFromAction(SimpleAction? act, bool isMainLink, int displayOrder = 1, bool allowtenant = true)
        {
            if (act == null || act.Module == null)
            {
                return null;
            }
            FrameworkMenu menu = new FrameworkMenu
            {
                //ActionId = act.ID,
                //ModuleId = act.ModuleId,
                ClassName = act.Module.FullName,
                MethodName = act.MethodName,
                Url = act.Url,
                ShowOnMenu = isMainLink,
                FolderOnly = false,
                Children = [],
                IsPublic = false,
                IsInside = true,
                DisplayOrder = displayOrder,
                TenantAllowed = allowtenant
            };
            if (isMainLink)
            {
                menu.ModuleName = act.Module.ModuleName;
                menu.ActionName = act.ActionDes?.Description ?? act.ActionName;
                menu.MethodName = null;
            }
            else
            {
                menu.ModuleName = act.Module.ModuleName;
                menu.ActionName = act.ActionDes?.Description ?? act.ActionName;
            }
            return menu;
        }

    }


}
