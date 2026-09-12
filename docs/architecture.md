# 架构与数据模型

## 1. 总体架构

```
┌────────────────────────────────────────────────────────────────┐
│  A 端 · ControlHub.Server（ASP.NET Core，net10.0）               │
│                                                                │
│  Endpoints/    ClientEndpoints · AdminEndpoints · DeviceEndpoints · ProfileEndpoints
│  Services/     SyncService · RevisionNotifier · AdminAuthService · DiscoveryService · MaintenanceService
│  Data/         HubStore（SQLite）                                │
│  Http/         AuthFilters · HubExceptionMiddleware             │
│  wwwroot/      index.html + css + js（原生单页应用）              │
└───────────────────────────────┬────────────────────────────────┘
                                │ 引用
                    ┌───────────▼────────────┐
                    │ ControlHub.Protocol     │  共享：常量/错误码/封套/DTO/校验和
                    └───────────┬────────────┘
                                │ 引用
┌───────────────────────────────▼────────────────────────────────┐
│  B 端 · ControlHub.Plugin（ClassIsland 插件，net10.0）            │
│                                                                │
│  Plugin.cs            入口（注册服务 + 设置页面 + 后台服务）         │
│  Services/             SyncEngine · HubClient · ServerDiscovery   │
│                        ClassIslandAdapter · HubState              │
│  Models/               PluginSettings · HubSettingsStore          │
│  Views/                ControlHubSettingsPage（代码构建 UI）        │
└────────────────────────────────────────────────────────────────┘
```

## 2. A 端分层

| 层 | 职责 |
|---|---|
| `Endpoints/` | 路由与参数绑定，只做「取参 → 调服务 → 回封套」 |
| `Services/` | 业务逻辑：解析档案、计算版本、推送、鉴权、发现 |
| `Data/HubStore` | 全部 SQLite 访问，按领域拆分为 partial 文件 |
| `Http/` | 鉴权过滤器、异常中间件、请求辅助 |

### 数据模型（SQLite 表）

| 表 | 关键字段 |
|---|---|
| `settings` | 键值设置；`revision`（全局版本号）就存这里 |
| `users` / `sessions` | 管理员账号与会话 |
| `groups` | 分组；`default_profile_id` 为组默认档案 |
| `profiles` | 配置档案；`content` 存整份 `ContentBundleDto` JSON，`revision` 为档案内容版本 |
| `devices` | 设备；`profile_id` 单独绑定，`push_epoch`/`applied_push_epoch` 支撑定向推送 |
| `enroll_codes` | 注册码；`used_count`/`max_uses`/`expires_at` |
| `audit_logs` / `client_logs` | 审计日志 / 客户端日志 |

> 档案内容以「整包 JSON」形式存于 `profiles.content`，而非拆分成多张表——这样下发时「取出来就能发」，
> 也天然支持任意自定义设置项，避免为每种配置加表。

### 版本与推送（核心机制）

- **全局版本 `revision`**：保存在 `settings` 表。任何内容变更通过 `HubStore.BumpRevisionAsync` 递增。
- **设备世代 `push_epoch`**：保存在 `devices` 表。定向推送只递增目标设备的该值。
- **唤醒**：`RevisionNotifier` 维护一个信号，`Publish()` 唤醒所有挂起的长轮询；等待者醒来后重新判定「版本变了 or 我的世代号变了」。

### 档案解析优先级

```
设备单独指定 profile_id  →  设备所属分组 default_profile_id  →  全局默认档案
```

由 `SyncService.ResolveProfileAsync` 实现，A 端与 Web 端统计共用同一逻辑。

## 3. B 端分层

| 组件 | 职责 |
|---|---|
| `SyncEngine`（BackgroundService） | 后台循环：注册 → 心跳 → 长轮询 → 拉取 → 应用 → 上报；含退避重连、令牌失效重注册 |
| `HubClient` | 无状态 HTTP 客户端，封装全部设备接口 |
| `ServerDiscovery` | UDP 广播发现服务器 |
| `ClassIslandAdapter` | **唯一的 ClassIsland 交互入口**：把 `ContentBundleDto` 写入 `IProfileService.Profile` 并保存 |
| `HubState` | 可观察的运行状态，供设置页面绑定 |

### 配置应用流程

```
SyncResponse.Content（DTO）
      │  ClassIslandAdapter.Apply
      ▼
Profile.Subjects / TimeLayouts / ClassPlans（ClassIsland 模型）
      │  profileService.SaveProfile()
      ▼
ClassIsland 大屏即时生效
```

**为什么要有 `ClassIslandAdapter` 这一层**：ClassIsland 2.x 的模型细节（`TimeLayoutItem.TimeType`
为 0/1/2/3 整数、`ClassPlan.Classes` 按 `TimeType==0` 时间点对齐、`ObservableOrderedDictionary<Guid,T>`
等）被隔离在此文件内。协议层保持「字符串类型 + 索引对齐」的干净表达；若 ClassIsland API 后续调整，
只需修改这一个文件。

### 离线与容错

- 设置与令牌持久化在 `settings.json`，重启后无需重新注册。
- 记录 `AppliedRevision`/`AppliedPushEpoch`/`LastAppliedChecksum`，重复推送不会重复覆盖。
- 断线指数退避（3s → 60s 封顶）；令牌失效（`DEVICE_UNKNOWN`）自动清空令牌并重注册。
- 被吊销（`DEVICE_REVOKED`）则停止同步并明确提示。

## 4. 关键设计取舍

1. **JSON 封套统一**：两端只处理一种响应格式，客户端 `HubClient` 统一抛 `HubApiException`。
2. **长轮询而非 WebSocket/SignalR**：更简单、穿透内网与反向代理更稳，且 `RevisionNotifier` 足够支撑秒级推送。
3. **共享协议程序集**：常量/DTO 单点定义，杜绝两端字段名、错误码不一致。
4. **A 端 UI 无构建步骤**：原生 ES Module + CSS 变量，双击 exe 即可用，便于内网环境离线部署。
5. **B 端 UI 用代码构建**：规避 Avalonia XAML 编译链依赖，降低插件构建门槛。
