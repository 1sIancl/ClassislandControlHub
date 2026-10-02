# 贡献指南

感谢你有兴趣参与 ClassislandControlHub（集控）。本文档说明**怎么把项目跑起来、怎么改、怎么提交**。

## 一、开始之前

- 本项目分三部分：**A 端**（ASP.NET Core 服务端 + 原生 HTML/CSS/JS 管理界面）、**B 端**（ClassIsland 插件）、
  **桌面外壳**（可选，仅 Windows）。改哪一部分，先看清它的边界：见 README「能力边界」。
- **改协议前请先读 [`docs/plugin-protocol.md`](docs/plugin-protocol.md)**。协议反复改的代价远大于功能晚一点上，
  因此本项目的原则是**先冻结协议，再写实现**。
- backlog 与优先级在 [`docs/roadmap.md`](docs/roadmap.md)，它是单一事实来源，改了状态要同步更新。

## 二、环境准备

| 需要 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0 | 服务端、插件、外壳、压测工具都用它 |
| Node.js | 任意 LTS（可选） | 只用于 `node --check` 校验前端语法，前端**没有构建步骤** |
| ClassIsland | 2.1.x | 只有要在本机调试 B 端插件时才需要 |

```bash
git clone https://github.com/1sIancl/ClassislandControlHub.git
cd ClassislandControlHub
dotnet build ClassIsland.ControlHub.slnx -c Release
```

## 三、本地运行

```bash
# A 端（开发用，默认 29800 端口）
dotnet run --project src/ControlHub.Server

# 前端：直接改 src/ControlHub.Server/wwwroot 下的文件，刷新浏览器即可，无需打包
# 注意：改了 js/css 后要同步把 index.html 与各 import 的 ?v=NN 版本号 +1（浏览器缓存兜底）
```

B 端插件：

```bash
# 需要 ClassIsland 主程序集（本地已构建或从 Release 下载）后：
dotnet build src/ControlHub.Plugin -c Release -p:CreateCipx=true
# 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx
```

## 四、改代码时的约定

### 通用

- **构建必须 0 警告 0 错误**（项目开启了分析器，`CA1416` 之类的平台告警会被当问题看）。
- 注释写**为什么**，不写「这行在做什么」。凡是与直觉相反的实现（例如为什么截图走渲染管线而不是 GDI 抓屏），
  都要在代码里留下原因，否则下一个人很可能「顺手改回去」。
- 不认识的请求一律**拒绝**（fail-closed），不要因为「可能是新版本客户端」就放行。

### A 端（服务端）

- 数据库变更必须**无损**：在 `HubStore.InitializeAsync` 里用 `EnsureColumnAsync` 补列，不要改老库的建表语句。
- 新增管理端接口：在对应 `*Endpoints.cs` 里注册，并显式标注权限
  （`RequirePermission(PermissionKeys.XXX)`）；新增权限键要同时补进权限模板，否则老账号拿不到。
- 新增审计类操作时调用 `AddAuditAsync`，让「谁在什么时候做了什么」可追溯。
- 时间一律用 `DateTimeOffset.UtcNow` 存储，展示层再转本地时区。

### B 端（插件）

- 远程指令在 `RemoteCommandExecutor` 里实现，新指令要在 `RemoteCommandKinds` 里加常量并在 README / USAGE 里写明。
- 插件**不要**假设自己拥有管理员权限：涉及隐私或破坏性的能力（截图、命令行、电源）都要有教室端开关或明确提示。
- 任何上报失败都不应影响主流程（配置同步优先）。

### 前端

- 原生 JS，无框架、无构建。公共 UI 能力（`h` / `modal` / `confirmDialog` / `toast` / `emptyState`）在 `js/core/ui.js`，
  接口调用统一走 `js/core/api.js`。
- 需要鉴权的二进制内容（截图、备份下载）必须用 `fetchBlob`，**不要**用 `<a href>` 直链——浏览器不会带令牌。
- 新界面请同时在 700px 与 900px 断点下自测（手机端是电教委员的常用入口）。

## 五、提交与 PR

1. **一个 PR 做一件事**。重构与功能分开，方便回滚。
2. 提交信息用中文，首行写清类型与影响范围，例如：
   ```
   fix: 服务端「运行时长 / 启动时间」不再显示系统开机时长
   feat: 远程诊断（截图 / 前台进程 / 诊断数据包）
   ```
3. 提交前请自查：
   - [ ] `dotnet build -c Release` 0 警告 0 错误
   - [ ] 改了前端 js：`node --check` 通过、`?v=NN` 已 +1
   - [ ] 改了协议 DTO：`docs/protocol.md` 或 `docs/plugin-protocol.md` 已同步
   - [ ] 改了界面：700px / 900px 断点下试过
   - [ ] 改了数据库：老库（`EnsureColumnAsync`）升级路径验证过
4. 描述里请写**验证方式**：你怎么确认它真的能用（命令、返回值、截图都行）。只写「已测试」不算。

## 六、发布

发布由标签触发，不需要手工打包：

```bash
# 1) 三处版本号 + README 兼容表
#    src/ControlHub.Protocol/HubProtocol.cs      ProductVersion
#    src/ControlHub.Plugin/manifest.yml          version
#    src/ControlHub.Plugin/ControlHub.Plugin.csproj  Version
# 2) 提交后打标签并推送
git tag -a 1.4.0.0 -m "ClassislandControlHub 1.4.0.0" && git push origin 1.4.0.0
```

流水线会跑：Linux / Windows 两平台构建 A 端、打包 `.cipx`，并创建 Release（`release.yml`）。
**不要重打已发布过的标签**：插件市场按版本号判断更新，篡改已发布版本会让已安装用户收不到更新。

## 七、安全问题

发现漏洞请不要开公开 Issue，直接邮件联系维护者（见仓库主页），或使用 GitHub 的 Private vulnerability reporting。
上线前的加固清单见 [`docs/security.md`](docs/security.md)。
