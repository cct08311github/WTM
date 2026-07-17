#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core
{
    public partial class BasePagedListVM<TModel, TSearcher> : BaseVM, IBasePagedListVM<TModel, TSearcher>
        where TModel : TopBasePoco
        where TSearcher : BaseSearcher
    {
        public virtual void UpdateEntityList(bool updateAllFields = false)
        {
            if (EntityList != null)
            {
                var ftype = EntityList.GetType().GenericTypeArguments.First();
                var itemPros = ftype.GetAllProperties();

                foreach (var newitem in EntityList)
                {
                    var subtype = newitem.GetType();
                    if (typeof(IBasePoco).IsAssignableFrom( subtype))
                    {
                        IBasePoco ent = (IBasePoco)newitem;
                        if (ent.UpdateTime == null)
                        {
                            ent.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                        }
                        if (string.IsNullOrEmpty(ent.UpdateBy))
                        {
                            ent.UpdateBy = LoginUserInfo?.ITCode;
                        }
                    }
                    //循环页面传过来的子表数据,将关联到TopBasePoco的字段设为null,并且把外键字段的值设定为主表ID
                    foreach (var itempro in itemPros)
                    {
                        if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                        {
                            itempro.SetValue(newitem, null);
                        }
                    }
                }

                IEnumerable<TopBasePoco>? data = null;
                //打开新的数据库联接,获取数据库中的主表和子表数据
                using (var ndc = DC!.CreateNew())
                {
                    List<string?> ids = [.. EntityList.Select(x => x.GetID().ToString())];
                    data = [.. ndc.Set<TModel>().AsNoTracking().Where(ids.GetContainIdExpression<TModel>())];
                }
                //比较子表原数据和新数据的区别
                IEnumerable<TopBasePoco>? toadd = null;
                IEnumerable<TopBasePoco>? toremove = null;
                Utils.CheckDifference(data, EntityList, out toremove, out toadd);
                //设定子表应该更新的字段
                List<string> setnames = [];
                foreach (var field in FC.Keys)
                {
                    if (field.StartsWith("EntityList[0]."))
                    {
                        string name = field.Replace("EntityList[0].", "");
                        setnames.Add(name);
                    }
                }

                //前台传过来的数据
                foreach (var newitem in EntityList)
                {
                    //数据库中的数据
                    foreach (var item in data!)
                    {
                        //需要更新的数据
                        if (newitem.GetID().ToString() == item.GetID().ToString())
                        {
                            dynamic i = newitem;
                            var newitemType = item.GetType();
                            foreach (var itempro in itemPros)
                            {
                                if (!itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)) && (updateAllFields == true || setnames.Contains(itempro.Name)))
                                {
                                    var notmapped = itempro.GetCustomAttribute<NotMappedAttribute>();
                                    if (itempro.Name != "ID" && notmapped == null && itempro.PropertyType.IsList() == false)
                                    {
                                        DC.UpdateProperty(i, itempro.Name);
                                    }
                                }
                            }
                            if ( typeof(IBasePoco).IsAssignableFrom( item.GetType()))
                            {
                                DC.UpdateProperty(i, "UpdateTime");
                                DC.UpdateProperty(i, "UpdateBy");
                            }
                        }
                    }
                }
                //需要删除的数据
                foreach (var item in toremove!)
                {
                    //如果是PersistPoco，则把IsValid设为false，并不进行物理删除
                    if (typeof(IPersistPoco).IsAssignableFrom( ftype))
                    {
                        (item as IPersistPoco)!.IsValid = false;
                        if (typeof(IBasePoco).IsAssignableFrom(ftype))
                        {
                            (item as IBasePoco)!.UpdateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                            (item as IBasePoco)!.UpdateBy = LoginUserInfo?.ITCode;
                        }
                        dynamic i = item;
                        DC.UpdateEntity(i);
                    }
                    else
                    {
                        foreach (var itempro in itemPros)
                        {
                            if (itempro.PropertyType.IsSubclassOf(typeof(TopBasePoco)))
                            {
                                itempro.SetValue(item, null);
                            }
                        }
                        dynamic i = item;
                        DC.DeleteEntity(i);
                    }
                }
                //需要添加的数据
                foreach (var item in toadd!)
                {
                    if (typeof(IBasePoco).IsAssignableFrom( item.GetType()))
                    {
                        IBasePoco ent = (IBasePoco)item;
                        if (ent.CreateTime == null)
                        {
                            ent.CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime;
                        }
                        if (string.IsNullOrEmpty(ent.CreateBy))
                        {
                            ent.CreateBy = LoginUserInfo?.ITCode;
                        }
                    }
                    DC.AddEntity(item);


                }

                DC.SaveChanges();
            }
        }
    }
}
