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

### 5.2 设备接口（`HubDevice` 鉴权，注册接口除外）

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/client/enroll` | 设备注册（无需鉴权） |
| GET | `/client/time` | 时间同步（无需鉴权，返回服务器 UTC 时间） |
| POST | `/client/heartbeat` | 心跳上报 |
| GET | `/client/sync` | 拉取配置（版本一致时 `data = null`） |
| GET | `/client/wait` | 长轮询等待变更 |
| POST | `/client/report` | 上报配置应用结果 |
| POST | `/client/logs` | 上传客户端日志 |

### 5.3 管理接口（`Bearer` 鉴权，登录接口除外）

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/admin/login` | 登录 |
| POST | `/admin/logout` | 登出 |
| GET | `/admin/me` | 当前账号 |
| POST | `/admin/password` | 修改密码 |
| GET | `/admin/dashboard` | 仪表盘统计 |
| GET/POST | `/admin/groups` | 分组列表 / 新建 |
| PUT/DELETE | `/admin/groups/{id}` | 分组修改 / 删除 |
| GET/POST | `/admin/profiles` | 档案列表 / 新建 |
| POST | `/admin/profiles/sample` | 新建示例档案 |
| GET/PUT/DELETE | `/admin/profiles/{id}` | 档案详情 / 更新 / 删除 |
| POST | `/admin/profiles/{id}/default` | 设为默认 |
| POST | `/admin/profiles/{id}/push` | 推送到使用该档案的设备 |
| GET/PUT | `/admin/devices`、`/admin/devices/{id}` | 设备列表 / 修改 |
| POST/DELETE | `/admin/devices/{id}/revoke`、`/admin/devices/{id}` | 停用 / 删除 |
| GET/DELETE | `/admin/devices/{id}/logs` | 设备日志查看 / 清空 |
| GET/POST | `/admin/enroll-codes` | 注册码列表 / 生成 |
| PUT/DELETE | `/admin/enroll-codes/{code}` | 注册码修改 / 删除 |
| GET | `/admin/assignments` | 下发绑定关系视图 |
| POST | `/admin/push` | 立即推送（all/group/device） |
| GET | `/admin/audit` | 审计日志（分页） |
| GET | `/admin/accounts` | 管理员账号列表 |
| GET/PUT | `/admin/branding` | 品牌个性化读取 / 保存 |
| GET/PUT | `/admin/time-offset` | 手动时间偏移读取 / 设置（叠加到授时） |

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
