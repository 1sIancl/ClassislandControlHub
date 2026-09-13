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

### 二、安装 B 端（ClassislandControlHub集控接收端）

1. 构建插件包（或在 GitHub Releases 下载现成的 `.cipx`）：

   ```bash
   dotnet build src/ControlHub.Plugin/ControlHub.Plugin.csproj -c Release -p:CreateCipx=true -p:GenerateHashSummary=false
   # 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx
   ```

2. 在教室电脑安装 ClassIsland（2.1+），把 `.cipx` 放入插件目录，或通过 **ClassIsland 插件市场** 搜索「ClassislandControlHub集控接收端」安装。
3. 回到 A 端 Web 界面「设备管理 → 生成注册码」，复制注册码。
4. 打开 ClassIsland【应用设置 → ClassislandControlHub集控接收端】：
   - 点「自动发现服务器」（同一局域网），或手动填写服务器地址（如 `http://192.168.1.5:29800`）；
   - 填入注册码 → 「保存设置」→「立即同步」。
5. 设备即出现在「设备管理」列表中，配置下发数秒内自动生效。

### 三、开始使用

1. 登录 A 端 → 「配置档案 → 新建示例档案」自动生成标准作息 + 周一至周五课表 + 常用科目。
2. 按学校实际情况编辑：时间表（作息）、课表（周一到周日的排课网格）、科目。
3. 建分组（如「高一年级」）并绑定档案，便于批量管理。
4. 生成注册码，到各教室安装接收端并填入注册码。

## 核心特性

- **配置档案**：时间表 / 课表 / 科目 / 自定义设置，按「设备单独指定 → 分组默认 → 全局默认」优先级下发。
- **周课表网格**：课表按「周一到周日 × 节次」的表格排课，时间表可被多张课表复用。
- **版本化同步**：全局配置版本号 + 每设备推送世代号，精确统计「哪些设备还没取到最新配置」。
- **即时推送**：长轮询机制，管理员保存或推送后客户端数秒内自动同步。
- **定向下发**：可按分组/设备精准推送，不影响其他终端。
- **自动发现**：UDP 广播，接收端一键发现局域网内的服务器。
- **时间同步（NTP）**：A 端内置 SNTP 客户端从标准 NTP 服务器授时，并支持手动时间偏移；接收端自动与服务器对时，保证所有教室大屏时钟统一。
- **品牌个性化**：自定义站点名称、Logo 与浏览器图标，仿企业级后台质感。
- **审计与日志**：完整操作审计 + 客户端上报日志。

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
