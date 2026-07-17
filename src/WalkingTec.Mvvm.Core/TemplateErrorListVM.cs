using System.Collections.Generic;
using System.Linq;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// 错误数据列表
    /// </summary>
    public class TemplateErrorListVM : BasePagedListVM<ErrorMessage, BaseSearcher>
    {

        public TemplateErrorListVM()
        {
            EntityList = [];
            NeedPage = false;
        }

        protected override IEnumerable<IGridColumn<ErrorMessage>> InitGridHeader()
        {
            return new List<GridColumn<ErrorMessage>>{
                this.MakeGridHeader(x => x.Index, 60),
                this.MakeGridHeader(x => x.Message!)
            };
        }

        public override IOrderedQueryable<ErrorMessage> GetSearchQuery()
        {
            return EntityList!.AsQueryable().OrderBy(x => x.Index);
        }
    }
}
