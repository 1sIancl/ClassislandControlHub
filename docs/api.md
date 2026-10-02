# 对外 API 指南

给**要自己写脚本 / 对接监控 / 做二次开发**的人看的。完整字段定义见 [`protocol.md`](protocol.md)，
插件扩展相关见 [`plugin-protocol.md`](plugin-protocol.md)。

> **鉴权方式（推荐）**：用 **API 密钥** ——「系统设置 → API 密钥」或 `POST /admin/api-keys`。
> 密钥可以**只授予只读权限**、可以设有效期、可以随时撤销，不必为此专门建一个账号。
> 请求头二选一：`Authorization: ApiKey chk_...` 或 `X-Api-Key: chk_...`。
> 明文只在创建响应里出现**一次**（服务端只存哈希），丢了就重新签发。
> 出于安全考虑，「密钥管理」权限**不允许**授予密钥，避免一把密钥无限自我复制。

---

## 一、通用约定

| 项 | 约定 |
|---|---|
| 基址 | `http(s)://<服务器>:<端口>/api/v1` |
| 编码 | 请求与响应均为 UTF-8 JSON |
| 响应包装 | `{ "success": true, "data": ... }` 或 `{ "success": false, "error": { "code": "...", "message": "..." } }` |
| 时间 | 一律 ISO-8601 UTC（展示层再转本地时区） |
| 鉴权（管理端） | `Authorization: Bearer <登录令牌>` |
| 鉴权（教室端） | `Authorization: HubDevice <设备令牌>`（**不是** `Bearer`，写错会 401） |
| 权限 | 每个管理端接口都标注所需权限键，权限不足直接拒绝（fail-closed） |

### 错误码（完整列表，取自 `HubErrorCodes`）

| 错误码 | 含义 | 处理 |
|---|---|---|
| `AUTH_REQUIRED` | 请求未携带凭证 | 带上令牌 |
| `AUTH_INVALID` | 凭证无效或已过期 | 重新登录 |
| `PERMISSION_DENIED` | 已登录但当前账号没有该操作的权限 | 补权限，**不要重试** |
| `ACCOUNT_LOCKED` | 连续登录失败，账号被临时锁定（默认 5 次 / 15 分钟） | 等锁定窗口过去，别继续试 |
| `IP_NOT_ALLOWED` | 来源地址不在 `ControlHub:AdminIpAllowList` 内 | 从允许网段访问 |
| `VALIDATION_FAILED` | 参数不合法（消息里会说明哪个字段） | 修参数 |
| `NOT_FOUND` | 目标不存在（设备 / 档案 / 指令 / 记录） | 核对 ID |
| `CONFLICT` | 唯一约束冲突（例如重名） | 换一个值 |
| `RATE_LIMITED` | 请求过于频繁 | 退避后重试 |
| `DEVICE_UNKNOWN` | 设备令牌无效 | 设备需重新注册 |
| `DEVICE_REVOKED` | 设备已被管理员停用 | 在设备列表恢复 |
| `ENROLL_CODE_INVALID` / `ENROLL_CODE_EXPIRED` | 注册码错误 / 过期或用完 | 重新签发注册码 |
| `PROTOCOL_UNSUPPORTED` | 协议版本不受支持 | 升级客户端或服务端 |
| `INTERNAL` | 服务端异常 | 看服务端日志（带 `X-Request-Id` 一起查） |

> 每个响应都会回写 `X-Request-Id`；把它和服务端日志里的 `traceId` 对上，就能定位某一次请求的完整处理过程。

## 二、一分钟上手

```bash
BASE=http://127.0.0.1:29800/api/v1

# 1) 登录拿令牌（不要用 admin 跑脚本，建议单建最小权限账号）
TOKEN=$(curl -s -X POST "$BASE/admin/login" -H 'Content-Type: application/json' \
  -d '{"username":"monitor","password":"***"}' | jq -r .data.token)

# 2) 服务器状态（版本 / 运行时长 / 在线设备数；无需鉴权）
curl -s "$BASE/server/info" | jq '.data | {version, startedAt, uptimeSeconds, onlineDeviceCount, pendingDeviceCount}'

# 3) 设备列表（含离线原因，可直接用于告警文案）
curl -s "$BASE/admin/devices" -H "Authorization: Bearer $TOKEN" \
  | jq -r '.data[] | "\(.name)\t\(.online)\t\(.offlineReason // "-")\t\(.lastSeenAt)"'
```

## 三、常用接口

### 服务器（无需鉴权）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/server/info` | 版本、启动时间、运行时长、设备统计、数据目录 |
| GET | `/ping` | 探针 |
| GET | `/api/health` | 健康检查（注意：不在 `/api/v1` 下） |

### 设备

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/devices` | `devices.read` | 设备列表（状态、在线、版本、**离线原因**、备注） |
| PUT | `/admin/devices/{id}` | `devices.write` | 改名称 / 分组 / 绑定档案 / 备注 |
| POST | `/admin/devices/{id}/revoke` | `devices.write` | 停用 / 恢复 |
| DELETE | `/admin/devices/{id}` | `devices.write` | 删除（同事务清理其日志、指令、诊断记录） |
| GET | `/admin/devices/{id}/logs` | `devices.read` | 客户端上报日志 |
| GET | `/admin/devices/{id}/apply-diff` | `devices.read` | **下发前差异预览**（当前档案 → 目标档案的新增 / 删除 / 修改） |
| GET | `/admin/devices/{id}/plugins` | `remote.read` | 教室端插件清单 |
| POST | `/admin/devices/{id}/plugins/refresh` | `remote.write` | 命令教室端重新上报插件清单 |

### 配置下发

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/push` | `deploy.write` | 按范围推送：`{"scope":"all\|group\|device","targetIds":[],"message":"..."}` |
| GET | `/admin/assignments` | `devices.read` | 设备 / 分组与档案的绑定关系 |
| GET/POST/PUT/DELETE | `/admin/profiles...` | `profiles.*` | 档案增删改查；`/profiles/{id}/push` 单独推送；`/profiles/{id}/versions` 历史版本与回滚 |
| POST | `/admin/profiles/import-cses` | `profiles.write` | 从 CSES 课表导入 |

### 远程管理与诊断

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/devices/{id}/command` | `remote.write` | 单台下发指令：`{"kind":"shell","payload":"{\"command\":\"ipconfig\"}"}` |
| POST | `/admin/devices/command` | `remote.write` | 广播：加 `deviceIds` 与 `includeOffline` |
| GET | `/admin/devices/{id}/commands` | `remote.read` | 指令历史（含状态与输出） |
| GET | `/admin/devices/{id}/commands/queue` | `remote.read` | **待执行队列**（离线排队 / 定时未到 / 已派发在途 + 到期时间） |
| DELETE | `/admin/devices/commands/{id}` | `remote.write` | 取消尚未派发的指令 |
| POST | `/admin/devices/{id}/notify` | `remote.write` | 发送通知 |
| POST | `/admin/devices/appearance` | `remote.write` | 下发外观 |
| GET | `/admin/devices/{id}/diagnostics` | `remote.read` | 诊断工件列表（截图） |
| GET | `/admin/devices/diagnostics/{id}/content` | `remote.read` | 读取截图内容（图片字节流，**需带令牌**，不能直接给 `<img>` 用） |
| DELETE | `/admin/devices/{id}/diagnostics` | `remote.write` | 清空诊断记录 |

指令类型（`kind`）：`shell`、`plugin.*`、`appearance.*`、`notify`、`power.*`、`automation.*`、
`diagnostic.screenshot` / `diagnostic.processes` / `diagnostic.bundle`。语义：**一次性派发**、
**2 小时 TTL**、关机 / 重启有 **15 秒撤销窗口**。

### 教室端（设备侧）

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| POST | `/client/enroll` | 无（用注册码） | 注册，返回设备令牌 |
| POST | `/client/heartbeat` | `HubDevice` | 心跳（默认 30 秒），带上已应用版本 |
| GET | `/client/sync` | `HubDevice` | 拉取配置内容包 |
| GET | `/client/wait` | `HubDevice` | 长轮询（最长 60 秒），配置有变更即返回 |
| POST | `/client/report` | `HubDevice` | 上报配置应用结果（成功 / 部分 / 失败） |
| GET | `/client/commands` | `HubDevice` | 领取待执行指令（一次性：领取即标记已派发） |
| POST | `/client/commands/report` | `HubDevice` | 回报执行结果与输出 |
| POST | `/client/logs` | `HubDevice` | 上报日志 |
| POST | `/client/diagnostics` | `HubDevice` | 上传诊断工件（Base64，单张 ≤ 4 MB，类型白名单） |

## 四、写监控脚本的常见用法

```bash
# ① 离线超过 10 分钟的设备（直接可读的原因用于告警文案）
curl -s "$BASE/admin/devices" -H "Authorization: Bearer $TOKEN" \
  | jq -r '.data[] | select(.online == false and (.offlineMinutes // 0) > 10)
           | "\(.name) 离线 \(.offlineMinutes | floor) 分钟：\(.offlineReason)"'

# ② 排队太久还没执行的指令（可能设备一直没上线）
for id in $(curl -s "$BASE/admin/devices" -H "Authorization: Bearer $TOKEN" | jq -r '.data[].id'); do
  curl -s "$BASE/admin/devices/$id/commands/queue" -H "Authorization: Bearer $TOKEN" \
    | jq -r --arg id "$id" '.data[] | "\($id) 排队中：\(.kind)（\(.expiresAt) 到期）"'
done
```

## 五、注意事项

- **不要高频轮询 `/admin/devices`**：几百台时它要组装分组与状态；监控建议 30 秒 ~ 1 分钟一次。
  需要「变更即知」请用 Webhook（设备掉线 / 同步失败 / 指令失败会主动推送）。
- **截图内容必须带鉴权头读取**：浏览器直链不会带令牌，所以管理端是「先取 Blob 再展示」。
  脚本同理：`curl -H "Authorization: Bearer $TOKEN" .../content -o shot.png`。
- **写操作要幂等**：指令是一次性的，重试会重复执行（例如重复重启）；请用返回的 `commandId` 去重。
- 破坏性操作（删除设备 / 清空诊断 / 恢复备份）建议先在界面上做一次，确认语义后再写进脚本。
