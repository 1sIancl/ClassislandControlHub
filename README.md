# ClassIsland 集控系统（ControlHub）

> **一处配置，全网生效 —— 让每一块教室大屏，都准时、统一、可控。**

面向 **ClassIsland 2.x（.NET 10）** 的班级大屏集中管理系统，分为 A、B 两端：

- **A 端 · 集控服务器（ControlHub.Server）**：ASP.NET Core 应用，自带可视化 Web 管理界面。既可作为学校内网本地服务器运行，也可部署到服务器以网页形式访问，负责配置分发与部署管理。
- **B 端 · 客户端插件（ControlHub.Plugin）**：以 ClassIsland 插件形式运行在教室大屏上，连接 A 端，自动接收并应用**课表、时间表、科目、自定义设置**的下发与同步。

```bash
# Linux 服务器一键部署（root 执行）
sudo bash <(curl -sL https://raw.githubusercontent.com/1sIancl/ClassIsland.ControlHub/main/sh/main.sh)
```

> 详细使用说明见 [USAGE.md](USAGE.md)。


```
                ┌───────────────────────────────┐
                │  A 端 · 集控服务器              │
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
        │  ClassIsland + 插件     │  ClassIsland + 插件│
        └───────────────────────┴─────────────────┘
```

## 目录结构

```
ClassIsland.ControlHub/
├─ ClassIsland.ControlHub.slnx        解决方案
├─ docs/
│  ├─ protocol.md                      通信协议与数据格式（完整规范）
│  ├─ architecture.md                  架构与数据模型
│  └─ deployment.md                    部署与使用指南
├─ HANDOFF.md                          给后续开发/接手的说明
└─ src/
   ├─ ControlHub.Protocol/             两端共享的通信协议与数据模型（无外部依赖）
   ├─ ControlHub.Server/               A 端服务器 + Web 管理界面（wwwroot/）
   └─ ControlHub.Plugin/               B 端 ClassIsland 插件（产出 .cipx）
```

## 快速开始

### 运行 A 端

```bash
cd src/ControlHub.Server
dotnet run -c Release
```

启动后浏览器访问 `http://localhost:29800`。默认管理员账号为 `admin`，初始密码在
`appsettings.json` 的 `ControlHub:DefaultAdminPassword` 中（默认 `admin123`），登录后请立即修改。

> 局域网访问：服务器默认监听 `0.0.0.0:29800`，同一局域网内用 `http://本机IP:29800` 访问即可。

### 使用 B 端

1. 在 A 端 Web 界面「设备管理 → 生成注册码」。
2. 在教室电脑的 ClassIsland 中安装插件（把 `src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx` 放入插件目录，或通过插件市场分发）。
3. 打开【应用设置 → 集控客户端】，点「自动发现服务器」或手动填写地址，填入注册码，点「立即同步」。

## 技术栈

| 端 | 技术 |
|---|---|
| A 端 | ASP.NET Core（minimal API）、SQLite、原生 HTML/CSS/JS 单页应用（无构建步骤） |
| B 端 | ClassIsland 插件 SDK（`ClassIsland.PluginSdk 2.1.1.1`）、Avalonia 12 |
| 共享 | `ControlHub.Protocol`（net10.0，无外部依赖，System.Text.Json） |

## 核心特性

- **配置档案**：时间表 / 课表 / 科目 / 自定义设置，按「设备单独指定 → 分组默认 → 全局默认」优先级下发。
- **版本化同步**：全局配置版本号 + 每设备推送世代号，精确统计「哪些设备还没取到最新配置」。
- **即时推送**：长轮询机制，管理员保存或推送后客户端数秒内自动同步。
- **定向下发**：可按分组/设备精准推送，不影响其他终端。
- **自动发现**：UDP 广播，插件一键发现局域网内的服务器。
- **审计与日志**：完整操作审计 + 客户端上报日志。

## 文档

- [通信协议与数据格式](docs/protocol.md)
- [架构与数据模型](docs/architecture.md)
- [部署与使用指南](docs/deployment.md)
- [交接说明（给后续开发）](HANDOFF.md)
