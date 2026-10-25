# IslandHub

<img src="docs/images/logo.png" alt="IslandHub" width="120" />

> 一处配置，全网生效。教室大屏统一管：课表、作息、提醒、开关机。

[![构建](https://github.com/1sIancl/ClassislandControlHub/actions/workflows/build.yml/badge.svg)](https://github.com/1sIancl/ClassislandControlHub/actions/workflows/build.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

面向 **ClassIsland 2.1.1.1 及以上（.NET 10）** 的教室大屏集中管理系统，分两端：

- **A 端（服务器）**：ASP.NET Core + SQLite + Web 管理界面。装在校园内网或服务器上，统一管课表、作息、设备与下发。
- **B 端（接收端）**：ClassIsland 插件，装在教室机上，自动接收并应用课表 / 作息 / 科目 / 自定义设置。

## 功能

| 功能 | 说明 |
|---|---|
| 配置下发 | 档案（课表 + 作息 + 科目）按楼栋 / 楼层 / 教室下发，可预览差异 |
| 设备管理 | 楼栋 → 楼层 → 教室的分组与拖拽调整，标签、CSV 批量导入导出 |
| 远程管理 | 重启 / 关机 / 唤醒 / 音量亮度 / 截图 / 前台进程 / 远程命令 / 插件管理 |
| 定时提醒 | 按时间或设备上线触发，支持模板变量与变量替换 |
| 考试模式 | 一键切到考试档案，考完还原 |
| 走班制 | 按星期与时段自动切档案 |
| 快捷换课 | 临时换课、调休，按日期改某几节 |
| 大屏看板 | 值班室看的整屏概览（在线率、课表、最近事件、天气） |
| 通知与集成 | 定时通知、Webhook（企业微信 / 钉钉 / 飞书）、AI 辅助导入课表 |
| 插件管理 | 代理缓存 ClassIsland 插件市场、批量安装、完整性校验、运营标记 |
| 账号与权限 | 多账号、角色模板、权限矩阵、两步验证、密码策略 |
| 审计与审批 | 全量操作审计、危险操作转审批（非超管需超管确认） |
| 备份与恢复 | 手动 / 自动 / 更新前快照，加密导出与导入，一键回滚 |
| 其他 | 天气、时间偏移（含每日漂移）、界面多语言（中 / 英）、首次使用引导 |

完整操作说明见 [USAGE.md](USAGE.md)。

## 快速开始

### 一、A 端

**Linux 一键部署**（Ubuntu / Debian / CentOS 等，需 root）：

```bash
curl -fsSL https://cdn.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh -o /tmp/ich-install.sh \
  && sudo bash /tmp/ich-install.sh
```

脚本会装 .NET 10 SDK、拉源码、编译发布并注册 systemd 服务。完成后访问 `http://服务器IP:29800`。

> 国内网络可用备选镜像：把 URL 里的 `cdn.jsdelivr.net` 换成 `fastly.jsdelivr.net`；
> 拉源码失败时可加 `GIT_MIRROR=https://kkgithub.com`。

**Windows 本地运行**（需 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)）：

```bash
git clone https://github.com/1sIancl/ClassislandControlHub.git
cd ClassislandControlHub
dotnet run --project src/IslandHub.Server -c Release
```

**Windows 服务**（管理员 PowerShell）：`powershell -ExecutionPolicy Bypass -File .\sh\install-windows.ps1`
（数据在 `C:\ProgramData\IslandHub\data`，卸载用 `sh\uninstall-windows.ps1`）

默认账号 `admin` / `admin123`，登录后立即改密码。

### 二、安装包怎么选

同一版本有**两种 A 端包**，功能一致，只差要不要预装运行时：

| 包 | 体积 | 适用 |
|---|---|---|
| `controlhub-server-<平台>-<版本>.zip` | 1.6~2 MB | 目标机已装 .NET 10 运行时 |
| `controlhub-server-<平台>-full-<版本>.zip` | 约 48 MB | 自带运行时，解压即用（多数学校选这个） |

两者可互换，保留 `data/` 目录即可，设备无需重新注册。

### 三、B 端（教室端）

1. 在教室电脑安装 ClassIsland（2.1.1.1 及以上）。
2. 装插件：在 ClassIsland 插件市场搜「IslandHub 接收端」，或把 Release 里的 `.cipx` 放进插件目录。
3. A 端「设备管理 → 生成注册码」，复制注册码。
4. 教室里打开【应用设置 → IslandHub 接收端】→ 自动发现服务器或手填地址 → 填注册码 → 保存 → 立即同步。
5. 设备出现在「设备管理」列表，之后拖进对应楼层即可。

自己打包：`dotnet build src/IslandHub.Plugin/IslandHub.Plugin.csproj -c Release -p:CreateCipx=true`

## 版本兼容性

装插件前先按下表确认两端版本。A 端与插件通过 `IslandHub.Protocol` 共享同一份协议定义（协议版本 `1.0`），**建议同步升级**。

| IslandHub 版本 | 插件包 | 支持的 ClassIsland | 状态 |
|---|---|---|---|
| A 端 `1.4.8` / 插件 `1.4.8.0` | `ControlHub.Plugin.cipx` `1.4.8.0` | `2.1.1.1 及以上`（`apiVersion 2.1.1.1`） | ✅ **当前已发布版本** |
| A 端 `1.4.7` / 插件 `1.4.7.0` | `ControlHub.Plugin.cipx` `1.4.7.0` | `2.1.1.1 及以上`（`apiVersion 2.1.1.1`） | ⚠️ 上一版本 |
| A 端 `1.4.6` / 插件 `1.4.6.0` | `ControlHub.Plugin.cipx` `1.4.6.0` | `2.1.1.1 及以上`（`apiVersion 2.1.1.1`） | ⚠️ 旧版本 |
| A 端 `1.3.8` ~ `1.4.5` | 对应版本 `.cipx` | `2.1.1.1 及以上` | ⚠️ 旧版本，建议升级 |
| A 端 `1.0` ~ `1.3.7` | 对应版本 `.cipx` | `2.1.1.1 及以上`（部分为 `apiVersion 2.1.0.0`） | ⚠️ 早期版本 |
| — | — | `2.2.x`（预览版） | ❌ 未实测 |

说明：

- 插件 `manifest.yml` 声明 `apiVersion: 2.1.1.1`、`supportedOSPlatforms: [Windows]`，即**只支持 Windows 教室端**。
- 所有版本都用 `ClassIsland.PluginSdk 2.1.1.1` 编译，所以真实下限**始终是 2.1.1.1**；`apiVersion` 自 `1.3.8` 起与之一致。
- ClassIsland 2.2 与当前 SDK 是同期技术栈（Avalonia 12 / .NET 10），预期可用但**尚未实测**。
- A 端需要 .NET 10 运行时（一键脚本会自动装）；插件随 ClassIsland 进程运行，不需要单独装。
- 插件市场一列的含义：Release 带 `.cipx` 资产、标签符合 `tagPattern: "1.*.*.*"` 即可在市场搜到并安装；
  市场里搜不到时才需要从 [Releases](https://github.com/1sIancl/ClassislandControlHub/releases) 下载手动导入。

## 最近更新

- **`1.4.8.0`**（2026-10-25）：界面多语言扩到 22 个页面 + 全部复用模块 + 应用外壳；设备管理页抽完；
  新增备份加密导入、提醒富内容预览、首次使用引导页、插件市场运营（推荐 / 弃用标记与上架自检）。
- **`1.4.7.0`**（2026-10-24）：界面多语言扩到 19 个页面；修文案占位符（`${…}` 不再显示在界面上）。
- **`1.4.6.0`**（2026-10-18）：天气管理、回滚发布、操作审批、CodeQL、无障碍收尾、文档三件。

逐版细节见 [CHANGELOG.md](CHANGELOG.md)。

## 技术栈

| 层 | 选型 |
|---|---|
| 服务端 | ASP.NET Core 10、SQLite（`Microsoft.Data.Sqlite`）、长轮询 |
| 前端 | 原生 ES Module + CSS，无构建步骤 |
| 教室端 | .NET 10 + ClassIsland PluginSdk 2.1.1.1 |
| 桌面外壳（可选） | Avalonia 12 |

## 命名

产品名 **IslandHub**，代码标识符统一 `IslandHub.*`。以下**刻意保留旧名**，改名会让已部署机器配置失效：

- 配置节 `ControlHub`（`appsettings.json` / `ControlHub__Xxx` / `--ControlHub:Xxx`）
- Windows 服务名、安装目录 `%ProgramFiles%\ClassislandControlHub`、数据目录 `%ProgramData%\ClassislandControlHub\data`
- 数据库 `controlhub.db`、本机令牌 `local-shell.token`
- 局域网发现魔数 `ClassIsland.ControlHub`（协议常量，改了新旧端互相发现不了）
- 插件 id `classislandcontrolhub.client`、包名 `ControlHub.Plugin.cipx`
- GitHub 仓库 `1sIancl/ClassislandControlHub`（`manifest.yml` 的 `repoOwner` / `repoName` 需与之一致）

## 文档

- [使用说明](USAGE.md)
- [部署指南](docs/deployment.md) · [升级与回滚](docs/upgrade.md) · [故障排查](docs/troubleshooting.md) · [常见问题](docs/faq.md)
- [HTTP 接口](docs/api.md) · [协议说明](docs/protocol.md) · [插件扩展协议](docs/plugin-protocol.md) · [插件市场运营](docs/plugin-market-ops.md)
- [架构说明](docs/architecture.md) · [安全说明](docs/security.md) · [示例配置](docs/examples.md)
- [路线图](docs/roadmap.md) · [贡献指南](CONTRIBUTING.md)

## 许可证

[GPL-3.0](LICENSE)
