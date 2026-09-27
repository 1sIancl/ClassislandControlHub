# ClassislandControlHub集控

> **一处配置，全网生效 —— 让每一块教室大屏，都准时、统一、可控。**

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

- **设备管理改为「楼栋 → 楼层 → 教室」三级看板**：分组支持两级树结构，楼栋 / 楼层可增删改并带标识色，
  教室设备拖拽换层；顶部概览显示栋数 / 层数 / 教室数与在线率。默认档案按「设备 → 楼层 → 楼栋 → 全局」逐级回退。
  （原先独立的「分组管理」页已并入设备管理，旧链接 `#/groups` 仍可用。）
- **修复远程管理一批「界面看着正常、功能其实没生效」的问题**：B 端载荷解析大小写、插件启用状态与
  启停 / 卸载入口、命令被重复执行、指令回报越权、广播对象与界面文案不一致；外观下发现在真正即时生效。
- **CSES 导入一并导入逐格课表**（按 `enable_day` 生成每天课表，作息一致的天自动合并为一张时间表）。
- **HTTPS 自签名证书**与 **Windows 服务部署脚本**（`sh/install-windows.ps1`）。
- **插件市场上架准备**：`manifest.yml` 补齐市场字段，打包自动生成 MD5 摘要（不依赖 PowerShell 7）。

> 更早的改动见提交记录；上手指引见 [使用说明](USAGE.md)。

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
