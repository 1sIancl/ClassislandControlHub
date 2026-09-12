# HANDOFF.md · 给下一个接手的 AI

这份文档写给「接下来继续开发这个项目的 AI 或人」。读完后你应该能直接定位、构建、修改本项目，
并且不会重蹈本轮的坑。

## 1. 项目是什么 / 现状

ClassIsland 集控系统，A 端（服务器+Web 管理端）与 B 端（ClassIsland 插件）两部分的完整实现，
**当前已能编译、A 端已跑通 45 项端到端接口测试、插件已打出 `.cipx` 包**。

- 位置：`C:\Users\0524\WorkBuddy\2026-09-12-10-59-31\ClassIsland.ControlHub\`
- 解决方案：`ClassIsland.ControlHub.slnx`（.NET 10 默认生成 **slnx**，不是 sln）
- 三个项目：
  - `src/ControlHub.Protocol` —— 共享协议（常量/错误码/封套/DTO/校验和），无外部依赖
  - `src/ControlHub.Server` —— A 端 ASP.NET Core + SQLite + `wwwroot/` 原生前端
  - `src/ControlHub.Plugin` —— B 端 ClassIsland 插件（产出 `.cipx`）

## 2. 环境（重要，本机特殊）

- **.NET SDK 装在用户目录**：`C:\Users\0524\.dotnet\dotnet.exe`（版本 10.0.401）。系统 `dotnet` 宿主存在但**没有 SDK**。构建一律用完整路径，例如：
  ```bash
  "C:/Users/0524/.dotnet/dotnet.exe" build "C:/Users/0524/WorkBuddy/2026-09-12-10-59-31/ClassIsland.ControlHub/ClassIsland.ControlHub.slnx" -c Release
  ```
- NuGet 缓存已指向 `C:\Users\0524\.workbuddy\binaries\nuget\packages`（设 `NUGET_PACKAGES` 环境变量），避免重复下载。
- **Bash 工具的 PATH 是坏的**（`ls`/`head`/`tail` 等都 not found）。但 `python` 可用（`python -c "..."`），PowerShell 也可用。输出乱码是控制台 GBK 编码问题，不影响文件内容。
- **本机有 HTTP_PROXY**：`http://127.0.0.1:3707`。用 urllib 请求 localhost 必须绕过代理（`ProxyHandler({})`），否则返回 502。

## 3. 如何构建 / 打包

```bash
DN="C:/Users/0524/.dotnet/dotnet.exe"
ROOT="C:/Users/0524/WorkBuddy/2026-09-12-10-59-31/ClassIsland.ControlHub"
export NUGET_PACKAGES="C:/Users/0524/.workbuddy/binaries/nuget/packages"

# 全解决方案编译
"$DN" build "$ROOT/ClassIsland.ControlHub.slnx" -c Release

# 打包插件 .cipx（GenerateHashSummary=false 跳过依赖 pwsh 的 MD5 步骤）
"$DN" build "$ROOT/src/ControlHub.Plugin/ControlHub.Plugin.csproj" -c Release -p:CreateCipx=true -p:GenerateHashSummary=false
# 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx
```

运行 A 端做联调时，注意 `ContentRootPath` 已被设为 `AppContext.BaseDirectory`（`Program.cs`），
所以从任意工作目录启动都能找到 `wwwroot` 和 `appsettings.json`。

## 4. 已验证的事实（不要推翻重来）

- **A 端 45/45 项 E2E 通过**：注册/心跳/同步/长轮询/定向推送/分组/审计/静态资源/404 兜底等。测试脚本是工作区根目录的 `_e2e.py`（自拉起服务器 + 测完关闭）。
- 三端协议字段名、错误码、`ApiResult` 封套已对齐，改动任何 DTO 都要同步两端。

## 5. ClassIsland 插件 API 关键结论（本轮源码级核实）

这些是从 `ClassIsland.PluginSdk 2.1.1.1` / `ClassIsland.Core` / `ClassIsland.Shared` 的 NuGet 包和
GitHub 源码（commit `19bb60c`）核实过的，**别凭记忆改**：

- 目标框架：**net10.0**；UI 框架 Avalonia **12.1.1**；插件 SDK 包 `ClassIsland.PluginSdk`（2.1.1.1）。
- 插件入口：`[PluginEntrance] public class Plugin : PluginBase`，重写 `Initialize(HostBuilderContext, IServiceCollection)`。
  **`PluginBase` 没有 `OnShutdown()` 可重写**（旧文档有，实际 2.1.1.1 没有）。
- 注册设置页：`services.AddSettingsPage<T>()`，命名空间 `ClassIsland.Core.Extensions.Registry`（记得 `using`）。
- 设置页：继承 `ClassIsland.Core.Abstractions.Controls.SettingsPageBase`，加
  `[SettingsPageInfo(id, name, SettingsPageCategory.External)]`（`ClassIsland.Core.Attributes` + `ClassIsland.Core.Enums.SettingsWindow`）。
  **设置页用构造函数注入自己的服务**（ClassIsland 通过 DI 解析）；文档里的 `IAppHost.GetService<T>()` 在 2.x 不可用（`IAppHost` 不在 Core 程序集里）。
- 档案读写：`IProfileService`（`ClassIsland.Core.Abstractions.Services`）继承 `IPublicProfileService`：
  `Profile` 属性（get/set）、`SaveProfile()`。
- 模型（`ClassIsland.Shared.Models.Profile`）：
  - `Profile.TimeLayouts/ClassPlans/Subjects` 类型是 `ObservableOrderedDictionary<Guid,T>`（`ClassIsland.Shared.ComponentModels`），支持 `dict[key]=v`、`Remove(key)`、`ContainsKey`、`TryGetValue`。
  - `TimeLayout.Layouts` 是 `ObservableCollection<TimeLayoutItem>`；`TimeLayoutItem.TimeType` 是 **int**：0上课/1课间/2分割线/3行动；`StartTime`/`EndTime` 是 `TimeSpan`。
  - `ClassPlan.Classes` 是 `ObservableCollection<ClassInfo>`，按「时间表里 TimeType==0 的时间点」顺序对齐；`ClassInfo` 有 `Index`、`SubjectId(Guid)`、`IsEnabled`、`CurrentTimeLayout`。
  - `ClassPlan.TimeRule`：`Type=Weekly` + `WeekDay`(0=周日) + `WeekCountDiv`(0=不轮换, n=第n周) + `WeekCountDivTotal`(轮换总周数)。
  - `ClassPlanGroup.DefaultGroupGuid` = `ACAF4EF0-...`。
- 这些细节全部被隔离在 `ClassIslandAdapter.cs`，协议层用的是干净 DTO，改模型映射只动这一个文件。

## 6. 本轮踩过的坑（务必避免）

1. **对同一文件的多个 Edit 并行发会互相覆盖**（后写覆盖先写）。改同一文件必须串行，或一次性合并成单次 Edit。本轮因此丢过 `ReadDevice` 的字段赋值、`deploy.js` 的一处 dataset。
2. **C# 方法实参列表不允许尾逗号**（对象/集合初始化器可以）。写 `Foo(a, b,)` 会报 CS1525。
3. **`dotnet new sln` 在 .NET 10 生成 `.slnx`**，后续 `dotnet sln <名字>.sln add` 会失败，要用 `.slnx`。
4. **Avalonia 12 的 `TextBox.Watermark` 已废弃**，用 `PlaceholderText`。
5. **本地 dotnet 无 SDK**，别裸调 `dotnet`，用完整路径。
6. PowerShell 工具的输出经常被吞/乱码，**把结果 `Out-File` 再 `Read` 最稳**；用 python 读日志判断编译结果。

## 7. 接下来可以做的（尚未完成）

按优先级：

1. **真机验证 B 端**：在装了 ClassIsland 2.1 的机器上安装 `ControlHub.Plugin.cipx`，验证设置页 UI 渲染、注册、同步、课表真正显示到大屏。这是唯一没被自动化覆盖的部分。
2. **`TimeRule` 轮换映射的实测**：单双周（`WeekInterval=2`）→ `WeekCountDiv/WeekCountDivTotal` 的映射是按源码语义推的，建议在真机上对单双周课表做一次验证。
3. **HTTP 走代理的脚本**：若要把 A 端正式挂到内网，建议补一个 Windows 服务/NSSM 的启动脚本或 systemd unit。
4. **插件市场发布**：`.cipx` 若要走 ClassIsland 插件市场，需按官方要求生成含 MD5 摘要的包（装 pwsh 后去掉 `GenerateHashSummary=false`），并补仓库元信息。
5. **HTTPS 证书**：当前 `EnableHttps` 需自行配置 `CertificatePath/CertificatePassword`，可补自签证书自动生成。

## 8. 目录里的临时文件（可删）

工作区根目录 `C:\Users\0524\WorkBuddy\2026-09-12-10-59-31\` 下这些是本轮调试产生的，可安全删除：
`_e2e.py`、`_e2e_result.txt`、`_server.log`、`_server.out.log`、`_server.err.log`、
`_build*.log`、`_slnbuild*.log`、`_cipx.log`、`_ls.txt`、`_net.txt`、`_env.txt`、`_env2.txt`、
`dotnet-install.ps1`、`_sdk/`（含下载的 ClassIsland 包与源码参照）、`_e2edata/`。
其中 `_sdk/` 里的 `ClassIsland.Core.xml`、`ClassIsland.Shared.xml` 和 `ci_src/` 是很有用的 API 参照，
若还要动 B 端建议先留着。
