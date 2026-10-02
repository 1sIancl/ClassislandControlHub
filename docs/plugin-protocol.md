# 插件扩展协议（草稿 v1）

> **状态：待评审的冻结草案。** 这份文档的目的不是描述现状，而是**先把接口定下来再写实现**——
> 第三方一旦接入，改协议的代价远大于晚一点上线。评审通过后才进入实现阶段（见文末落地顺序）。

## 1. 目标与边界

让第三方 ClassIsland 插件把自己的能力接进集控：由集控统一下发、展示、鉴权与审计。

**不做的事（v1 明确排除）**：

- 不在 A 端执行任何插件代码（不引入远程代码执行面）；
- 不提供插件自绘 UI——参数表单由 schema 自动生成，复杂交互请引导用户去 ClassIsland 本地界面；
- 不做自动重试与跨设备编排（留给后续的规则引擎）。

一句话：**插件在教室端进程内执行，集控只负责「传指令、校验、展示、记账」。**

## 2. 操作类型 ID

| 类别 | 取值 | 说明 |
|---|---|---|
| 内置 | `shell`、`notify`、`plugin.*`、`power.*`、`appearance.apply`、`automation.*`、`diagnostic.*` | 现状不变（见 `RemoteCommandKinds`） |
| 扩展 | **`ext.<插件ID>.<操作名>`** | 必须带 `ext.` 前缀 |

扩展 ID 约束：

- `<插件ID>` 用 ClassIsland 插件 ID（含点号，例如 `classisland.weather`）；
- `<操作名>` 只允许 `[a-z0-9-]`，整串长度 ≤ 96；
- 例：`ext.classisland.weather.refresh`；
- `ext.local.*` 保留给集控自身，第三方不得占用。

**A 端对不认识的 `ext.*` 一律拒绝**（fail-closed），并返回稳定错误码，避免「指令发出去石沉大海」。

## 3. 能力声明（注册）

插件随插件清单一起上报能力清单（新增**可选**字段，向后兼容）：

```jsonc
POST /api/v1/client/plugins
{
  "plugins": [ /* 现有字段不变 */ ],
  "capabilities": [
    {
      "kind": "ext.classisland.weather.refresh",
      "title": "刷新天气数据",
      "description": "让教室端重新拉取天气并刷新大屏显示",
      "pluginId": "classisland.weather",
      "permission": "remote.write",
      "idempotent": true,
      "maxDurationSeconds": 30,
      "parameters": { "type": "object", "properties": { } },
      "result": { "type": "object", "properties": { } }
    }
  ]
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `kind` | ✅ | 见第 2 节 |
| `title` / `description` | ✅ | 管理端展示用；标题 ≤ 30 字 |
| `pluginId` | ✅ | 提供方插件 ID，用于归属与权限分组 |
| `permission` | ✅ | 执行该能力需要的集控权限键（见第 5 节） |
| `idempotent` | ✅ | 重复执行是否安全；决定 TTL 内的派发语义（第 6 节） |
| `maxDurationSeconds` | ⬜ | 预期最长执行时间，仅用于展示与超时提示 |
| `parameters` | ⬜ | 参数 JSON Schema（第 4 节）；缺省表示无参数 |
| `result` | ⬜ | 结果结构，管理端据此渲染回报内容 |

A 端存储为 `device_capabilities.<deviceId>`（复用现有设置存储方式）。插件启停/升级后重新上报；
上报失败不影响其它功能（能力清单保持上一次的值）。

## 4. 参数 JSON Schema（受限子集）

只支持下列关键字，保证 A 端能**可靠地自动生成表单**（超出子集的能力声明会被忽略并记录原因）：

| 关键字 | 取值 |
|---|---|
| `type` | `string` / `number` / `integer` / `boolean`（枚举用 `enum`） |
| `enum` | 字符串数组，配合 `x-ui.widget = select` |
| `title` / `description` / `default` | 展示与默认值 |
| `required` | 对象级：必填属性名数组 |
| `x-ui.widget` | `text` / `textarea` / `number` / `switch` / `select` / `device` / `datetime` / `color` |
| `x-ui.placeholder` / `x-ui.rows` | 表单细节 |

限制：属性数 ≤ 20；单个字符串 ≤ 4 KB；**v1 不支持数组与嵌套对象**（确有需要时放字符串参数，由插件自行解析）。

## 5. 权限声明

- 每个能力必须声明一个集控权限键：`remote.read`（纯读取）或 `remote.write`（有副作用）；
- 后续可引入细粒度键 `ext.<插件ID>`（`PermissionKeys` 已预留命名空间），由管理员在账号里逐项勾选；
- A 端渲染与下发都以权限为准：当前账号无权限的能力**不显示**，即使直连接口也拒绝（沿用现有 fail-closed 校验）。

## 6. 载荷、回报与执行语义

| 环节 | 约定 |
|---|---|
| 载荷 | `RemoteCommandDto.Payload` 为按 `parameters` schema 校验后的 JSON 字符串 |
| 回报 | 复用 `CommandReportRequest`（`Success` / `Output` / `ExitCode` / `Error` / `FinishedAt`） |
| 输出上限 | 沿用现状 24 KB；结构化结果放 JSON 字符串，管理端按 `result` schema 渲染 |
| 派发 | 一次性派发（取走即标记已派发），TTL 默认 2 小时 |
| 超时 | `maxDurationSeconds` 只用于展示与提示，**不强制中断**（避免跨进程强杀） |
| 重试 | v1 不自动重试：设备重新上线后若指令未过期且未派发过会再次派发；`idempotent: false` 的能力在 TTL 内最多派发一次 |
| 审计 | 下发与结果都记审计（操作者 + kind + deviceId + 结果摘要） |

失败时 `Error` 必须是人类可读的一句话，并**同时**给出稳定错误码（第 7 节）：码用于统计告警，文案用于人看。

## 7. 错误码

| 码 | 含义 |
|---|---|
| `EXT_UNSUPPORTED` | 设备未声明该能力（或插件已卸载） |
| `EXT_INVALID_PAYLOAD` | 载荷不符合参数 schema |
| `EXT_DENIED` | 教室端本地策略拒绝（例如教师在插件设置里关闭了该能力） |
| `EXT_TIMEOUT` | 超过声明的 `maxDurationSeconds` |
| `EXT_INTERNAL` | 插件内部错误（详情放 `Error`） |

## 8. 版本化与兼容策略

- 协议版本沿用 `HubProtocol.Version`（当前 `1.0`，内部 `MajorVersion` / `MinorVersion`）；
- **Minor 递增 = 向后兼容**：新增可选字段、新增端点、新增能力；
- **Major 递增 = 破坏性变更**：重命名 / 删除字段、改变既有语义。

| 版本落差 | 约定行为 |
|---|---|
| A 端较新、插件较旧（无 `capabilities`） | 只显示内置操作；扩展操作不出现，不报错 |
| 插件较新、A 端较旧（旧端忽略 `capabilities`） | 扩展操作不出现；插件按 `apiVersion` 保持最小可用能力集 |
| 能力字段 A 端不认识（更高 Minor） | 用默认值展示；下发前仍做 schema 校验，不通过则拒绝并说明原因 |

**最小可用能力集**（任何组合都必须可用，新版本不得破坏）：设备注册、心跳、对时、配置下发、命令回报。

## 9. 落地顺序（实现阶段）

1. 上报通道加 `capabilities`（可选字段）+ A 端存储与展示；
2. 下发前校验：能力存在 + schema 通过 + 权限满足 → 拒绝时给稳定错误码；
3. 管理端按 schema 自动生成表单；命令历史按 `result` 渲染；
4. 插件 SDK 侧提供注册助手（`IControlHubCapability` 之类），插件只声明不问 JSON 细节；
5. 能力级权限键 `ext.<插件ID>`，管理员可细粒度授权。

前两步是「能用」，第 3 步是「好用」，第 4 步决定第三方接入成本——建议按顺序做，不要跳。
