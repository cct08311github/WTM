# Fork 更新日志

本文件只记录本 fork 相对上游（dotnetcore/WTM）的变更，按版本从新到旧排列。上游的变更请见 [CHANGELOG.md](CHANGELOG.md)。

## 版本号规则

fork 的版本号为四段式：`<上游版本>.<fork 修订号>`，例如 `10.0.0.2` 表示基于上游 `10.0.0` 的第 2 个 fork 版本。

* 上游版本取自 `version.props`（与上游保持一致，不在 fork 中修改），fork 修订号 `ForkRevision` 定义在 `fork.props`
* 四段式版本不会与上游在 nuget.org 上发布的三段式版本冲突，且大于对应的上游版本（`10.0.0.2` > `10.0.0`）
* 每次发布 fork 版本时 `ForkRevision` 加 1；合并上游的版本号升级后重置为 1

## 未发布

* **修复（安全）：**   `/v1/activities`（流程设计器的活动清单）改为在程序内取得 Elsa 的 activity 描述（复制 Elsa.Server.Api 2.17.0 `Activities.List` 的逻辑，并使用 Elsa 的 endpoint 序列化设置，输出与 Elsa 自己的端点逐字节相同），`WtmMiddleware` 不再对 `/v1/activities?inneruse=1` 发出 HTTP 内部调用（#216）。10.0.0.6 中该内部调用的目标在默认设置（`AllowedHosts` 为 `*` 且未设置 `InternalCallBaseAddress`）下由 Host header 决定，内部密钥也会被送到那里，已登录用户可借此取得密钥并匿名读取全部角色与群组名称。现在没有内部调用、密钥 header（`X-Wtm-Internal-Call`）和专用的不跟随重定向的 `WtmWorkflowInternal` HttpClient；匿名的 `?inneruse=1` 请求与公开形式一样返回 401。“正式环境必须设置 `InternalCallBaseAddress`”以及多实例／滚动重启时的短暂 401 都不再适用。`WorkflowOptions:InternalCallBaseAddress`、`WorkflowApiSecurity.ResolveSelfCall`、`IsAllowedHost` 等 public API 为兼容下游而保留，标注为 `[Obsolete]`（仅编译警告，不再有作用）。仅在 `EnableWorkflow=true` 时相关。程序内应答与原来一样遵守 `WorkflowOptions:RequireAuthentication`／`RequireAdminRole`（改为程序内调用后不再经过 Elsa controller 的授权过滤器，因此在中介层以同一个 policy 检查：未登录 401、权限不足 403）；已登录的 `?inneruse=1` 与其他形式走同一条路径；只应答 GET 与 HEAD（未登录时任何方法仍先返回 401）；响应带 `application/json; charset=utf-8`、`X-Content-Type-Options: nosniff`、`Cache-Control: no-store`；生成过程出错时返回空 body 的 500（只记录异常类型与消息）；JWT 调用者验证通过后设置 `HttpContext.User`；路径比对接受开头的多余斜线（`//v1/activities`）
* **修改：**   移除未使用的 `src/WalkingTec.Mvvm.Mvc/echarts.common.min.js`（echarts 4.0.4）。它没有列入 csproj、没有被引用，也不在 NuGet 包内，不影响下游（#189）
* **修改（demo）：**   LayUI demo 的 `layuiadmin/lib/extend/echarts.js` 由 4.x 升级到官方 echarts 6.1.0，修复 GHSA-fgmj-fm8m-jvvx 与内置 zrender 的 GHSA-fhv8-fx5f-7fxf（#189）。`framework_layui.js` 的图表代码需要 echarts ≥ 5.0（散点图与刷新用到 `replaceMerge`、dataset `filter` transform，4.x 下会失效），自行复制 layuiadmin 的下游网站请自行更新网站上的 echarts。外观变化：默认色盘更换、图例默认移到底部、标题居中、tooltip 改为白底圆角，内置 `light` 主题已移除（`Theme=light` 会使用默认主题）；想保持 5.x 外观可使用 `echarts/theme/v5.js`。仅限 demo，不影响 NuGet 包。详见 FORK.md 第 16 节
* **修复（安全）：**   LayUI `wt:chart` 的图表数据在输出时补上适当的编码（#221）：`ListExtension.ToChartData` 改以 `System.Text.Json` 转义放进 JSON 的字符串，数字固定使用 invariant culture；Scatter 图的 tooltip 对 `seriesName` 与数据值做 HTML 转义（formatter 内自带转义函数，不依赖 echarts 版本）。一般数据的输出与修改前逐字相同，`framework_layui.js` 未修改。影响：使用 LayUI `wt:chart` 的网站；行为：一般数据的图表与 tooltip 不变，只有含特殊字符的数据被正确编码。详见 FORK.md 第 17 节

## 10.0.0.6(2026-10-21)

* **修改：**   升级依赖（均在同一主版本内）：`Microsoft.AspNetCore.Authentication.JwtBearer`、`Microsoft.AspNetCore.DataProtection`、`Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation`、`Microsoft.AspNetCore.SpaServices.Extensions` 10.0.9 → 10.0.13（#186、#187、#188、#190）；`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.2 → 10.0.3（#110）；`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 → 2.1.14（#184）
* **修改：**   `Quartz` 3.15.1 → 3.22.7（#131）。public API 与数据库 schema 不变，但有行为变更：trigger 连续 fire 失败 5 次会被停放为 `ERROR`（`MaxConsecutiveFireFailures`，设为 0 恢复原行为）；`IConfiguration` 中的数据源、listener 等名称保留大小写；使用 persistent store 时请为 job／trigger 设置固定的 `WithIdentity`。WTM 本身使用 RAMJobStore，不需要修改；自行使用 Quartz persistent store 或依赖 trigger 无限重试的应用请确认
* **修复（安全）：**   `pagemode`、`tabmode`、`divid` 与导出用的 `DONOTUSEDOWNLOADING` 等 UI 状态 cookie 在 HTTPS 请求下设置 `Secure`（#168）
* **修复（安全）：**   开发期代码生成器（`CodeGenVM`／`_CodeGenController`）的输出目录改由服务器端计算，`ModelName`／`Area` 与所选模型均先验证，所有读写都限制在允许的根目录内；验证失败时返回 400 且不写入任何文件（#167）
* **修复（安全）：**   `_FrameworkController.RemoteEntry` 与 `SetLanguageForBlazor` 把 `redirect` 参数放进返回的 `<script>` 前，先以 `JsStringEncoder.Escape` 转义；实际跳转目标不变（#175、#170）
* **修复：**   LayUI `framework_layui.js` 的 `gridcellchange`（下拉单元格）选中项正则表达式写错，且未转义 `ele.value`，导致特殊字符时选中错误（#169）
* **修改（demo）：**   Vue3 demo 前端：echarts 5.6 → 6.1 并移除未使用的 `echarts-wordcloud`、`echarts-gl`（#191）；vue-i18n 9.14 → 11.4（#182）；`@intlify/core-base`、`brace-expansion`、`immutable`、`js-yaml` 安全修补（#166）；`toolsValidate.ts` 的正则表达式修正字符范围并消除 ReDoS（#171）；其他 minor/patch 更新。仅限 demo，不影响 NuGet 包

* **修复：**   `BaseCRUDVM.GetWorkflowTimeLineAsync`／`GetWorkflowInstanceAsync` 不再以 HTTP 自我调用 `_WorkflowApi/GetTimeLine`、`GetWorkflow`（#207）。该调用不会转送登录 cookie，只带 `RemoteToken`；单主机时 `RemoteToken` 是登录时签发的本机 JWT（默认 3600 秒），却随用户缓存保存 7 天，过期后自我调用被 401 拒绝，时间轴与流程信息返回 null（`wt:flowinfo` 会抛异常）。现在改为在程序内直接读取（新增 Core 的 `WorkflowQuery`，controller 与 `BaseCRUDVM` 共用），controller 的路由、授权、返回格式不变；`HasMainHost` 时行为不变（这两个端点本来就读本站数据库，不转送主站）。影响：启用 workflow 的网站；没有新增或升级套件。详见 FORK.md 第 14 节
* **修改（BREAKING，安全）：**   `/v1/activities`（流程设计器的活动清单）现在一律要求已登录用户，与 `WorkflowOptions:RequireAuthentication` 无关：`WtmMiddleware` 对未登录的调用方直接返回 401，不发出内部调用（#208）。原来 `RequireAuthentication` 关闭（默认）时未登录即可取得清单，其中审批活动的下拉选项包含全部角色与群组的 ID 与名称。为避免外部调用方直接请求 `/v1/activities?inneruse=1` 绕过检查，内部调用会带一个程序启动时随机生成、只存在内存的密钥 header（`X-Wtm-Internal-Call`，用 `CryptographicOperations.FixedTimeEquals` 比较）；`inneruse=1` 且密钥缺少或不符、又未登录的请求返回 401；`/v1/activities/` 与其子路径，以及 Elsa 路由 `v{version:apiVersion}/activities` 接受的其他版本写法（例如 `/v1.0/activities`、`/v01/activities`，不分大小写）也做同样检查；内部调用改用不跟随 redirect 的专用 HttpClient。注意：默认设置（`AllowedHosts=*` 且未设置 `WorkflowOptions:InternalCallBaseAddress`）下内部调用的目标由 Host header 决定，密钥也会送到那里，已登录用户可借此取得密钥并在进程重启前匿名读取角色与群组名称，**正式环境必须设置 `WorkflowOptions:InternalCallBaseAddress`**（根本解法见 #216）。影响：只有 `EnableWorkflow=true` 的网站，且只有未登录的调用方；已登录后台打开的流程设计器不受影响。升级：自行调用的整合改带登录 cookie 或 JWT；`RequireAuthentication` 关闭且内部调用经负载均衡转到其他实例时，请把 `WorkflowOptions:InternalCallBaseAddress` 设为本机地址或开启 `RequireAuthentication`。回滚：退回上一版本，没有恢复匿名访问的开关。其余 Elsa `/v1/*` 管理端点不变，仍由 `RequireAuthentication` 控制。详见 FORK.md 第 15 节
* **修改（BREAKING，安全）：**   `_WorkflowApi` 的 `GetWorkflowUsers`、`GetWorkflowGroups`、`GetWorkflowGroupManagers`、`GetWorkflowMyGroupManagers`、`GetWorkflowRoles` 原本标有 `[Public]`，未登录即可调用，且参数为空时会返回全部用户（帐号与姓名）、群组、群组主管与角色，所有使用 WalkingTec.Mvvm.Mvc 的网站（不论是否启用 workflow）都受影响（#13，P0）。现在：`EnableWorkflow=false` 时 `_WorkflowApiController` 不加入应用程序，`/_WorkflowApi/*` 一律返回 404（由匿名 stub `WorkflowApiDisabledController` 处理，带 SPA 通配路由的网站也一样，不会返回前端页面，#210）；`EnableWorkflow=true` 时这 5 个端点与其他端点一样需要登录，未登录返回 401。审批活动（`WtmApproveActivity`）的角色／群组下拉选项及审批执行时的查询不再以无凭证的 HTTP 自我调用这些端点，改为直接查询数据库（多主机 `HasMainHost` 时改为带当前用户 remote token 直接调用主站）。影响：未登录或不带 token 调用这些端点的外部调用方（BMS／BOC_WebAPI 已确认没有依赖）。升级：外部集成改为携带登录 cookie 或 JWT；回滚：退回上一版本（`Mvc` 与 `Core` 一起），没有恢复匿名访问的开关。详见 FORK.md 第 12 节
* **修复（安全）：**   工作流内部调用 `/v1/activities` 的目标不再由未经验证的 Host header 决定（#172）。新增可选设置 `WorkflowOptions:InternalCallBaseAddress`（默认 null）：设置后内部调用一律使用该地址（例如 `https://localhost:5001` 或内网地址），`RequireAuthentication=true` 时它优先于 `Domains:server` 与 AllowedHosts；值不是绝对 http/https URI 时会被忽略并输出 Warning。未设置、`RequireAuthentication` 关闭、且 `AllowedHosts` 是不含通配符的明确清单时，只有 Host 在清单内才发出内部调用，否则返回 400 并输出一次 Warning。未设置新选项且 `AllowedHosts` 为空、`*` 或含通配符的部署，行为与原来完全相同。使用明确 AllowedHosts 的部署，ASP.NET Core 的 host filtering 通常已先拦截不符合的 host，因此实际上几乎不会改变行为。建议正式环境设置 `WorkflowOptions:InternalCallBaseAddress`；回滚方式：移除该设置，或让 AllowedHosts 含 `*`。详见 FORK.md 第 11 节
* **新增：**   Oracle Database 最低支持版本为 19c（底层 ODP.NET 23；WTM 6.x 可连接 11g）。仍在使用 11g／12c 的应用，需先升级数据库，或改用其他整合方式。`Connections[].Version` 对 Oracle 只识别 19／21／23，其他值（例如 `11`、`12`）会改用 19 兼容模式（行为不变）；现在启动时会为每个这样的连接输出 Warning，说明已套用 19 兼容模式、且 19c 以下不受支持（#158）
* **文档：**   `docs/migrations/password-hash-upgrade.md` §4 启用 PBKDF2 的步骤新增“检查应用程序自己的密码代码”：直接调用 `Utils.GetMD5String` 比对或写入密码的自定义代码不经过 `PasswordHasher`，启用前须改用 `WTMContext.VerifyPassword`／`HashPassword`，否则已升级为 PBKDF2 的账号在这些路径会比对失败（#162）
* **修复（安全）：**   demo `AccountController.Reg`（匿名注册）遇到已存在的 ITCode 时，不再返回带 `ITCode` 字段的“账号重复”错误，改为与其他账号相关的注册失败返回同一个通用消息（HTTP 400，`Message` 为 `Sys.SubmitFailed`，`Form` 为空），避免被用来探测账号是否存在（#209）。必填、长度等格式验证错误仍保留明确消息，且在检查账号是否存在之前执行；注册成功的响应不变。仅限 demo（`demo/WalkingTec.Mvvm.Vue3Demo`），不影响 NuGet 包与 LayUI；响应时间差异（timing）不在本次范围内。详见 FORK.md 第 13 节

## 10.0.0.5(2026-10-19)

* **修改（Schema）：**   `FrameworkUserBase.Password` 长度由 32 调整为 128（为 PBKDF2 密码格式预留）。使用 `EnsureCreated` 的新数据库自动生效；既有数据库需手动加宽字段（见 [docs/migrations/password-hash-upgrade.md](docs/migrations/password-hash-upgrade.md)）；使用 EF Migrations 的应用需在同一次部署新增 AlterColumn 迁移，否则 EF 9+ 的 `Migrate()` 会因 PendingModelChangesWarning 失败
* **新增：**   启用 `PasswordOptions:Algorithm=Pbkdf2Sha256` 前会检查数据库中 Password 字段的实际长度，不足或无法确认时自动退回 MD5 并输出 Warning；默认仍为 MD5
* **修复（安全）：**   demo `FileApiController` 的 `UploadImage` 与 `GetFile` 缩略图在解码用户图片前先检查：只解析 JPEG／PNG／GIF／BMP／WebP（TIFF 等格式不再被解析），并限制文件大小、宽高、总像素、帧数与缩放目标尺寸，上限可通过 `ImageUploadLimits` 配置节调整。被拒绝时 `UploadImage` 返回原有的上传失败错误，`GetFile` 返回原文件。这是 ImageSharp 3.x 已知漏洞的短期缓解，验证码依赖支持 ImageSharp 4 后再升级（#78）
* **修改：**   固定传递依赖 System.Security.Cryptography.Xml 至 10.0.13（修复 GHSA-r955-8q5j-3h83、GHSA-w3x6-4m5h-cxqf 等；Elsa 2.17 经 DataProtection 引入的 9.0.1 存在已知高危漏洞），同时将 System.Security.Cryptography.Pkcs 的固定版本由 9.0.21 调整为 10.0.13（Xml 10.0.13 要求 Pkcs >= 10.0.13；仍满足 GHSA-846f-f764-76mx、GHSA-2fmv-rv2c-59x7）。固定写在 `WalkingTec.Mvvm.Core`（会传递给 Mvc 与 TagHelpers.LayUI），并在其 csproj 中取消 Xml 的 net10.0 包剪除，使固定版本写入 nuspec（否则不含 ASP.NET Core 共享框架的类库／测试项目仍会还原到 9.0.1）。使用 Web SDK 的应用本就不受影响（#153）
* **文档：**   `docs/migrations/password-hash-upgrade.md` §2 的 SQL Server 事前检查查询改为按 `TABLE_SCHEMA = SCHEMA_NAME()` 过滤（与程序实际检查一致），并一并列出 `DATA_TYPE`、`COLLATION_NAME` 与数据库默认排序规则，便于判断 §3 的 ALTER 是否需要写 `COLLATE`（已在 SQL Server 2019 实测 §2／§3／§5(a)）（#156）

## 10.0.0.4(2026-10-19)

* **修改（BREAKING）：**   Bearer（JwtBearer）认证改用与远程令牌相同的校验规则（#3）：令牌必须带 `exp`；只接受 HS256；校验 `nbf`；过期判定允许 30 秒时钟偏差（原为 0 秒）
* **新增：**   `JwtOptions:AllowTokensWithoutExpiry`（默认 false），过渡期内允许不带 `exp` 的令牌，启动时输出 Warning（#3）
* **新增：**   `Configs.ExposeExceptionDetails`（默认跟随 `IsQuickDebug`），可单独关闭响应中的异常详情；非 Development 环境开启 `IsQuickDebug` 时，启动输出 Critical 日志（#26）
* **新增：**   `CorsOptions:AllowCredentialsWithAnyOrigin`（默认 true，行为不变）；启用 `EnableAll` 但未配置 Policy 时，启动输出警告（#12）
* **新增：**   `Configs.BufferJsonBodyOnly`（默认 false，行为不变），开启后只缓冲 JSON 请求体（#17）
* **新增：**   运行期间热重载 JWT 签名密钥时输出警告：更换密钥需重启；改为不安全的密钥时输出 Critical（#21）
* **修复：**   Excel 导入：某个可选列为空时，后面各列不再错位（#86）
* **修复：**   生成的 Excel 导出文件与模板文件符合 OOXML 规范（#87）
* **修复：**   `wt:upload`、`wt:multiupload`、`wt:ueditor` 放在 `wt:form` 之外时不再抛出异常（#88）
* **修复：**   导出 zip 的临时路径在 Linux／macOS 上正确建立子目录（#89）
* **修复：**   CORS 的 `Domain` 配置项会去除空白；`Domain` 为空时不再导致启动失败（#12）
* **修改：**   `WtmMiddleware` 改为异步读取请求体，不再阻塞线程池线程（#17）
* **修改：**   升级存在已知漏洞的依赖，均在同一主版本内：`System.Security.Cryptography.Pkcs` 9.0.21、`SQLitePCLRaw.lib.e_sqlite3` 2.1.12、`Microsoft.OpenApi` 2.7.5（#14）。只有升级主版本才能修复的依赖见 #77、#78
* **修改：**   仓库示例密钥 `CHANGE-ME-development-only-placeholder-key` 加入不安全密钥清单（#15）
* **新增：**   `WorkflowOptions:RequireAuthentication`／`WorkflowOptions:RequireAdminRole`（默认均为 false）：开启后，工作流 API（`Elsa.Server.Api.*` 控制器）要求 WTM 已登录用户，并可额外要求流程管理员角色；启用了工作流但未开启时，启动输出 Warning。内部调用 `/v1/activities` 时，凭证只会转发到配置的 `Domains:server` 或 `AllowedHosts` 中的主机（#13）
* **修复：**   内部调用 `/v1/activities` 失败时，返回内部调用的状态码；原来会返回空白的 200 或 500（#13）
* **修改：**   LayUI TagHelper、`PrivilegeFilter`、`FrameworkFilter` 以及 `FResultExtension`（`Alert`／`Message`／`RefreshGrid`／`RedirectUrl`）中，嵌入 JavaScript 字符串的值统一通过新增的 `JsStringEncoder` 转义。解析后的值不变，因此页面显示不变；只有值中含引号、`<>&` 等字符时，生成的源码文字才会不同（#24、#25）
* **修复：**   表单服务端错误信息中的 `<`、`>` 原来会显示为非标准的 `&lg;`／`&rg;`，现在正确显示为文字（#24）
* **修复：**   `FResultExtension.RedirectUrl` 原来写成 `location.url`，实际不会跳转；现在改为 `location.href`，会真正跳转（#25）
* **修改：**   `framework_layui.js` 中按名称查找图表、默认值、筛选条件等全局变量时，改用 `window[...]`，不再使用 `eval`；找不到时略过，不再抛出异常（#25）
* **新增：**   `UIOptions:EncodeHtmlContent`（默认 false）：开启后，LayUI TagHelper 以 HTML 内容输出的数据文字（显示值、标题、label、按钮文字、流程信息、未自定义格式的表格单元格等）一律 HTML 编码；`wt:display` 可以用 `encode-html` 属性单独覆盖。默认输出不变（#24）
* **修改：**   LayUI TagHelper 手工拼接的 HTML 属性值，统一通过新增的 `HtmlAttributeEncoder` 编码（只编码 `& < > " '`）；DataTable 工具栏与行按钮的 event key 在三处表示中解码后保持一致。浏览器解码后的值不变（#24）
* **修改：**   Tab 生成的脚本不再使用 `eval`；ColorPicker 颜色值转义；Slider 的 `DefaultValue` 必须是数字或数字数组才会输出；Upload／MultiUpload 嵌在脚本中的 HTML 先做属性编码再做 JS 转义（#24、#25）
* **修复：**   本地文件存储：上传路径（`subdir`、`_subDirFunc`）必须位于分组根目录之内，否则拒绝；客户端文件名只取最后一段，扩展名只接受 1–10 个 ASCII 字母或数字；分组不存在时改用第一个本地分组，不再抛出 NullReferenceException。读取与删除的行为不变，已存路径位于当前配置的根目录之外时只输出 Warning（#11）
* **修复：**   demo 的 `FileApiController.UploadImage` 参数顺序错误（#11）
* **修复：**   Upload／MultiUpload 前端脚本插入 HTML 前，先对文件名等运行期的值做 HTML 编码（#121）
* **修复：**   Slider 的 `DefaultValue` 为单元素数组或空数组时不再抛出异常（#122）
* **修复：**   OSS 上传的 object key 拒绝绝对路径、盘符与 `..` 片段，并在上传前检查；扩展名改为清理后的值；存入数据库的 `FileName` 只保存最后一段，`FileExt` 为清理后的扩展名（#128）
* **新增：**   `PasswordOptions`（`Algorithm`：`Md5`（默认）／`Pbkdf2Sha256`；`Pbkdf2Iterations` 默认 600000）与 `PasswordHasher`：登录时同时接受旧的 MD5 与 `$pbkdf2-sha256$` 格式，以常数时间比较；`WTMContext` 新增 `HashPassword`／`VerifyPassword`。默认 MD5 时生成的哈希与 `Utils.GetMD5String` 完全相同，已存储的密码不会被改写。启用 `Pbkdf2Sha256` 前须先加宽用户表的 `Password` 字段（目前 32），字段放不下时自动沿用 MD5 并输出 Warning（#10）
* **修改：**   `WTMContext.DoLogin` 改为先按 ITCode、租户与 `IsValid` 查出用户，再在内存中校验密码（原为在 SQL 中比较哈希），账号不存在时同样计算一次哈希以拉平耗时。默认 MD5 下登录结果不变；唯一差异是以小写存储的 MD5 现在在区分大小写的数据库中也能匹配（#10）
* **修改：**   依赖更新（同一主版本内）：EF Core 10.0.13（Relational／SqlServer／InMemory）、Oracle.EntityFrameworkCore 10.23.26301、Microsoft.OpenApi 2.12.2 与 Swashbuckle.AspNetCore 10.3.x、Aliyun.OSS.SDK.NetCore 2.14.1、Fare 2.2.2。升级 OpenApi／Swashbuckle 后，生成的 swagger.json 中无类型（object／any）的属性不再输出 `nullable: true`（例如 `{"nullable":true}` 变为 `{}`），在 OpenAPI 3.0 中语义相同；路径、操作与其他 schema 不变，影响范围主要是 Elsa 与 System.Reflection 类型（#95）
* **升级说明：**
    1. 使用内置 `TokenService` 签发的令牌、以及 Cookie 登录（含 LayUI 页面）都不受 #3 影响
    2. 如有外部系统使用同一把密钥自行签发令牌，请确认这些令牌：使用 HS256、带有 `exp`、不带未来的 `nbf`。暂时无法带上 `exp` 的，可设置 `JwtOptions:AllowTokensWithoutExpiry=true` 过渡；算法与 `nbf` 的要求没有开关可以放宽
    3. 其余新增配置项的默认值都与原行为一致

## 10.0.0.3(2026-10-18)

* **新增：**   JwtOptions:AllowInsecureKey（默认 false）。设为 true 时，非 Development 环境下不安全的签名密钥不再阻止启动，改为每次启动输出 Critical 日志；仅作为无法立即更换密钥时的过渡手段
* **升级说明：**
    1. 默认行为与 10.0.0.2 相同，不影响已配置安全密钥的部署
    2. Critical 日志由 `UseWtmContext` 输出；从不调用 `UseWtmContext` 的非标准应用仍可正常启动，但不会有该日志

## 10.0.0.2(2026-10-18)

* **修改（BREAKING）：**   非 Development 环境下，若 JwtOptions.SecurityKey 未配置（框架默认值）、为空或长度不足 32 位、字符种类过少、或为仓库示例值（忽略大小写，含末尾补 x 的变体），应用启动时将直接失败；Development 环境仅输出警告
* **升级说明：**
    1. 除 Development（不区分大小写）以外的任何环境（包括 Staging、Test、CI 等）均视为生产环境；未设置环境名称时按 Production 处理。升级前请先通过环境变量 `JwtOptions__SecurityKey` 或密钥管理服务设置唯一的随机密钥（至少 32 位）
    2. 仅使用 Cookie 登录或 LayUI 页面的应用同样受影响：远程令牌（_remotetoken）与 Bearer 认证始终会被注册，启动时一律会检查该密钥
    3. 仓库中 demo 的 appsettings 示例密钥已不可用于非 Development 环境；官方 Docker 镜像以 Production 环境运行，部署时必须通过环境变量覆盖该密钥
    4. 负载均衡或多实例部署时，所有实例必须使用相同的密钥，否则令牌无法在实例间互相校验
    5. 使用 `AddWtmContextForConsole` 的控制台应用不受影响
    6. 更换密钥后，已签发的 JWT 将失效，用户需要重新获取令牌；Cookie 登录不受影响（Cookie 由 ASP.NET Data Protection 保护，不使用该密钥）
    7. 自 10.0.0.3 起可通过 `JwtOptions:AllowInsecureKey=true` 显式跳过该检查（仅限过渡期，不建议长期使用）

## 10.0.0.1(2026-10-18)

* **修复：**   远程登录令牌（_remotetoken）在使用前完整校验签名、签发者、受众与有效期，校验失败视为未登录
