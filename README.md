# ClassislandControlHub集控

> **一处配置，全网生效 —— 让每一块教室大屏，都准时、统一、可控。**

[![构建](https://github.com/1sIancl/ClassislandControlHub/actions/workflows/build.yml/badge.svg)](https://github.com/1sIancl/ClassislandControlHub/actions/workflows/build.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

**ClassislandControlHub集控** 是面向 **ClassIsland 2.x（.NET 10）** 的班级大屏集中管理系统，分为 A、B 两端：

- **A 端 · 集控服务器**：ASP.NET Core 应用，自带可视化 Web 管理界面。可作为学校内网本地服务器运行，也可部署到服务器以网页形式访问，负责课表、时间表、科目的统一下发与设备管理。
- **B 端 · ClassislandControlHub集控接收端**：以 ClassIsland 插件形式运行在教室大屏上，连接 A 端，自动接收并应用**课表、时间表、科目、自定义设置**的下发与同步。

```
                ┌───────────────────────────────┐
                │  A 端 · 集控服务器（Web 管理端）  │
                │  ASP.NET Core + SQLite + Web UI │
                │  ┌─────────┐  ┌─────────────┐   │
                │  │ REST API│  │ UDP 自动发现 │   │
                │  └────┬────┘  └──────┬──────┘   │
                └───────┼──────────────┼──────────┘
                        │ HTTP + JSON  │ UDP 广播
           （注册/心跳/同步/长轮询/上报）  │
                        │              │
        ┌───────────────┴───────┬──────▼──────────┐
        │  B 端 · 教室大屏        │  B 端 · 教室大屏  │
        │  ClassIsland + 接收端   │  ClassIsland + 接收端│
        └───────────────────────┴─────────────────┘
```

## 安装教程

### 一、部署 A 端（集控服务器）

**方式 1：Linux 服务器一键部署（推荐）**

在目标 Linux 服务器（Ubuntu / Debian / CentOS / OpenCloudOS 等，需 root）上执行：

```bash
curl -fsSL https://cdn.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh -o /tmp/classislandcontrolhub-install.sh && sudo bash /tmp/classislandcontrolhub-install.sh
```

脚本会自动完成：安装 .NET 10 SDK → 拉取源码 → 编译发布 → 注册 systemd 服务并启动。完成后访问 `http://服务器IP:29800` 即可。

> **国内服务器提示**：
> - 下载脚本走 jsDelivr CDN（国内有节点），比 raw.githubusercontent.com 快且稳；若 jsDelivr 也超时，换备选镜像：
>   ```bash
>   curl -fsSL https://fastly.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh -o /tmp/classislandcontrolhub-install.sh && sudo bash /tmp/classislandcontrolhub-install.sh
>   ```
> - 脚本内拉源码已内置 GitHub 镜像自动回退；若仍失败，可显式指定镜像：
>   ```bash
>   GIT_MIRROR=https://kkgithub.com sudo bash /tmp/classislandcontrolhub-install.sh
>   ```

**方式 2：Windows / 本地运行**

前置：安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/1sIancl/ClassislandControlHub.git
cd ClassIsland.ControlHub
dotnet run --project src/ControlHub.Server -c Release
```

启动后浏览器访问 `http://localhost:29800`，局域网内其它机器用 `http://本机IP:29800` 访问。默认账号 `admin`，密码 `admin123`（登录后请立即修改）。

**方式 3：Windows 服务（管理员 PowerShell 一键）**

在仓库目录用**管理员身份**运行 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\sh\install-windows.ps1
```

脚本会发布服务端并注册为 Windows 服务（开机自启、崩溃自动重启），同时放行防火墙端口。数据保存在
`C:\ProgramData\ClassislandControlHub\data`，升级只需重新执行该命令。卸载用 `.\sh\uninstall-windows.ps1`。

### 二、安装 B 端（ClassislandControlHub集控接收端）

1. 构建插件包（或在 GitHub Releases 下载现成的 `.cipx`）：

   ```bash
   dotnet build src/ControlHub.Plugin/ControlHub.Plugin.csproj -c Release -p:CreateCipx=true
   # 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx 与 checksums.md（自动生成，无需 pwsh）
   ```

2. 在教室电脑安装 ClassIsland（2.1+），把 `.cipx` 放入插件目录，或通过 **ClassIsland 插件市场** 搜索「ClassislandControlHub集控接收端」安装。
3. 回到 A 端 Web 界面「设备管理 → 生成注册码」，复制注册码。
4. 打开 ClassIsland【应用设置 → ClassislandControlHub集控接收端】：
   - 点「自动发现服务器」（同一局域网），或手动填写服务器地址（如 `http://192.168.1.5:29800`）；
   - 填入注册码 → 「保存设置」→「立即同步」。
5. 设备即出现在「设备管理」列表中，配置下发数秒内自动生效。

### 三、开始使用

1. 登录 A 端 → 「配置档案 → 新建示例档案」自动生成标准作息 + 周一至周五课表 + 常用科目；
   已有课表可以用「CSES 导入」或「AI 导入」直接生成档案。
2. 按学校实际情况编辑：时间表（作息）、课表（周一到周日的排课网格）、科目。
3. 生成注册码，到各教室安装接收端并填入注册码。
4. 到「设备管理」按 **楼栋 → 楼层 → 教室** 整理终端：先「+ 新建楼栋」，再在楼栋里「+ 添加楼层」，
   最后把教室设备拖进对应楼层。给楼栋绑定档案，整栋教室就跟着走；某一层要单独用别的档案就在楼层上指定。

## 核心特性

- **多账号与按模块权限**：超级管理员 + 自定义权限账号，按「档案 / 设备 / 下发 / 远程 / 提醒 / 审计 / 备份 / 设置 / 账号」逐模块勾选查看与修改。
  服务端对每条管理端接口强制校验（未声明权限的路由一律拒绝），并有「不能授予自己没有的权限」「至少保留一个超级管理员」等提权防护；
  账号、个人偏好与个人提醒按用户隔离，设备、档案等学校数据保持共享。新同事有两种自助注册方式（各自独立开关，默认关闭）：
  凭**邀请码**直接注册，或**提交申请、由管理员审批**后建号。
- **定时提醒**：按「日期 + 时间 + 重复规则（仅一次 / 每天 / 每周 / 每月）」创建提醒，到点自动推送到指定教室 / 楼栋 / 全部在线教室，支持语音播报；
  按校正后的服务器时间触发（误差通常 15 秒内），并保留触发历史便于核对。提醒按账号隔离，互不干扰。
- **新手引导**：新账号首次登录自动播放分步引导，可随时跳过，也可在「系统设置」里重新观看。
- **配置档案**：时间表 / 课表 / 科目 / 自定义设置，按「设备单独指定 → 所属楼层 → 所属楼栋 → 全局默认」优先级下发；每个档案带唯一四位识别码，便于区分。
- **周课表网格**：课表按「周一到周日 × 节次」的表格排课，点格选课自动跳到下一节，双击清空，时间表可被多张课表复用。
- **楼栋 / 楼层 / 教室看板**：「设备管理」按「楼栋 → 楼层 → 教室」三级组织设备——顶部是楼栋区块与在线概览，
  楼栋里排一层层楼层卡片，教室就是卡片里的设备。楼栋与楼层可增删改并带标识色，
  教室设备拖拽即可换楼层/换楼栋，列表视图保留完整状态表。
- **CSES 导入**：支持从 ClassIsland 导出的 CSES（通用课表交换格式，`.yml`/`.yaml`）一键导入科目、时间表与逐格课表。
- **AI 辅助导入**：把任意格式的课表文本（Excel 粘贴、网页复制、手工整理）交给大模型，自动整理成作息时间表、科目与课表。接口地址、密钥与模型在「系统设置」中配置，兼容任何 OpenAI Chat Completions 服务。
- **版本化同步**：全局配置版本号 + 每设备推送世代号，精确统计「哪些设备还没取到最新配置」。
- **即时推送**：长轮询机制，管理员保存或推送后客户端数秒内自动同步。
- **定向下发**：可按分组/设备精准推送（给楼栋下发会自动覆盖其下楼层），不影响其他终端。
- **远程管理**：远程命令行（统一广播 / 单台执行并回报输出）、插件管理（查看 / 启用 / 禁用 / 卸载）、
  外观统一下发（主题 / 强调色，客户端实时生效）、向教室大屏发送提醒（支持语音播报）。
  指令为一次性派发（不会重复执行）、超 2 小时自动作废；广播默认只发在线设备，可勾选「含离线设备」排队。
- **备份与恢复**：A 端数据一键备份（手动/自动，保留最近 16 份），支持下载 zip 与恢复。
- **自动发现**：UDP 广播，接收端一键发现局域网内的服务器。
- **时间同步（NTP）**：A 端内置 SNTP 客户端从标准 NTP 服务器授时，并支持手动时间偏移；接收端自动与服务器对时，保证所有教室大屏时钟统一。
- **HTTPS**：可配置正式证书，也可在开启 HTTPS 且未提供证书时自动生成自签名证书（5 年有效，缓存在数据目录）。
- **部署形态**：Linux 一键 systemd、Windows 控制台运行或一键注册为 Windows 服务（开机自启、崩溃自动重启）。
- **界面自定义**：A 端 Web 界面支持主题（浅/深/跟随系统）、强调色、字体、圆角、密度与品牌个性化（站点名、Logo、图标）高度自定义。
- **自动更新**：A 端支持检查 GitHub Release 并一键更新（保留数据）。
- **审计与日志**：完整操作审计 + 客户端上报日志。

## 最近更新

- **发布 `1.2.0.0`**（A 端 `1.2.0` / 插件 `1.2.0.0`）：从本版本起，打标签即由 GitHub Actions 自动构建并发布
  （插件 `.cipx` + Linux / Windows 两份 A 端包），无需再手动打包上传。插件需在 ClassIsland 2.1.x 上使用。
- **Webhook 外部通知**：设备掉线、配置应用失败、远程指令失败时，自动推送到企业微信群机器人、钉钉（支持加签）、
  飞书或自定义端点。「系统设置 → Webhook 外部通知」可增删改、按事件订阅，并支持一键测试发送（失败会告诉你具体原因）。
- **两步验证（TOTP）**：「系统设置 → 账号安全」可开启基于时间的一次性密码（Google / Microsoft Authenticator
  等常见验证器都能用）：登录时密码通过后需再输入 6 位验证码。密钥只在绑定时显示一次，登录票据一次性、5 分钟过期。
- **危险操作的撤销窗口**：关机 / 重启计算机 / 睡眠 / 重启 ClassIsland 这类不可逆指令会**延迟 15 秒派发**，
  右下角出现倒计时提示条，点「撤销」即刻取消——延迟未到点的指令根本不会发给客户端，撤销是真实生效的。
- **界面自定义更完整**：除校名（站点名称）、Logo、浏览器图标外，现在还能设置**登录页背景图**
  （填图片地址或本地上传 ≤300KB），并可调「淡化程度」保证表单与文字始终清晰。
- **跨天 / 跨周临时换课**：在「配置档案 → 临时换课」里把某节课在指定日期范围内换成别的科目——
  可以只换一天，也可以持续到手动撤销，还能限定星期几。覆盖**不改动档案本身**，到期后教室自动恢复原课表；
  「预览当天课表」可直接看到某天实际会上什么。
- **课表历史版本与一键回滚**：每次保存配置档案前自动留一份快照（每个档案保留最近 20 份），
  改课表改坏了可在「配置档案 → 历史版本」里一键回滚；回滚前同样会自动备份当前内容，可以再滚回来。
- **通知更好用**：「远程管理 → 发送提醒」支持**多选设备**（任意勾选教室）与**通知模板**（常用广播内容一键套用）；
  设备列表支持**多选批量操作**：下发配置、发送通知、重启 ClassIsland、关机 / 重启计算机 / 睡眠。
- **远程触发自动化**：「远程管理 → 自动化」可列出并触发 ClassIsland 里配置了「信号触发器」的自动化
  （例如「放学后关闭屏幕」「上课前切换主题」），效果等于在教室里手动触发一次。
- **岗位角色模板**：新建 / 编辑账号时可一键套用「教务管理员 / 通知发布员 / 设备运维 / 只读观察员」，
  套用后仍可逐项微调；「只读观察员」的所有修改都会被服务端拒绝。
- **设备备注**：每台设备可写备注（如「三楼东侧」），列表与看板上直接显示，便于快速认出教室。
- **注册方式扩展为两种**：除凭邀请码直接注册外，新增**自助注册申请（默认关闭）**——用户在登录页填表提交，
  管理员在「系统设置 → 注册申请」里批准（勾权限）或拒绝（填理由），批准后才真正建号；
  申请中的密码只存哈希并在批准时原样启用，管理员全程看不到明文。登录页同时重做为「产品介绍 + 登录卡片」两栏版式。
- **多用户与按模块权限**：新增账号 CRUD、按模块勾选权限、邀请码自助注册（默认关闭）与新手引导；
  服务端此前只判断「是否登录」，现在每条管理端路由都强制校验权限（fail-closed），并做了提权防护。
- **定时提醒**：可按日期 / 时间 / 重复规则自动向教室大屏推送提醒，含触发历史；
  账号、个人偏好与提醒按用户隔离，设备与档案等学校数据仍共享。
- **设备管理改为「楼栋 → 楼层 → 教室」三级看板**：分组支持两级树结构，楼栋 / 楼层可增删改并带标识色，
  教室设备拖拽换层；顶部概览显示栋数 / 层数 / 教室数与在线率。默认档案按「设备 → 楼层 → 楼栋 → 全局」逐级回退。
  （原先独立的「分组管理」页已并入设备管理，旧链接 `#/groups` 仍可用。）
- **修复远程管理一批「界面看着正常、功能其实没生效」的问题**：B 端载荷解析大小写、插件启用状态与
  启停 / 卸载入口、命令被重复执行、指令回报越权、广播对象与界面文案不一致；外观下发现在真正即时生效。
- **CSES 导入一并导入逐格课表**（按 `enable_day` 生成每天课表，作息一致的天自动合并为一张时间表）。
- **HTTPS 自签名证书**与 **Windows 服务部署脚本**（`sh/install-windows.ps1`）。
- **插件市场上架准备**：`manifest.yml` 补齐市场字段，打包自动生成 MD5 摘要（不依赖 PowerShell 7）。

> 更早的改动见提交记录；上手指引见 [使用说明](USAGE.md)。

## 版本兼容性

本项目分 A 端（服务器）与 B 端（ClassIsland 插件）两部分，装插件前请先按下表确认版本：

| 集控版本 | 插件包 | 支持的 ClassIsland | 状态 |
|---|---|---|---|
| A 端 `1.2.0` / 插件 `1.2.0.0` | `ControlHub.Plugin.cipx` `1.2.0.0` | `2.1.x`（`apiVersion 2.1.0.0`，基于 `ClassIsland.PluginSdk 2.1.1.1`） | ✅ 当前版本 |
| A 端 `1.1.0` / 插件 `1.1.0.0` | `1.1.0.0` | `2.1.x` | ⚠️ 上一版本，建议升级（不含两步验证 / Webhook 等） |
| A 端 `1.0.x` / 插件 `1.0.0.0` | `1.0.0.0` | `2.1.x` | ⚠️ 早期版本，建议同步升级 |
| — | — | `2.2.x`（预览版） | ❌ 暂不支持 |

补充说明：

- 插件 `manifest.yml` 声明了 `apiVersion: 2.1.0.0` 与 `supportedOSPlatforms: [Windows]`，
  即**只支持 Windows 上的 ClassIsland 2.1.x**。ClassIsland 2.2 预览期 API 变动较大（不少插件都还没适配），
  本项目会在 2.2 正式版接口稳定后再跟进。
- A 端与插件通过 `ControlHub.Protocol` 共享同一份协议定义（协议版本 `1.0`）。**建议两端同步升级**：
  只升一端时注册、心跳与配置下发仍可用，但新增能力（远程命令、定时提醒等）需要两端都升级才会生效。
- A 端需要 **.NET 10 运行时**（用 `sh/main.sh` 一键脚本会自动装）；插件随 ClassIsland 进程运行，不需要单独装运行时。

## 技术栈

| 端 | 技术 |
|---|---|
| A 端 | ASP.NET Core（minimal API）、SQLite、原生 HTML/CSS/JS 单页应用（无构建步骤） |
| B 端 | ClassIsland 插件 SDK（`ClassIsland.PluginSdk 2.1.1.1`）、Avalonia 12 |
| 共享 | `ControlHub.Protocol`（net10.0，无外部依赖，System.Text.Json） |

## 文档

- [使用说明](USAGE.md)
- [通信协议与数据格式](docs/protocol.md)
- [架构与数据模型](docs/architecture.md)
- [部署与使用指南](docs/deployment.md)

## 许可证

本项目以 **GNU General Public License v3.0（GPL-3.0）** 发布，全文见 [LICENSE](LICENSE)。

**为什么是 GPL-3.0：**

- B 端插件依赖 **ClassIsland**——通过 `ClassIsland.PluginSdk` 包，并在编译期引用 ClassIsland 主程序集
  （`ClassIsland.dll`，`Private=false`，本身不打进插件包）。ClassIsland 本体已采用 **GPLv3**。
- 按 GPLv3 的传染性，与 ClassIsland 结合运行的插件属于衍生作品，必须以 GPLv3 兼容的许可证分发。
  由于插件、共享程序集 `ControlHub.Protocol` 与 A 端同处一个仓库、一起分发源码，
  **整个仓库统一采用 GPL-3.0**，避免出现「一部分 GPL、一部分闭源」的许可冲突。
- 第三方依赖的许可证均与 GPL-3.0 兼容：`Microsoft.Data.Sqlite`、Avalonia、FluentAvaloniaUI、
  CommunityToolkit.Mvvm、`Microsoft.Extensions.*`（均为 MIT）。

**这对使用者意味着：**

- ✅ 可自由使用、修改、再分发（校内自建、商用部署均可）。
- ✅ 再分发时（含分发编译好的 A 端 / 插件包）需同样以 GPL-3.0 提供**完整源码**并保留版权与许可声明。
- ℹ️ 仅在校内自行部署、不对外分发二进制，则没有额外的开源义务。

> A 端本身不依赖任何 GPL 组件。若你希望把 A 端单独以更宽松的许可证使用（例如闭源集成到自有系统），
> 欢迎开 Issue 说明用途，我们再单独讨论授权方式。
