# Fork 更新日志

本文件只记录本 fork 相对上游（dotnetcore/WTM）的变更，按版本从新到旧排列。上游的变更请见 [CHANGELOG.md](CHANGELOG.md)。

## 版本号规则

fork 的版本号为四段式：`<上游版本>.<fork 修订号>`，例如 `10.0.0.2` 表示基于上游 `10.0.0` 的第 2 个 fork 版本。

* 上游版本取自 `version.props`（与上游保持一致，不在 fork 中修改），fork 修订号 `ForkRevision` 定义在 `fork.props`
* 四段式版本不会与上游在 nuget.org 上发布的三段式版本冲突，且大于对应的上游版本（`10.0.0.2` > `10.0.0`）
* 每次发布 fork 版本时 `ForkRevision` 加 1；合并上游的版本号升级后重置为 1

## 未发布

* **修改（Schema）：**   `FrameworkUserBase.Password` 长度由 32 调整为 128（为 PBKDF2 密码格式预留）。使用 `EnsureCreated` 的新数据库自动生效；既有数据库需手动加宽字段（见 [docs/migrations/password-hash-upgrade.md](docs/migrations/password-hash-upgrade.md)）；使用 EF Migrations 的应用需在同一次部署新增 AlterColumn 迁移，否则 EF 9+ 的 `Migrate()` 会因 PendingModelChangesWarning 失败
* **新增：**   启用 `PasswordOptions:Algorithm=Pbkdf2Sha256` 前会检查数据库中 Password 字段的实际长度，不足或无法确认时自动退回 MD5 并输出 Warning；默认仍为 MD5
* **修复（安全）：**   demo `FileApiController` 的 `UploadImage` 与 `GetFile` 缩略图在解码用户图片前先检查：只解析 JPEG／PNG／GIF／BMP／WebP（TIFF 等格式不再被解析），并限制文件大小、宽高、总像素、帧数与缩放目标尺寸，上限可通过 `ImageUploadLimits` 配置节调整。被拒绝时 `UploadImage` 返回原有的上传失败错误，`GetFile` 返回原文件。这是 ImageSharp 3.x 已知漏洞的短期缓解，验证码依赖支持 ImageSharp 4 后再升级（#78）
* **修改：**   固定传递依赖 System.Security.Cryptography.Xml 至 10.0.13（修复 GHSA-r955-8q5j-3h83、GHSA-w3x6-4m5h-cxqf 等；Elsa 2.17 经 DataProtection 引入的 9.0.1 存在已知高危漏洞），同时将 System.Security.Cryptography.Pkcs 的固定版本由 9.0.21 调整为 10.0.13（Xml 10.0.13 要求 Pkcs >= 10.0.13；仍满足 GHSA-846f-f764-76mx、GHSA-2fmv-rv2c-59x7）。固定写在 `WalkingTec.Mvvm.Core`（会传递给 Mvc 与 TagHelpers.LayUI），并在其 csproj 中取消 Xml 的 net10.0 包剪除，使固定版本写入 nuspec（否则不含 ASP.NET Core 共享框架的类库／测试项目仍会还原到 9.0.1）。使用 Web SDK 的应用本就不受影响（#153）

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
