# 通信协议与数据格式

本文档定义集控系统 A 端（服务器）与 B 端（ClassIsland 插件）之间的通信协议。
协议常量、错误码与数据模型统一定义在共享程序集 **`ControlHub.Protocol`** 中，
两端引用同一份代码，从根本上避免「协议漂移」。

## 1. 传输层

| 项 | 约定 |
|---|---|
| 协议 | HTTP/1.1（可经反向代理升级为 HTTPS/HTTP2） |
| 数据格式 | JSON（UTF-8），camelCase 字段名，忽略 null |
| 内容类型 | `application/json; charset=utf-8` |
| 默认端口 | HTTP `29800`，HTTPS `29801` |
| 发现端口 | UDP `29810` |
| API 根路径 | `/api/v1` |

当前协议版本：`1.0`（`HubProtocol.Version`）。不兼容变更才递增主版本。

## 2. 统一响应封套

无论成功失败，HTTP 响应体一律使用如下结构（`ApiResult<T>`）：

```jsonc
// 成功
{ "ok": true, "data": { /* 业务数据 */ }, "error": null, "serverTime": "2026-09-12T03:00:00Z" }

// 失败
{ "ok": false, "data": null, "error": { "code": "AUTH_INVALID", "message": "登录状态已失效", "detail": null }, "serverTime": "..." }
```

HTTP 状态码与业务结果同时生效：`401` 鉴权失败、`404` 资源不存在、`400` 参数错误、`500` 内部错误等。

### 错误码（`HubErrorCodes`）

| 码 | 含义 |
|---|---|
| `AUTH_REQUIRED` | 缺少凭证 |
| `AUTH_INVALID` | 凭证无效或已过期 |
| `DEVICE_UNKNOWN` | 设备未注册 |
| `DEVICE_REVOKED` | 设备已被停用 |
| `ENROLL_CODE_INVALID` | 注册码错误 |
| `ENROLL_CODE_EXPIRED` | 注册码过期或用尽 |
| `VALIDATION_FAILED` | 参数校验失败 |
| `NOT_FOUND` / `CONFLICT` / `RATE_LIMITED` | 资源不存在 / 冲突 / 限流 |
| `PROTOCOL_UNSUPPORTED` | 协议版本不受支持 |
| `INTERNAL` | 服务器内部错误 |

## 3. 鉴权

| 角色 | 方案 | 请求头 |
|---|---|---|
| 管理员（Web 端） | `Bearer` | `Authorization: Bearer <adminToken>` |
| 设备（插件） | `HubDevice` | `Authorization: HubDevice <deviceToken>` |

- 管理员令牌由登录接口签发，默认 12 小时有效。
- 设备令牌由注册接口签发，长期有效，直至被吊销或重新注册。
- 客户端额外携带 `X-Hub-Client-Version: <pluginVersion>` 便于服务端统计版本分布。

### 管理端模块权限

登录只解决「你是谁」，能不能做还要看模块权限。每条管理端路由都用 `.RequirePermission(...)` 声明所需权限键
（约定为 `模块.read` / `模块.write`，定义在 `PermissionKeys`），**未声明权限的路由一律拒绝**（fail-closed）：

| 权限键 | 覆盖范围 |
|---|---|
| `profiles.read` / `profiles.write` | 配置档案查看 / 增删改与导入 |
| `devices.read` / `devices.write` | 设备、楼栋楼层分组与注册码 |
| `deploy.write` | 向教室终端下发配置 |
| `remote.read` / `remote.write` | 远程命令历史与插件清单 / 下发命令、插件、外观 |
| `reminders.read` / `reminders.write` | 定时提醒查看 / 增删改与立即触发 |
| `audit.read` | 审计日志 |
| `backup.read` / `backup.write` | 备份查看下载 / 创建、删除、恢复 |
| `settings.read` / `settings.write` | 品牌、时间偏移、AI 配置、更新 |
| `accounts.read` / `accounts.write` | 账号管理与邀请码 |

- `role=admin`（超级管理员）通吃全部权限且不可被裁剪；`role=custom` 按账号勾选的权限集合精确匹配。
- 两条约束：不能授予自己没有的权限；系统至少保留一个超级管理员。
- 权限变更后该账号的既有会话立即失效，需要重新登录才生效。
- 账号、个人偏好与个人提醒按用户隔离；设备、楼栋楼层、配置档案、注册码等为全校共享数据，靠权限控制谁能改。

## 4. 同步机制

同步采用 **「版本号 + 长轮询」** 模型，避免客户端高频轮询：

- **全局配置版本号 `revision`**：任何会影响下发内容的变更（保存档案、设默认、删档案）都使其递增。
- **每设备推送世代号 `pushEpoch`**：定向推送只递增目标设备的该值，全局版本不变。这样管理端能精确统计「哪些设备还没取到新配置」。
- 设备判断是否需要同步：`appliedRevision < revision` **或** `appliedPushEpoch < pushEpoch`。

### 长轮询

客户端调用 `GET /client/wait?revision=&pushEpoch=&timeout=`，服务器挂起该请求：

- 若期间版本号/世代号变化 → 立即返回 `{ changed: true, revision, pushEpoch }`；
- 否则最长挂起 `timeout` 秒（默认 25，上限 60）后返回 `{ changed: false }`。

客户端据此实现「管理员一推送，终端秒级拿到新配置」。

## 5. 接口一览

### 5.1 公开接口（无需鉴权）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/ping` | 存活探针 |
| GET | `/api/v1/server/info` | 服务器公开信息（名称、版本、端口、是否要求注册码等） |
| GET | `/admin/registration` | 注册方式开关（邀请码 / 自助申请）与相应提示 |
| POST | `/admin/register` | 凭邀请码自助注册（开关关闭或邀请码无效时拒绝） |
| POST | `/admin/register-requests` | 提交自助注册申请（进入待审批队列，不建号） |

### 5.2 设备接口（`HubDevice` 鉴权，注册/对时接口除外）

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/client/enroll` | 设备注册（无需鉴权） |
| GET | `/client/config` | 注册前读取服务器连接配置（无需鉴权） |
| GET | `/client/time` | 时间同步（无需鉴权，返回服务器 UTC 时间） |
| POST | `/client/heartbeat` | 心跳上报 |
| GET | `/client/sync` | 拉取配置（版本一致时 `data = null`） |
| GET | `/client/wait` | 长轮询等待变更 |
| POST | `/client/report` | 上报配置应用结果 |
| POST | `/client/time-report` | 上报 ClassIsland 授时结果 |
| GET | `/client/commands` | 拉取待执行命令（一次性派发） |
| POST | `/client/commands/report` | 回报命令执行结果 |
| POST | `/client/plugins` | 上报本机插件清单（含启用状态与版本） |
| POST | `/client/logs` | 上传客户端日志 |

> `/client/commands` 采用一次性派发：命令带 `dispatchedAt` / `expiresAt`（默认 TTL 2 小时），
> 取走后即标记为已派发，避免断线重连时重复执行；回报时会校验命令归属，跨设备回报被拒绝。

### 5.3 管理接口（`Bearer` 鉴权，标注「公开」的除外）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/login` | 公开 | 登录 |
| GET | `/admin/registration` | 公开 | 自助注册开关 |
| POST | `/admin/register` | 公开 | 凭邀请码自助注册 |
| POST | `/admin/logout` | 已登录 | 登出 |
| GET | `/admin/me` | 已登录 | 当前账号与权限（含 `onboardingDone`） |
| POST | `/admin/password` | 已登录 | 修改密码 |
| POST | `/admin/onboarding` | 已登录 | 标记新手引导已完成 |
| GET | `/admin/dashboard` | 已登录 | 仪表盘统计 |
| GET | `/admin/audit` | `audit.read` | 审计日志（分页） |
| GET | `/admin/permissions` | `accounts.read` | 可授予的权限目录 |
| GET/POST | `/admin/accounts` | `accounts.read` / `accounts.write` | 账号列表 / 新建（初始密码只显示一次） |
| PUT/DELETE | `/admin/accounts/{id}` | `accounts.write` | 修改账号与权限 / 删除账号 |
| POST | `/admin/accounts/{id}/reset-password` | `accounts.write` | 重置密码 |
| GET/POST | `/admin/register-codes` | `accounts.read` / `accounts.write` | 邀请码列表 / 生成 |
| DELETE | `/admin/register-codes/{code}` | `accounts.write` | 删除邀请码 |
| PUT | `/admin/registration` | `accounts.write` | 开关邀请码注册 |
| GET | `/admin/register-requests` | `accounts.read` | 自助注册申请列表（待审批排在最前） |
| POST | `/admin/register-requests/{id}/approve` | `accounts.write` | 批准申请：按勾选权限建号 |
| POST | `/admin/register-requests/{id}/reject` | `accounts.write` | 拒绝申请（可填理由，不建号） |
| DELETE | `/admin/register-requests/{id}` | `accounts.write` | 删除已处理的申请记录 |
| PUT | `/admin/registration-approval` | `accounts.write` | 开关自助注册申请 |
| GET | `/admin/devices` | `devices.read` | 设备列表（含在线状态、版本） |
| PUT | `/admin/devices/{id}` | `devices.write` | 修改设备（改名 / 调组） |
| POST/DELETE | `/admin/devices/{id}/revoke`、`/admin/devices/{id}` | `devices.write` | 停用 / 删除设备 |
| GET/DELETE | `/admin/devices/{id}/logs` | `devices.read` / `devices.write` | 设备日志查看 / 清空 |
| GET/POST | `/admin/groups` | `devices.read` / `devices.write` | 楼栋 / 楼层 / 教室分组列表 / 新建 |
| PUT/DELETE | `/admin/groups/{id}` | `devices.write` | 分组修改 / 删除（级联子节点） |
| GET/POST | `/admin/enroll-codes` | `devices.read` / `devices.write` | 注册码列表 / 生成 |
| PUT/DELETE | `/admin/enroll-codes/{code}` | `devices.write` | 注册码修改 / 删除 |
| GET | `/admin/assignments` | `devices.read` | 下发绑定关系视图 |
| POST | `/admin/push` | `deploy.write` | 立即推送（all/group/device） |
| GET/POST | `/admin/profiles` | `profiles.read` / `profiles.write` | 档案列表 / 新建 |
| POST | `/admin/profiles/sample` | `profiles.write` | 新建示例档案 |
| POST | `/admin/profiles/import-cses` | `profiles.write` | 导入 CSES（含逐格课表） |
| GET/PUT/DELETE | `/admin/profiles/{id}` | `profiles.read` / `profiles.write` | 档案详情 / 更新 / 删除 |
| POST | `/admin/profiles/{id}/default` | `profiles.write` | 设为默认 |
| POST | `/admin/profiles/{id}/push` | `deploy.write` | 推送到使用该档案的设备 |
| POST | `/admin/devices/{id}/command`、`/admin/devices/command` | `remote.write` | 单设备 / 批量下发远程命令 |
| GET | `/admin/devices/{id}/commands` | `remote.read` | 命令历史（含派发与完成状态） |
| POST | `/admin/devices/{id}/notify` | `remote.write` | 向教室发送临时通知 |
| POST | `/admin/devices/appearance` | `remote.write` | 下发外观（主题 / 强调色） |
| GET | `/admin/devices/{id}/plugins` | `remote.read` | 插件清单 |
| POST | `/admin/devices/{id}/plugins/refresh` | `remote.write` | 命令 B 端重新上报插件清单 |
| GET | `/admin/reminders`、`/admin/reminders/summary`、`/admin/reminders/fires` | `reminders.read` | 提醒列表 / 概览 / 触发历史 |
| POST/PUT/DELETE | `/admin/reminders`、`/admin/reminders/{id}` | `reminders.write` | 新建 / 修改 / 删除提醒 |
| POST | `/admin/reminders/{id}/run` | `reminders.write` | 立即触发（不影响原有重复计划） |
| GET/POST | `/admin/backups` | `backup.read` / `backup.write` | 备份列表 / 创建 |
| GET | `/admin/backups/{id}/download` | `backup.read` | 下载备份 |
| POST | `/admin/backups/{id}/restore` | `backup.write` | 恢复备份 |
| DELETE | `/admin/backups/{id}` | `backup.write` | 删除备份 |
| GET/PUT | `/admin/branding` | `settings.read` / `settings.write` | 品牌个性化读取 / 保存 |
| GET/PUT | `/admin/time-offset` | `settings.read` / `settings.write` | 手动时间偏移读取 / 设置（叠加到授时） |
| GET | `/admin/update/state`、`/admin/update/check` | `settings.read` | 更新状态 / 检查更新 |
| POST | `/admin/update/apply` | `settings.write` | 应用更新 |
| GET/PUT | `/admin/ai/config` | `settings.read` / `settings.write` | AI 配置读取 / 保存 |
| POST | `/admin/ai/test` | `settings.write` | AI 连通性测试 |
| POST | `/admin/ai/parse`、`/admin/ai/apply` | `profiles.write` | AI 解析自然语言课表 / 应用解析结果 |

## 6. 关键数据格式

### 6.1 设备注册

```jsonc
// 请求
{
  "enrollCode": "A1B2C3D4",
  "deviceId": "ci-<guid>",            // 客户端持久化的机器指纹
  "deviceName": "高一(3)班",
  "machineName": "CLASS-PC-01",
  "osVersion": "Windows 11 24H2",
  "classIslandVersion": "2.1.1.1",
  "pluginVersion": "1.0.0.0",
  "capabilities": ["timeLayouts", "classPlans", "subjects", "settings"]
}

// 应答
{
  "deviceId": "ci-<guid>",
  "deviceToken": "<长期令牌>",
  "deviceName": "高一(3)班",
  "groupId": null, "groupName": null,
  "revision": 3, "heartbeatSeconds": 30, "serverName": "XX 中学集控"
}
```

### 6.2 心跳

```jsonc
// 请求
{ "state": "idle", "appliedRevision": 3, "appliedPushEpoch": 0,
  "uptimeSeconds": 3600, "currentClassPlanName": "周一", "lastError": null,
  "metrics": { "memoryMb": "120" } }

// 应答
{ "revision": 3, "pushEpoch": 0, "shouldSync": false, "heartbeatSeconds": 30,
  "serverTime": "...", "message": null, "revoked": false }
```

### 6.3 配置同步应答（`SyncResponse`）

```jsonc
{
  "revision": 4, "pushEpoch": 0,
  "profileId": "<guid>", "profileName": "2026 春季学期",
  "issuedAt": "...", "force": false,
  "checksum": "sha256:<hex>",
  "content": { /* ContentBundleDto */ }
}
```

客户端用 `checksum` 做内容去重（避免重复应用），用 `revision`/`pushEpoch` 回传应用结果。

### 6.4 时间同步（`GET /client/time` + 内置 NTP 服务器）

用于让 B 端教室终端的 ClassIsland 大屏时钟以服务器为时间源。采用标准 NTP 协议：

- **A 端内置 NTP 服务器**（UDP `NtpPort`，默认 123，`NtpServer`）：标准 NTP 单播响应，回显客户端 T1、返回 T2/T3，时间来自 `ServerTimeService.GetUtcNow()`（NTP 校正 + 手动偏移）。
- **B 端**：把 ClassIsland 的「精确时间服务器」（`Settings.ExactTimeServer`）指向 A 端主机，启用「使用精确时间」（`IsExactTimeEnabled=true`）并触发同步。此后 ClassIsland 周期性从 A 端 NTP 服务器同步，**修改的是 ClassIsland 的时钟（精确时间），而非 Windows 系统时间**。
- `GET /client/time` 仍提供 HTTP 方式的服务器时间（匿名可访问），供需要 HTTP 对时的场景使用。

```jsonc
// GET /client/time 应答（data）
{ "serverTime": "2026-09-12T03:00:00Z" }
```

**授时链**：

```
标准 NTP（ntp.aliyun.com）
      │  A 端 SNTP 客户端（NtpClient）授时，得到 ntpOffset
      ▼
A 端时间 = 系统时间 + ntpOffset + 手动偏移（/admin/time-offset，settings 表 key=timeOffsetSeconds）
      │  A 端内置 NTP 服务器（UDP 123）下发
      ▼
ClassIsland「精确时间」（ExactTimeServer 指向 A 端，IsExactTimeEnabled=true）
      │
      ▼
教室大屏时钟（不改 Windows 系统时间，无需管理员权限）
```

### 6.5 内容包（`ContentBundleDto`）

```jsonc
{
  "timeLayouts": [ /* TimeLayoutDto[] */ ],
  "classPlans":  [ /* ClassPlanDto[]   */ ],
  "subjects":    [ /* SubjectDto[]     */ ],
  "settings":    { "values": {}, "lockLocalEditing": false, "announcement": null }
}
```

**时间点 `TimeLayoutItemDto`**

```jsonc
{ "startTime": "08:00:00", "endTime": "08:45:00", "kind": "class",
  "breakName": null, "isHideDefault": false, "defaultSubjectId": null }
```

`kind` 取值：`class`（上课）/ `break`（课间）/ `separator`（分割线）/ `action`（行动），
与 ClassIsland 的 `TimeLayoutItem.TimeType`（0/1/2/3）一一对应。

**课表 `ClassPlanDto`**

```jsonc
{
  "id": "<guid>", "name": "周一", "timeLayoutId": "<guid>",
  "isEnabled": true, "isOverlay": false, "overlaySourceId": null, "groupId": null,
  "daysOfWeek": [1], "weekInterval": 0, "weekOffset": 0,
  "slots": [ { "index": 0, "startTime": "08:00:00", "subjectId": "<guid>", "isEnabled": true } ]
}
```

- `slots[index]` 与时间表中第 `index` 个「上课」时间点一一对应，是两端对齐课表的核心字段。
- `daysOfWeek`：`0=周日 .. 6=周六`；`weekInterval`/`weekOffset` 用于单双周等轮换（`weekInterval=2, weekOffset=0` 即「单周」）。

**科目 `SubjectDto`**

```jsonc
{ "id": "<guid>", "name": "语文", "initial": "语", "teacherName": "", "isOutDoor": false }
```

## 7. 局域网发现（UDP）

客户端向广播地址的 UDP `29810` 端口发送探测包，服务器回应自身信息：

```jsonc
// 探测
{ "magic": "ClassIsland.ControlHub", "version": 1, "nonce": "<随机串>" }

// 应答（回显 nonce，便于客户端匹配）
{ "magic": "ClassIsland.ControlHub", "version": 1, "nonce": "<随机串>",
  "serverName": "XX 中学集控", "baseUrl": "http://192.168.1.5:29800",
  "apiPrefix": "/api/v1", "requiresEnrollCode": true,
  "revision": 0, "serverTime": "..." }
```

## 8. 数据约束与规范化

- 时间点由服务端按开始时间升序排列；分割线/行动类型的 `endTime` 强制等于 `startTime`。
- 课表 `slots` 由服务端按时间表节数对齐，越界的节点被丢弃，缺失的节点被补空。
- 科目/时间表/课表缺失 `id` 时由服务端自动分配；重复 `id` 会重新生成。
- 服务端保存档案时返回 `notes`，说明自动修复或跳过的问题，便于管理端提示用户。
