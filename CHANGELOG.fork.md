# Fork 更新日志

本文件只记录本 fork 相对上游（dotnetcore/WTM）的变更，按版本从新到旧排列。上游的变更请见 [CHANGELOG.md](CHANGELOG.md)。

## 版本号规则

fork 的版本号为四段式：`<上游版本>.<fork 修订号>`，例如 `10.0.0.2` 表示基于上游 `10.0.0` 的第 2 个 fork 版本。

* 上游版本取自 `version.props`（与上游保持一致，不在 fork 中修改），fork 修订号 `ForkRevision` 定义在 `fork.props`
* 四段式版本不会与上游在 nuget.org 上发布的三段式版本冲突，且大于对应的上游版本（`10.0.0.2` > `10.0.0`）
* 每次发布 fork 版本时 `ForkRevision` 加 1；合并上游的版本号升级后重置为 1

## 10.0.0.2(2026-10-18)

* **修改（BREAKING）：**   非 Development 环境下，若 JwtOptions.SecurityKey 未配置（框架默认值）、为空或长度不足 32 位、字符种类过少、或为仓库示例值（忽略大小写，含末尾补 x 的变体），应用启动时将直接失败；Development 环境仅输出警告
* **升级说明：**
    1. 除 Development（不区分大小写）以外的任何环境（包括 Staging、Test、CI 等）均视为生产环境；未设置环境名称时按 Production 处理。升级前请先通过环境变量 `JwtOptions__SecurityKey` 或密钥管理服务设置唯一的随机密钥（至少 32 位）
    2. 仅使用 Cookie 登录或 LayUI 页面的应用同样受影响：远程令牌（_remotetoken）与 Bearer 认证始终会被注册，启动时一律会检查该密钥
    3. 仓库中 demo 的 appsettings 示例密钥已不可用于非 Development 环境；官方 Docker 镜像以 Production 环境运行，部署时必须通过环境变量覆盖该密钥
    4. 负载均衡或多实例部署时，所有实例必须使用相同的密钥，否则令牌无法在实例间互相校验
    5. 使用 `AddWtmContextForConsole` 的控制台应用不受影响
    6. 更换密钥后，已签发的 JWT 将失效，用户需要重新获取令牌；Cookie 登录不受影响（Cookie 由 ASP.NET Data Protection 保护，不使用该密钥）

## 10.0.0.1(2026-10-18)

* **修复：**   远程登录令牌（_remotetoken）在使用前完整校验签名、签发者、受众与有效期，校验失败视为未登录
