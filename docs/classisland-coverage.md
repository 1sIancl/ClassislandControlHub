# ClassIsland 功能覆盖矩阵

> 这份文档回答一个问题：**IslandHub 到底能管 ClassIsland 的哪些功能，哪些管不了、为什么。**
>
> 结论先行：ClassIsland 有 34 个宿主服务，其中 **9 类可完整管理、7 类部分可管、6 类管不了**，
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
| **自动化**（`AutomationService`） | 列出已定义的自动化、按 ID 触发 | **不能编辑自动化定义**（触发条件/动作链是宿主内部对象，无稳定 API）；`Action.Type=Automation` 只透传 ID |
| **音频**（`AudioService`） | 设音量 | 语音合成（TTS）未接：宿主用系统语音，跨进程拿不到句子断点与音色 |
| **时间**（`ExactTimeService`） | A 端给设备授时（`/time` 通道） | 不改宿主自己的时间校准策略 |
| **行动**（`ActionService`） | 行动的 `Type` 与原始配置**原样保存与下发** | **不在 A 端执行行动**：行动是宿主进程内对象，A 端没有执行入口 |

## 三、管不了的（6 类，附原因）

| 功能 | 为什么管不了 | 替代做法 |
|---|---|---|
| **主界面组件**（`ComponentsService`） | 组件是 Avalonia 控件实例，与宿主 UI 线程强绑定；跨进程序列化会丢掉控件状态，编辑布局必然崩 | 在教室端本机配置；A 端只下发档案与设置 |
| **规则集 / 触发规则**（`RulesetService`） | 规则对象里含宿主内部类型，没有稳定的跨进程契约 | 课表触发规则用档案里的 `DaysOfWeek` / 轮换表达；复杂规则在教室端配 |
| **附加设置写回**（`AttachedSettingsHostService`） | `AttachedObjects` 的 GUID → .NET 类型映射由宿主注册表决定，跨进程拿不到；塞错类型宿主读取时抛异常 | A 端完整保存（可编辑、可迁移），教室端沿用原值；导入预览里如实标注 |
| **迷你信息栏**（`MiniInfoProviderHostService`） | 同上：控件级服务 | 教室端本机 |
| **天气数据**（`WeatherService`） | 依赖宿主自己的 API Key 与缓存 | 教室端本机 |
| **主题包安装**（`XamlThemeService`） | 主题是随包编译的 XAML 资源 | 只下发配色/材质/字号 |

## 四、无需管理的（宿主内部 / 界面）

`BootService`、`SplashService`、`TutorialService`、`TaskBarIconService`、`UriNavigationService`、
`WindowRuleService`、`WallpaperPickingService`、`MemoryWatchDogService`、`RefreshingService`、
`AuthorizeService`、`ConsoleService`、`HangService`、`FileFolderService`、`IpcService`、`FooService`、
`PluginMarketService`（市场索引由 A 端自己的插件市场承担）。

这些是宿主进程内的启动、窗口、内存看门狗、授权、IPC 等，跨进程管理既没有入口，也没有意义。

## 五、通用保障（对每一类可管功能都成立）

| 关注点 | 做法 |
|---|---|
| **配置读写** | 内容整包存 `profiles.content`（JSON），下发时由教室端映射回宿主模型；改一次进一次版本历史，可回滚 |
| **状态同步** | 设备上线/心跳/指令回执；离线指令进队列，上线后按序补投（可设作废时限） |
| **权限** | 每个指令按权限键校验，超级管理员/自定义角色不同粒度 |
| **异常处理** | 指令失败带**错误码 + 可执行建议**（`COMMAND_UNSUPPORTED` / `HOST_API_UNAVAILABLE` / `PLATFORM_UNSUPPORTED` 等），不是一句"失败了" |
| **审计** | 谁在什么时候对哪台设备做了什么，全部落库，含失败原因 |
| **幂等** | 下发按内容版本号比对，内容没变不重发；指令有唯一 ID，重复投递被去重 |

## 六、一句话总结

**能管的都管到位了，管不了的都写明了为什么。**

课程表、时间表、课程切换、通知、插件、命令、诊断、电源、界面状态是完整覆盖的；
设置项与外观是「简单项可写、复杂项只读」；组件布局、规则集、附加设置写回、天气、主题包
受限于宿主进程边界，靠"在教室端本机配置 + A 端原样保存"来保证不丢数据。
