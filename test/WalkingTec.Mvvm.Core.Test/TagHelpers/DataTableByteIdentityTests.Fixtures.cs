namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice O1 stage 1 — captured, normalized DataTableTagHelper.Process()
/// output for each DataTableByteIdentityTests matrix config, frozen at dotnet10 @
/// the pre-island commit (the flag-off byte-identity baseline). Regenerate ONLY by
/// re-capturing from the actual unmodified TagHelper output (see
/// DataTableByteIdentityTests.Normalize for the four documented, anchored
/// normalizations applied before comparison) — never hand-edit these strings.
///
/// DataTableTagHelper.cs is CRLF-encoded on disk, and its multi-line
/// interpolated script strings embed the source file's literal line
/// terminators into the emitted output — so these fixtures legitimately
/// contain embedded \r\n bytes (C# raw string literals preserve source line
/// terminators verbatim; verified empirically). Do not run a line-ending
/// normalizer over this file.
/// </summary>
internal static class DataTableByteIdentityFixtures
{
    public const string Default = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string UseLocalData = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    
    ,method:'post'
    
    ,page:false
    ,limit:0
    
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    ff.LoadLocalData("wtTable_Fixed470O1",wtTable_Fixed470O1option,[],true); 

  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string EnableHeaderFilter = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      wtmHeaderFilter.refresh('wtTable_Fixed470O1');
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    wtmHeaderFilter.init('wtTable_Fixed470O1');
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string EnableAnalysis = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
     ,toolbar: '#wtToolBar_wtTable_Fixed470O12'
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"><button type="button" class="layui-btn layui-btn-sm" onclick="wtmAnalysis.toggle('wtTable_Fixed470O1','WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM')">&#xe67e; 分析模式</button></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>


<div id="analysis-panel-wtTable_Fixed470O1" style="display:none;margin-top:10px;"></div><link rel="stylesheet" href="/_js/framework_analysis.css?v=<<VER>>" /><script src="/_js/lib/sortablejs/sortable.min.js?v=<<VER>>"></script><script src="/_js/framework_analysis.js?v=<<VER>>"></script>
""";

    public const string ActionsMatrix = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象
var ids; var objs;switch(layEvent){
case 'ActImport':{
var isPost = false;
var tempUrl = '',whereStr=null;
ff.BgRequest(tempUrl, isPost===true&&ids!==null&&ids!==undefined?{'Ids':ids}:undefined);};break;

case 'ActEdit':{
var isPost = false;
var tempUrl = '',whereStr=null;
if(data==undefined||data==null||data.ID==undefined||data.ID==null){
    ids = ff.GetSelections('wtTable_Fixed470O1');
    if(ids.length == 0){
        layui.layer.msg('Sys.SelectOneRow');
        return;
    }else if(ids.length > 1){
        layui.layer.msg('Sys.SelectOneRowMax');
        return;
    }else{
        tempUrl = tempUrl + '&id=' + ids[0];
        objs = ff.GetSelectionData('wtTable_Fixed470O1');
        if(objs!=null && objs.length > 0){
            tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0]);
        }
    }
}else{
    ids = [data.ID];
    objs = [data];
    tempUrl = tempUrl + '&id=' + data.ID;
    tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);
}

ff.OpenDialog(tempUrl,'<<DIALOG_GUID>>','Edit Item',700,500,isPost===true&&ids!==null&&ids!==undefined?{'Ids':ids}:undefined,false);};break;

case 'ActBatchDelete':{
var isPost = false;
var tempUrl = '',whereStr=null;
isPost = true;
var ids = ff.GetSelections('wtTable_Fixed470O1');
if(ids.length == 0){
    layui.layer.msg('Sys.SelectOneRowMin');
    return;
}


        layer.confirm('Are you sure you want to delete these rows?', {title:'Sys.Info'},function(index){
            ff.Download(tempUrl,ids);
        layer.close(index);
      });};break;

case 'ActDetails':{
var isPost = false;
var tempUrl = '',whereStr=null;
var ids = [];
var objs = [];
if(data != null && data.ID != null){
    ids.push(data.ID);
    tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);
} else {
    ids = ff.GetSelections('wtTable_Fixed470O1');
    objs = ff.GetSelectionData('wtTable_Fixed470O1');
    if(objs!=null && objs.length > 0){
        tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0]);
    }
}
if(ids.length > 1){
    layui.layer.msg('Sys.SelectOneRowMax');
    return;
}else if(ids.length == 1){
    tempUrl = tempUrl + '&id=' + ids[0];
}

ff.LoadPage(tempUrl,false,'Details',isPost===true&&ids!==null&&ids!==undefined?{'Ids':ids}:undefined);};break;

case 'ActExport':{
var isPost = false;
var tempUrl = '',whereStr=null;
var ids = ff.GetSelections('wtTable_Fixed470O1');
isPost = true;

ff.DownloadExcelOrPdf(tempUrl,'wtForm_Fixed470O1',wtTable_Fixed470O1defaultfilter.where,ids);};break;

case 'ActAddRow':{ff.AddGridRow("wtTable_Fixed470O1",wtTable_Fixed470O1option,{"LoginName":"","TempIsSelected":"0","ID":"00000000-0000-0000-0000-000000000000","LAY_CHECKED":false});
};break;

case 'ActRemoveRow':{};break;

case 'GroupSub1':{
var isPost = false;
var tempUrl = '',whereStr=null;
ff.BgRequest(tempUrl, isPost===true&&ids!==null&&ids!==undefined?{'Ids':ids}:undefined);};break;

case 'GroupSub2':{
var isPost = false;
var tempUrl = '',whereStr=null;
if(data==undefined||data==null||data.ID==undefined||data.ID==null){
    ids = ff.GetSelections('wtTable_Fixed470O1');
    if(ids.length == 0){
        layui.layer.msg('Sys.SelectOneRow');
        return;
    }else if(ids.length > 1){
        layui.layer.msg('Sys.SelectOneRowMax');
        return;
    }else{
        tempUrl = tempUrl + '&id=' + ids[0];
        objs = ff.GetSelectionData('wtTable_Fixed470O1');
        if(objs!=null && objs.length > 0){
            tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0]);
        }
    }
}else{
    ids = [data.ID];
    objs = [data];
    tempUrl = tempUrl + '&id=' + data.ID;
    tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);
}

ff.BgRequest(tempUrl, isPost===true&&ids!==null&&ids!==undefined?{'Ids':ids}:undefined);};break;

case 'ActOnClick':{
var isPost = false;
var tempUrl = '',whereStr=null;
if(data==undefined||data==null||data.ID==undefined||data.ID==null){
    ids = ff.GetSelections('wtTable_Fixed470O1');
    if(ids.length == 0){
        layui.layer.msg('Sys.SelectOneRow');
        return;
    }else if(ids.length > 1){
        layui.layer.msg('Sys.SelectOneRowMax');
        return;
    }else{
        tempUrl = tempUrl + '&id=' + ids[0];
        objs = ff.GetSelectionData('wtTable_Fixed470O1');
        if(objs!=null && objs.length > 0){
            tempUrl = ff.concatWhereStr(tempUrl,whereStr,objs[0]);
        }
    }
}else{
    ids = [data.ID];
    objs = [data];
    tempUrl = tempUrl + '&id=' + data.ID;
    tempUrl = ff.concatWhereStr(tempUrl,whereStr,data);
}

myGridOnClickHandler(ids,ff.GetSelectionData('wtTable_Fixed470O1'));};break;

case 'ActForcePost':{
var isPost = false;
var tempUrl = '',whereStr=null;
ff.BgRequest(tempUrl, ids!==null&&ids!==undefined?{'Ids':ids}:undefined);};break;
default:break;}
return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
     ,toolbar: '#wtToolBar_wtTable_Fixed470O12'
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.ActionsMatrixListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"toolbar":"#wtToolBar_wtTable_Fixed470O1","field":"","title":"Sys.Operation","width":160,"fixed":"right","align":"center"}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActImport'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-upload"></i>Import</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActEdit'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-edit"></i>Edit</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActBatchDelete'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-delete"></i>BatchDelete</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActDetails'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-search"></i>Details</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActExport'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-download-circle"></i>Export</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActAddRow'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-add-1"></i>AddRow</a><button type="button" class="layui-btn  layui-btn-sm layui-unselect layui-form-select downpanel" style="z-index:9999;" id="btn_fixed-group-btn-01">
                                 <div class="layui-select-title" style="padding-right:20px;">
                                        Batch Group
                                 <i class="layui-edge"></i>
                                 </div>
                                 <dl class="layui-anim layui-anim-upbit" style="top: initial;padding:1px 0px 0px 0px;" >
                                    <dd style="padding: 0 0px;margin-bottom:1px;line-height: initial;"><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'GroupSub1'});" class="layui-btn  layui-btn-sm" style="width: 100%;"><i class=""></i>Sub Action One</a></dd><dd style="padding: 0 0px;margin-bottom:1px;line-height: initial;"><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'GroupSub2'});" class="layui-btn  layui-btn-sm" style="width: 100%;"><i class=""></i>Sub Action Two</a></dd>
                                 </dl>
                                 </button><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActOnClick'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-face-smile"></i>CustomClick</a><a href="javascript:void(0)" onclick="wtToolBarFunc_wtTable_Fixed470O1({event:'ActForcePost'});" class="layui-btn  layui-btn-sm" style=""><i class="layui-icon layui-icon-refresh"></i>ForcePost</a></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"><a class="layui-btn layui-btn-primary layui-btn-xs" onclick="ff.RemoveGridRow('wtTable_Fixed470O1',wtTable_Fixed470O1option,{{d.LAY_INDEX}});">RemoveRow</a></script>


<script>
                        setTimeout(function(){
                            var form = layui.form, $ = layui.jquery;
                            $(".downpanel").on("click", ".layui-select-title", function(e) {
                                $(".layui-form-select").not($(this).parents(".layui-form-select")).removeClass("layui-form-selected");
                                $(this).parents(".layui-form-select").toggleClass("layui-form-selected");
                                            e.stopPropagation();
                                        });
                            $(document).click(function(event) {
                            var _con2 = $(".downpanel");
                            if (!_con2.is (event.target) && (_con2.has(event.target).length === 0)) {
                            _con2.removeClass("layui-form-selected");
                            }
                            });
                            },500);</script>
""";

    public const string AggregateColumns = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    ,totalRow:true
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.AggregateColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"Name","title":"Name","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}},{"field":"Price","title":"Price","align":"center","templet":function(d){var sty = '';var bg = '';var did = 'Price<<RANDOM>>_'+d.LAY_INDEX;if(d.Price__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Price__bgcolor+"');</s"+"cript>"; if(d.Price__forecolor != undefined) sty = 'color:'+d.Price__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Price)+bg+'</div>';},"totalRow":true}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      if(res.Aggregates){var tfoot=tab.find('.layui-table-total');tfoot.find('[data-field="Price"] .layui-table-cell').text(res.Aggregates["Price"]||'');}
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string RichColumns = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.RichColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){return '<div class="layui-progress" lay-filter=""><div class="layui-progress-bar" lay-percent="'+ff.EscapeAttr(d.LoginName)+'%"></div></div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){return '<span class="layui-badge layui-bg-blue">'+ff.EscapeText(d.Name)+'</span>';}},{"field":"Email","title":"\u90AE\u7BB1","align":"left","templet":function(d){return (d.Email?'<img src="'+ff.EscapeAttr(d.Email)+'" style="width:48px;height:48px;object-fit:cover;"/>' : '');}},{"field":"CellPhone","title":"\u624B\u673A","align":"left","templet":function(d){return (d.CellPhone!==null&&d.CellPhone!==undefined?Number(d.CellPhone).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2}):'')  /* fmt:0.00 */;}},{"field":"ZipCode","title":"\u90AE\u7F16","align":"left","templet":function(d){return (function(d){if(d.ZipCode===null||d.ZipCode===undefined)return '';var _cc=d.Address;var _num=Number(d.ZipCode);if(typeof _cc==='string'&&/^[A-Za-z]{3}$/.test(_cc)){try{return new Intl.NumberFormat(undefined,{style:'currency',currency:_cc}).format(_num);}catch(e){return ff.EscapeText(String(_num));}}else{return ff.EscapeText(String(_num));}});}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string BoolColumn = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.BoolColumnListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"IsValid","title":"_Admin.IsValid","align":"center","templet":function(d){var sty = '';var bg = '';var did = 'IsValid<<RANDOM>>_'+d.LAY_INDEX;if(d.IsValid__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.IsValid__bgcolor+"');</s"+"cript>"; if(d.IsValid__forecolor != undefined) sty = 'color:'+d.IsValid__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+d.IsValid+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string IsInSelector = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
        
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","Searcher.UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['Searcher.SortInfo.Property'] = obj.field;
    sortfilter['Searcher.SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1',''));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string AutoSearchFalse = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

        var wtTable_Fixed470O1optionempty =  Object.assign({}, wtTable_Fixed470O1option);
        wtTable_Fixed470O1optionempty.url = null;
        wtTable_Fixed470O1optionempty.data = [];
        layui.table.render(wtTable_Fixed470O1optionempty);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

    public const string SearcherExpanded = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>


<script>
layui.use(['element'], function() {
  setTimeout(function() {
    var filter = $('#wtForm_Fixed470O1 .layui-collapse').attr('lay-filter');
    if (filter) { layui.element.fold(filter, false); }
  }, 0);
});
</script>
""";

    public const string DetailGridPrix = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=Detail_470O1;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
    
    ,defaultToolbar: []
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left"},{"field":"Name","title":"\u59D3\u540D","align":"left"}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-120px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>

<input type="hidden" name=".DetailGridPrix" value="Detail_470O1"/>

""";

    public const string EnableClientExport = """
TAG:table|MODE:StartTagAndEndTag|ATTRS:id=wtTable_Fixed470O1;lay-filter=wtTable_Fixed470O1;subpro=;|PRE:|POST:
<script>
var wtTable_Fixed470O1option = null;
/* 监听工具条 */
function wtToolBarFunc_wtTable_Fixed470O1(obj){ //注：tool是工具条事件名，test是table原始容器的属性 lay-filter="对应的值"
var data = obj.data, layEvent = obj.event, tr = obj.tr; //获得当前行 tr 的DOM对象

return;
}
layui.use(['table'], function(){
  var table = layui.table;
  wtTable_Fixed470O1option = {
    elem: '#wtTable_Fixed470O1'
    ,id: 'wtTable_Fixed470O1'
    ,text:{
        none:'Sys.NoData'
    }
    ,request: { 'pageName': 'Page', 'limitName': 'Limit'}    
     ,toolbar: '#wtToolBar_wtTable_Fixed470O12'
    ,defaultToolbar: ['exports']
    
    ,headers: {layuisearch: 'true'}
    ,where: {"_DONOT_USE_VMNAME":"WalkingTec.Mvvm.Core.Test.TagHelpers.PlainColumnsListVM, WalkingTec.Mvvm.Core.Test","_DONOT_USE_CS":null,"SearcherMode":0,"SelectorValueField":null,"ViewDivId":"FixedViewDiv470O1","UniqueId":"<<SEARCHER_UNIQUEID>>"}
    ,method:'post'
    
    ,page:{
        rpptext:'Sys.RecordsPerPage',
        totaltext:'Sys.Total',
        recordtext:'Sys.Record',
        gototext:'Sys.Goto',
        pagetext:'Sys.Page',
        oktext:'Sys.GotoButtonText',
    }
    ,limit:20
    ,limits:[10,20,50,80,100,150,200,500,1000]
    
    
    ,cols:[[{"type":"checkbox","rowspan":1,"fixed":"left","unresize":true,"totalRowText":"Sys.Total"},{"type":"numbers","rowspan":1,"fixed":"left","unresize":true},{"field":"LoginName","title":"\u8D26\u53F7","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'LoginName<<RANDOM>>_'+d.LAY_INDEX;if(d.LoginName__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.LoginName__bgcolor+"');</s"+"cript>"; if(d.LoginName__forecolor != undefined) sty = 'color:'+d.LoginName__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.LoginName)+bg+'</div>';}},{"field":"Name","title":"\u59D3\u540D","align":"left","templet":function(d){var sty = '';var bg = '';var did = 'Name<<RANDOM>>_'+d.LAY_INDEX;if(d.Name__bgcolor != undefined) bg = "<script>$('#"+did+"').closest('td').css('background-color','"+d.Name__bgcolor+"');</s"+"cript>"; if(d.Name__forecolor != undefined) sty = 'color:'+d.Name__forecolor+';'; return '<div style="'+sty+'" id="'+did+'">'+ff.EscapeText(d.Name)+bg+'</div>';}}]]
    
    
    
    ,done: function(res,curr,count){
      wtTable_Fixed470O1filterback = this;
      if(res.Code == 401){ layui.layer.confirm(res.Msg,{title:'Sys.Error'}, function(index){window.location.reload();layer.close(index);});}
      if(res.Code != undefined && res.Code != 200){ layui.layer.alert(res.Msg,{title:'Sys.Error'});}
     var tab = $('#wtTable_Fixed470O1 + .layui-table-view');tab.find('table').css('border-collapse','separate');
      tab.css('overflow','hidden').addClass('donotuse_fill donotuse_pdiv');tab.children('.layui-table-box').addClass('donotuse_fill donotuse_pdiv').css('height','100px');tab.find('.layui-table-main').addClass('donotuse_fill');tab.find('.layui-table-header').css('min-height','38px');ff.triggerResize();
      
      
       tab.find('div [lay-event=\'LAYTABLE_COLS\']').attr('title','Sys.ColumnFilter');
       tab.find('div [lay-event=\'LAYTABLE_PRINT\']').attr('title','Sys.Print');
      
      
      if(typeof wtmColVis !== 'undefined'){ wtmColVis.init('wtTable_Fixed470O1'); }
      
    }
    }
wtTable_Fixed470O1defaultfilter = {};
wtTable_Fixed470O1filterback = {};
wtTable_Fixed470O1url = '';
$.extend(true,wtTable_Fixed470O1defaultfilter ,wtTable_Fixed470O1option);
    
    wtVar_wtTable_Fixed470O1 = table.render(wtTable_Fixed470O1option);
    
    if (document.body.clientWidth< 500) { wtTable_Fixed470O1option.page.layout = ['count', 'prev', 'page', 'next']; wtTable_Fixed470O1option.page.groups= 1;} 

setTimeout(function(){
    var tempwhere = {};
    $.extend(tempwhere,wtTable_Fixed470O1defaultfilter.where);
    table.reload('wtTable_Fixed470O1',{url:'',where: $.extend(tempwhere,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher')),});
},100);



  table.on('tool(wtTable_Fixed470O1)',wtToolBarFunc_wtTable_Fixed470O1);
  
    table.on('sort(wtTable_Fixed470O1)', function(obj){
    var sortfilter = {};
    sortfilter['SortInfo.Property'] = obj.field;
    sortfilter['SortInfo.Direction'] = obj.type.replace(obj.type[0],obj.type[0].toUpperCase());
    var w = $.extend(wtTable_Fixed470O1option.where,sortfilter,ff.GetSearchFormData('wtForm_Fixed470O1','Searcher'));

    table.reload('wtTable_Fixed470O1', {
    initSort: obj,
    where: w
    });
  });
  table.on('exportData(wtTable_Fixed470O1)', function(obj){
    obj.filename = 'wtTable_Fixed470O1';
  });
})
</script>
<script type="text / html" id="wtToolBar_wtTable_Fixed470O12" >
<div  id="wtTable_Fixed470O1buttons"style="text-align:right;margin-right:-80px"></div>
</script>
<!-- Grid 行内按钮 -->
<script type="text/html" id="wtToolBar_wtTable_Fixed470O1"></script>



""";

}
