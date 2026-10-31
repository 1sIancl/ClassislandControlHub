# ClassIsland 功能覆盖矩阵

> 这份文档回答一个问题：**IslandHub 到底能管 ClassIsland 的哪些功能，哪些管不了、为什么。**
>
> 结论先行：ClassIsland 有 34 个宿主服务，其中 **9 类完整可管、7 类部分可管、5 类文件级可管、1 类仍做不到**，
> 其余是宿主内部/界面服务（跨进程管理既无 API 也无必要）。
>
> 「管不了」不是没做完，而是**跨进程边界上确实没有稳定入口**——硬做会让教室端崩，
> 比不管更糟。每一条都写明了原因和替代做法。

## 一、能完整管理的（9 类）

| 功能 | 宿主模块 | 怎么管 |
|---|---|---|
| **配置档案**：时间表、课表、科目、自定义设置、多周轮换 | `ProfileService` | 档案 CRUD + 内容整体下发；`.cidata` / CSES / AI 导入；版本历史与回滚 |
| **时间表时间点**：上课 / 课间 / 分割线 / 行动，含附加设置与行动配置 | `ProfileService` | 编辑器里增删改；类型感知（分割线/行动强制零时长）；附加设置与行动内容原样透传 |
| **课表与课程切换**：临时换课、按天调整、课程目录 | `LessonsService` | `lessonCatalog` 取目录、`lessonSwap` 换课、`lessonAdjust` 调课 |
| **课表轮换**：单双周 / 多周 / 轮换起点 / 手动偏移 | `ProfileService` | 档案轮换设置 + 课表 `weekInterval` / `weekOffset` |
| **即时通知** | `NotificationHostService` | `notify`：标题、正文、全屏强调、语音播报、目标设备/分组 |
| **插件** | `PluginService` | 列表与版本、安装（下载+校验+落盘）、启用/禁用、卸载 |
| **远程命令** | 宿主进程 | `shell`：任意命令 + 超时 + 输出 + 退出码 |
| **诊断** | `DiagnosticService` | 截图、进程列表、诊断包（含日志）、网络探测（Ping / 路由追踪 / 磁盘） |
| **电源与界面状态** | 宿主进程 | 关机 / 重启 / 睡眠；主界面显隐 / 全屏；音量 / 亮度 |

## 二、部分可管的（7 类）

| 功能 | 能做到 | 做不到 / 边界 |
|---|---|---|
| **宿主设置项**（`SettingsService`，`Settings.cs` 约 2400 行） | `settings.list` 列出设置项与当前值；`settings.set` 改**简单类型**（开关 / 数字 / 文本 / 选项） | 复杂对象（主题实例、规则集、插件设置页）只读；改动是否立即生效由宿主决定，部分需重启 |
| **外观与主题**（`ThemeService` / `XamlThemeService`） | 下发主题模式（浅/深/跟随系统）、强调色、字体、窗口材质（Mica/Acrylic/LiquidGlass/关） | 不能安装/切换第三方主题包（宿主主题是编译进包的 XAML 资源） |
| **定时提醒**（`NotificationWorkerService`） | A 端自己调度（每天/每周/每月/仅一次），到点通过 `notify` 投递 | 不接管宿主自己的提醒队列；两边同时配会各发各的 |
| **自动化**（`AutomationService`） | 列出并触发；**定义走文件级管理**（`Config/Automations/*.json`，改完重启生效） | 不在内存里改定义（触发条件/动作链是宿主内部对象）；`Action.Type=Automation` 只透传 ID |
| **音频**（`AudioService`） | 设音量 | 语音合成（TTS）未接：宿主用系统语音，跨进程拿不到句子断点与音色 |
| **时间**（`ExactTimeService`） | A 端给设备授时（`/time` 通道） | 不改宿主自己的时间校准策略 |
| **行动**（`ActionService`） | 行动的 `Type` 与原始配置**原样保存与下发** | **不在 A 端执行行动**：行动是宿主进程内对象，A 端没有执行入口 |

## 三、文件级可管（不实时同步，重启生效）

这几类没有跨进程 API——组件是 Avalonia 控件实例、规则集是宿主内部对象——但**它们的配置就是磁盘上的 JSON**。
所以走文件级管理：`config.list` 读清单 → `config.read` 读原文 → `config.write` 写回 → **重启宿主生效**。

| 功能 | 配置文件 | 能做什么 |
|---|---|---|
| **主界面组件布局**（`ComponentsService`） | `Config/ComponentLayouts/*.json` | 读懂并改整份布局（组件、位置、参数），整份写回 |
| **自动化定义**（`AutomationService`） | `Config/Automations/*.json` | 增删改自动化（触发条件 + 动作链），不再只能"列出来触发" |
| **主题**（`ThemeService` / `XamlThemeService`） | `Config/EnabledThemes.json`、`Config/Themes/` | 改启用列表；主题包本身是编译进宿主包的 XAML 资源，仍不能从外部安装 |
| **插件配置** | `Config/Plugins/{id}/*.json` | 改插件自己的设置文件（如注入器的 `settings.json`），比让用户手改文件安全 |
| **管理策略** | `Config/Management/Policy.json` | 改策略项；**`Credentials.json` 始终拒绝**（凭据不走远程通道） |

写回前有三道关卡，一道都不省：**路径规范化 → 白名单首段 → 目标在配置目录内**（`ConfigPathPolicy`）；
写时**校验 JSON → 备份 `.bak` → 落盘**。写坏配置会让教室端起不来，所以不是"能写就写"。

## 四、仍做不到的（1 类）

| 功能 | 为什么 | 现状 |
|---|---|---|
| **附加设置写回宿主**（`AttachedSettingsHostService`） | `AttachedObjects` 的 GUID → .NET 类型映射在宿主注册表里，跨进程拿不到；直接塞 JSON 进运行中的对象，宿主读取时抛异常 | A 端**完整管理**（查看、编辑、备份、迁移、导出）；不自动写回。要落到教室端目前靠「导出 `.cidata` → 教室端导入」，或等宿主开放映射接口 |

> 迷你信息栏与天气的配置在 `Settings.json` 里（不是独立文件），已由 <code>settings.list</code> / <code>settings.set</code> 覆盖：
> 简单项（开关、数值、文本）可改，复杂对象只读。

## 五、无需管理的（宿主内部 / 界面）

`BootService`、`SplashService`、`TutorialService`、`TaskBarIconService`、`UriNavigationService`、
`WindowRuleService`、`WallpaperPickingService`、`MemoryWatchDogService`、`RefreshingService`、
`AuthorizeService`、`ConsoleService`、`HangService`、`FileFolderService`、`IpcService`、`FooService`、
`PluginMarketService`（市场索引由 A 端自己的插件市场承担）。

这些是宿主进程内的启动、窗口、内存看门狗、授权、IPC 等，跨进程管理既没有入口，也没有意义。

## 六、通用保障（对每一类可管功能都成立）

| 关注点 | 做法 |
|---|---|
| **配置读写** | 内容整包存 `profiles.content`（JSON），下发时由教室端映射回宿主模型；改一次进一次版本历史，可回滚 |
| **状态同步** | 设备上线/心跳/指令回执；离线指令进队列，上线后按序补投（可设作废时限） |
| **权限** | 每个指令按权限键校验，超级管理员/自定义角色不同粒度 |
| **异常处理** | 指令失败带**错误码 + 可执行建议**（`COMMAND_UNSUPPORTED` / `HOST_API_UNAVAILABLE` / `PLATFORM_UNSUPPORTED` 等），不是一句"失败了" |
| **审计** | 谁在什么时候对哪台设备做了什么，全部落库，含失败原因 |
| **幂等** | 下发按内容版本号比对，内容没变不重发；指令有唯一 ID，重复投递被去重 |

## 七、一句话总结

**能管的都管到位了；不能实时管的，改成「改文件 + 重启生效」也能管；真管不了的只剩一条，并写明了原因。**

课程表、时间表、课程切换、通知、插件、命令、诊断、电源、界面状态是完整覆盖的；
设置项与外观是「简单项可写、复杂项只读」；主界面组件、自动化定义、主题、插件配置、管理策略
走**文件级管理**（读原文 → 改 → 写回，重启生效，写前校验 JSON + 备份 .bak）；
附加设置 A 端完整保存与编辑，但**写回宿主**还差一个 GUID → .NET 类型映射，目前靠导出 .cidata 落地。
