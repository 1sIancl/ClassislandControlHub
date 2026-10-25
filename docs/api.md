# 对外 API 指南

给**要自己写脚本 / 对接监控 / 做二次开发**的人看的。完整字段定义见 [`protocol.md`](protocol.md)，
插件扩展相关见 [`plugin-protocol.md`](plugin-protocol.md)。

> **鉴权方式（推荐）**：用 **API 密钥** ——「账号与权限 → API 密钥」或 `POST /admin/api-keys`。
> 密钥可以**只授予只读权限**、可以设有效期、可以随时撤销，不必为此专门建一个账号。
> 请求头二选一：`Authorization: ApiKey chk_...` 或 `X-Api-Key: chk_...`。
> 明文只在创建响应里出现**一次**（服务端只存哈希），丢了就重新签发。
> 出于安全考虑，「密钥管理」权限**不允许**授予密钥，避免一把密钥无限自我复制。
>
> **想直接导入工具？** 服务端提供机器可读的 OpenAPI 3.1 规范，见文末
> [《六、机器可读的规范（OpenAPI）》](#六机器可读的规范openapi)。

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

### 教室端指令错误码（`CommandErrorCodes`，随指令回报上报）

与上面那组刻意分开：上面是**管理端接口**为什么拒绝了这次请求（会映射成 HTTP 状态码），
这里是**教室端**为什么没做成——取值只出现在指令记录里（`DeviceCommandDto.errorCode`），
用于展示与「怎么办」。房间端不上报时该字段为空。

| 错误码 | 含义 | 处理 |
|---|---|---|
| `COMMAND_UNSUPPORTED` | 教室端不认识该指令，或不支持其中的某项配置 | 升级教室端插件 / ClassIsland（多半版本低于管理端） |
| `COMMAND_PAYLOAD_INVALID` | 载荷缺字段或格式不对 | 重新填一遍；反复出现先确认两端版本配套 |
| `COMMAND_TARGET_NOT_FOUND` | 指令要操作的对象不存在（如插件已被卸载） | 先「刷新插件列表」再重新选择 |
| `HOST_API_UNAVAILABLE` | 宿主未提供所需接口（版本偏低或服务未就绪） | 升级并重启教室端的 ClassIsland |
| `PLATFORM_UNSUPPORTED` | 教室端系统不支持该操作（如非 Windows 的电源管理） | 改用受支持的操作，或手动处理 |
| `COMMAND_DISABLED_BY_POLICY` | 教室端自己关掉了该能力（如「远程协助与隐私」关了截图） | 到那台教室机上打开对应开关 |
| `COMMAND_EXECUTION_FAILED` | 执行过程中失败（原因看原文） | 按原文到教室机上排查 |

> 服务端**不校验白名单**：老服务端遇到新插件的新码会原样存下（界面显示不出建议，但原文还在），
> 丢掉分类反而更难查。字段长度上限 64 字符，两端空白会被去掉。

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
| GET | `/server/info` | 版本、启动时间、运行时长、设备统计、数据目录、`secretsEncrypted`（注册码 / 邀请码 / Webhook 密钥的已加密与不可用条数，可用于监控「加密到底生效没有」）；`configReloadedAt` / `configReloadCount` / `configReloadChanges` 反映**配置热重载**（#87）最近一次的情况——改完 `appsettings.json` 刷新这里就能确认生效了没有 |

### 数据迁移（#61）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/migration/export` | `settings.read` | 导出迁移包（JSON）。**凭据不进包**：设备令牌、账号密码哈希、Webhook 密钥列被剥离，`settings` 里的 `enc:` 密文行跳过 |
| POST | `/admin/migration/preview` | `settings.write` | 上传迁移包 → 返回一次性令牌 + 逐表「新增 / 覆盖 / 跳过」预览（不改数据） |
| POST | `/admin/migration/apply` | `settings.write` | 按令牌确认导入：`{"token":"…"}`。**导入前自动备份**（响应里的 `backupId` 可一键回退）；设备与账号**只新增不覆盖**；导入后推进配置版本号 |

### 操作审批（#35）

危险操作（目前是「恢复备份」）由**非超级管理员**发起时不会执行，而是生成一条审批申请；
超级管理员确认后才执行。超管自己发起则直接执行。

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/approvals?status=pending` | 登录即可 | 审批列表：返回 `{ canDecide, items[] }`。**非超管只返回自己发起的**；`canDecide=false` 表示当前账号不能审批 |
| POST | `/admin/approvals/{id}/approve` | 仅超级管理员 | 通过并执行（可选请求体 `{"comment":"…"}`）。执行失败会把状态落成 `failed` 并写清原因 |
| POST | `/admin/approvals/{id}/reject` | 仅超级管理员 | 驳回，不执行（建议带 `comment` 说明原因） |

### 天气（#124）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/weather` | `settings.read` | 天气配置 + 当前数据（`config` / `hasData` / `temperature` / `weatherCode` / `summary` / `error`） |
| PUT | `/admin/weather` | `settings.write` | 保存配置并**立刻抓一次**：`{enabled, city, latitude, longitude, refreshMinutes, temperatureUnit}`。只填城市名会自动反查经纬度；**显式给坐标则不查**（内网离线部署可用） |
| POST | `/admin/weather/refresh` | `settings.write` | 立即抓取一次。抓取失败不报错，原因写在 `error` 里 |
| GET | `/client/weather` | 设备令牌 | 教室端读取。`available=true` 才显示；`summary` 是可直接显示的整行文字（如「杭州 晴间多云 22.5°C」） |

数据源是 Open-Meteo（免 Key）。⚠️ 内网无外网时抓取会失败——服务端**保留上一次成功的数据**，
所以 `available` 可能为 `true` 而 `error` 非空（表示「数据是旧的」）。

### 备份导入（#125）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/backups/import` | `backup.write` | `multipart/form-data`：`file`（`.zip` 或 `.zip.enc`）、`passphrase`（加密包必填，≥6 位）、`note`（可选）。返回新建的备份条目（`type: "imported"`） |

要点：

- **导入 ≠ 恢复**。导入只是把这个包**落成一份备份**（出现在列表里），真正覆盖数据仍要
  `POST /admin/backups/{id}/restore`（那条带操作审批）。这样传错文件不会把整库换掉。
- 加密包（`.zip.enc`）会自动识别并解密；口令错或文件被改过会返回 400
  「口令不对，或文件已损坏」（AES-GCM 认证失败，不是解出乱码）。
- 服务端校验：必须是 zip、**不接受带路径的条目**（防 Zip Slip）、必须含 `controlhub.db`、
  解压后 ≤ 2 GB、上传 ≤ 256 MB；任何一步失败都会清掉半截目录。
- 命令行解密（不给服务端口令的场景）：`IslandHub.Server --decrypt-backup <file> <out.zip>`。

`POST /admin/backups/{id}/restore` 的响应因此变成
`{ executed, approvalId, status, message }`：`executed=false` 表示已提交审批（`approvalId` 为申请号），
`status` 取值 `done` / `failed` / `pending`。同一申请只能被处理一次（并发点「通过」只有一个能进入执行）。
| GET | `/branding/img/{name}` | 上传的品牌图片（Logo / 浏览器图标 / 登录壁纸）。**免鉴权**——登录页在未登录时就要显示它；响应带 `nosniff` 与 `Content-Security-Policy: default-src 'none'; sandbox` |
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
| POST | `/admin/profiles/import-cidata/parse` | `profiles.write` | 上传 ClassIsland「数据迁移」包（`.cidata`，multipart 字段 `file`）→ 返回**一次性令牌** + 预览（能导入什么 / 哪些装不下） |
| POST | `/admin/profiles/import-cidata/apply` | `profiles.write` | 按令牌确认导入：`{"token":"…","profileFile":"演示中学.json","profileId":"…"}`，**合并**进目标档案 |

### 远程管理与诊断

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/devices/{id}/command` | `remote.write` | 单台下发指令：`{"kind":"shell","payload":"{\"command\":\"ipconfig\"}"}` |
| POST | `/admin/devices/command` | `remote.write` | 广播：加 `deviceIds` 与 `includeOffline` |
| GET | `/admin/devices/{id}/commands` | `remote.read` | 指令历史（含状态与输出） |
| GET | `/admin/devices/{id}/commands/queue` | `remote.read` | **待执行队列**（离线排队 / 定时未到 / 已派发在途 + 到期时间） |
| DELETE | `/admin/devices/commands/{id}` | `remote.write` | 取消尚未派发的指令 |
| POST | `/admin/devices/{id}/wake` | `remote.write` | 远程唤醒（Wake-on-LAN）：**直接发魔术包，不进指令队列**——要唤醒的机器是关着的，插件没在跑 |
| POST | `/admin/devices/wake` | `remote.write` | 批量唤醒（`{deviceIds:[]}` 或 `{groupId}`），返回 `{sent, skipped:[{deviceId,deviceName,reason}]}`；已停用的设备会被跳过 |
| POST | `/admin/devices/{id}/notify` | `remote.write` | 发送通知 |
| POST | `/admin/devices/appearance` | `remote.write` | 下发外观 |
| GET | `/admin/devices/{id}/diagnostics` | `remote.read` | 诊断工件列表（截图） |
| GET | `/admin/devices/diagnostics/{id}/content` | `remote.read` | 读取截图内容（图片字节流，**需带令牌**，不能直接给 `<img>` 用） |
| DELETE | `/admin/devices/{id}/diagnostics` | `remote.write` | 清空诊断记录 |

`plugin.install` 的载荷是 `{pluginId, version, cipxUrl, sha256, fileName}`：`cipxUrl` 是 **A 端的相对路径**
（`/client/plugin-packages/{id}/{version}/download`，绝对地址也接受），教室端用自己的服务器地址与设备令牌去取，
取回后核对 `sha256`（不一致回 `COMMAND_INTEGRITY_FAILED`）与包内 `manifest.yml`，再放进宿主的待安装目录。

指令类型（`kind`）：`shell`、`plugin.*`、`appearance.*`、`notify`、`power.*`、`automation.*`、
`volume.set` / `brightness.set`（教室端系统音量 / 屏幕亮度）、
`window.set`（教室端主界面显示 / 隐藏 / 全屏，载荷 `{visible?, fullscreen?}`，两者至少给一个）、
`lesson.swap` / `lesson.adjust` / `lesson.catalog`、
`diagnostic.screenshot` / `diagnostic.processes` / `diagnostic.bundle`、
`diagnostic.ping` / `diagnostic.traceroute`（载荷 `{host, count?}`，host 会被校验字符集）/
`diagnostic.disks`（无载荷）。语义：**一次性派发**、
**2 小时 TTL**、关机 / 重启有 **15 秒撤销窗口**。

### 定时提醒（含定时开关机，#20；事件触发 #12）

提醒的 `triggerKind` 有三种取值：`time`（默认，按 `nextFireAt` 到点触发）、
`device_online`（**设备上线时**触发）、`sync.completed`（**配置同步完成时**触发）与
`automation.fired`（**A 端下发的自动化执行成功**时触发）；
后两者的 `nextFireAt` 恒为 `null`。

- `device_online` 的触发时机是「目标覆盖的教室机**从离线变回在线**」——
  即心跳里的边沿判定（`OnlineTimeoutSeconds` 默认 120 秒之外算离线）。
- `sync.completed` 的触发时机是「目标覆盖的教室机**回报应用成功**」（`POST /client/report`
  且 `result=success`）；`result=partial` **不算**（部分应用意味着有分区没应用上）。
- `automation.fired` 的触发时机是「目标覆盖的教室机**成功回报了一条 `automation.trigger` 指令**」
  （`POST /client/commands/report` 且 `success=true`，服务端按指令 ID 精确取回指令确认类型）；
  宿主自己按规则自动触发的自动化不在此列。
- 同一条提醒有 **5 分钟冷却**，冷却期内重复上线不会再执行；未启用（`enabled=false`）的不触发。
- 事件触发的提醒不参与排程：`PUT/POST /admin/reminders` 时排程字段可留空，
  也不会因为「算不出下次触发时间」而被拒绝。
- 触发记录（`GET /admin/reminders/fires`）的 `detail` 里会写明「触发方式：设备上线 / 按计划」。



| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/reminders` | `reminders.read` | 自己的提醒列表（含 `action` 与下次触发时间） |
| POST | `/admin/reminders` | `reminders.write` | 新建：`{title?, content?, action?, targetType, targetId, repeatKind, repeatInterval, repeatWeekdays[], repeatDayOfMonth, fireTime, startDate, endDate?, speak, enabled}` |
| PUT | `/admin/reminders/{id}` | `reminders.write` | 修改（结构与新建一致） |
| DELETE | `/admin/reminders/{id}` | `reminders.write` | 删除（连触发历史一起） |
| POST | `/admin/reminders/{id}/run` | `reminders.write` | 立即执行一次，**不影响**原有重复计划 |
| GET | `/admin/reminders/summary` | `reminders.read` | 概览：总数 / 启用中 / 近 24 小时触发 / 下次触发 |
| GET | `/admin/reminders/fires?limit=` | `reminders.read` | 触发历史（含 `action` 与逐条说明：推送几台、跳过几台、是否因迟到而跳过） |

`action` 取值：`notify`（默认，推送到教室大屏）、`power.shutdown`（关机，**只发在线设备**）、
`power.wake`（网络唤醒，**不按在线过滤**）。`fireTime` 是**服务器本地时间**的 `HH:mm`；
「仅一次」的触发日期由 `startDate` 决定。通知类必须给标题或内容，关机 / 开机类可以都留空
（服务端会自动填「定时关机 / 定时开机」）。

> ⚠️ **迟到的物理动作不会执行**：调度器对错过的触发是「补发」（停机期间过去的时点，重启后仍会挑出来），
> 但 `power.*` 迟到超过 10 分钟就只推进计划、不执行，并在触发历史里留一条说明——
> 免得停机补发把整栋楼在错误的时间关掉或唤醒。

> ⚠️ `/admin/reminders/{id}/run` 是**领取式**的：抢不到（刚被调度器或另一个实例执行过）返回 **409**，
> 而不是重复执行一次。

### 走班制时段规则（#25）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/profile-slots` | `devices.read` | 带规则的教室 + `periodCount` / `currentPeriodIndex` / 当前档案 |
| GET | `/admin/devices/{id}/profile-slots` | `devices.read` | 单台教室的规则（编辑器用） |
| PUT | `/admin/devices/{id}/profile-slots` | `devices.write` | 整体替换：`{slots:[{periodIndex, profileId}]}`（空数组即清空） |
| POST | `/admin/profile-slots/apply-now` | `devices.write` | 立即按当前时段对齐一次，返回逐台说明 |

- 节次是 **0 基序号**（与课表节点、时间表里第 N 个「上课」时间点一致），不是时间——
  作息表改了规则仍然成立。
- 保存时校验：节次越界（超出这台教室当前作息的上课节数）、同一节重复、档案不存在 → 400。
- 后台**每 30 秒**检查一次，只在「该换」时改档案并下发；**考试模式进行中不动**。
- `apply-now` 的逐台结果取值：`applied` / `unchanged` / `out_of_period`（不在上课时段）/
  `skipped`（例如档案里没有作息时间表、或规则指向的档案已删除）。

### 课表模板套用（#9）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/admin/timetable-templates/{id}/apply` | `profiles.write` | 把模板套到档案：`{profileId}` |

- 按**科目名称**匹配（科目 ID 跨档案不同）；没有同名科目的节次**保留原科目**并在
  `missingSubjects` 里回报。
- 一张课表覆盖多天而模板里这几天不一致 → **拒绝套用**（`conflictedPlans`），提示把课表按天拆开。
- 模板没覆盖到的节次保持原样；节数不一致写进 `periodMismatches`。
- 套用会推进档案版本号（+1），但**不自动下发**。

### 考试模式（#21）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/exam-mode` | `devices.read` | 当前状态：`{active, profileId, profileName, message, startedAt, startedBy, targetCount, targets[]}` |
| POST | `/admin/exam-mode/start` | `devices.write` | 开启：`{profileId, scope, targetIds, message?, notify?}`（`scope` 同推送：`all`/`group`/`device`） |
| POST | `/admin/exam-mode/end` | `devices.write` | 结束并**按各自原值**还原档案 |

- 开启时按范围把设备的 `profile_id` 改成考试档案并**下发一次**；同时记录每台设备**原来的** `profile_id`
  （可能是空 = 跟随分组），结束时按记录写回——**不是**写回某个默认档案。
- 进行中再次开启返回校验错误（否则原档案会被覆盖，导致无法还原）。
- 目标里的每个设备都会排一条通知指令（`notify=false` 可关）。

### 账号与密码策略（#28）

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/password-policy` | 任意登录账号 | 密码要求与有效期：`{minLength, requireMixedChars, expiryDays, expiryWarnDays}` |

- 强度规则来自 `ServerOptions`（`PasswordMinLength` 默认 8、`PasswordRequireMixedChars` 默认 true），
  **建号 / 改密 / 重置 / 自助注册四条入口用同一份**——改配置时四处的校验与界面文案一起变。
- 有效期只提醒、不拦截：登录应答里带 `passwordWarning`；`AccountDto.passwordChangedAt`
  用来在账号列表显示「密码已用 N 天」（超期标黄）。

### 系统设置与品牌

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET/PUT | `/admin/branding` | `settings.read` / `settings.write` | 品牌个性化配置（站点名 / Logo / 登录页 / 主题色 / 玻璃 / 自定义 CSS） |
| POST | `/admin/branding/image` | `settings.write` | 上传品牌图片：`multipart/form-data`、字段名 `file`。**按文件内容判定类型**（PNG / JPEG / WebP / GIF / AVIF，≤ 8 MB，**不收 SVG**），返回 `{url,name,contentType,size}`；保存品牌时自动清理不再被引用的旧图 |
| GET | `/admin/time-offset` | `settings.read` | 时间偏移（全局固定偏移 / 每日漂移 / 各分组校区偏移，写入用同路径 PUT） |

> ⚠️ **`PUT /admin/branding` 是整体替换**：请求体里**没出现的字段会被重置为默认值**。
> 调用方必须「先 GET 读回 → 合并要改的字段 → 整份 PUT」（前端 `saveBranding()` 就是这么做的），
> 否则会把没展示的字段（例如登录页壁纸、主题色）清成默认。

**Webhook 事件名（`events` 字段的取值，9 个）**

`device.offline`（设备掉线）· `device.online`（恢复上线）· `device.error`（配置应用失败 / 部分失败）·
`command.failed`（远程指令失败）· `admin.login.failed`（登录失败 / 账号锁定）·
`reminder.fired`（定时提醒到点执行，失败时同事件但标题带「执行失败」）·
`sync.retry`（配置下发自动重试，第 N/M 次）· `plugin.deployed`（插件已下发）·
`backup.completed`（自动备份完成）。

- 投递体统一是 `{ event, title, message, time, detail }`；`detail` 的键会按
  `WebhookService.DetailLabels` 翻成中文（群消息里显示「设备」「配置版本」等）。
- ⚠️ 服务端保存与回显都按 `WebhookEvents.All` 过滤：**事件名不在这个列表里会被静默丢弃**
  （勾得上也发不出去）。前端的事件清单是硬编码的，新增事件要两边同时加。

### 课表（快捷换课）

直接操作教室端 ClassIsland 的**临时层课表与日期预定**，效果与在教室大屏上手动换课等价。
读用 `devices.read`、写用 `remote.write`。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/admin/lessons/live` | 各设备的课表实时状态（状态 / 当前课表名 / 当前与下节科目 / 下节起止 / **剩余秒数** / 是否已调整） |
| GET | `/admin/lessons/catalog?deviceId=` | 取某台设备上次上报的课表目录（当天各节、可选科目、可选课表） |
| POST | `/admin/lessons/catalog` | 请设备重新上报课表目录：`{"deviceIds":["..."]}` |
| POST | `/admin/lessons/swap` | 临时换课 / 恢复：`{"deviceIds":["..."],"swap":{"date":"2026-10-10","classIndex":1,"subjectId":"…","swap":false,"swapWithIndex":-1,"restore":false}}` |
| POST | `/admin/lessons/adjust` | 调休 / 撤销：`{"deviceIds":["..."],"adjust":{"date":"2026-10-10","classPlanId":"…","restore":false}}` |

> ⚠️ **目标设备必填**（`deviceIds` 或 `groupId`，两者都不给返回 400）：课表指令里的节次与科目是从
> **某一台**设备读来的，早先「没指定」会被当成「全部设备」——等于把某个班的换课发给全校。

### 插件管理（#121）

A 端把 ClassIsland 官方插件市场的索引与插件包**代理缓存到本地**，教室端只从 A 端取包——
教室机因此不需要外网。读用 `plugins.read`、写用 `plugins.write`。

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| GET | `/admin/plugin-market` | `plugins.read` | 市场列表（缓存状态 / 已缓存版本 / SHA256）与市场设置（索引地址、首选镜像、镜像表、上次同步时间与失败原因） |
| POST | `/admin/plugin-market/sync` | `plugins.write` | 立刻同步一次索引。失败**不推翻已有缓存**，原因记在设置里由界面显示 |
| PUT | `/admin/plugin-market/settings` | `plugins.write` | `{indexUrl, preferredMirror}`；字段给 `-` 表示恢复默认 / 改回「自动」 |
| GET | `/admin/plugin-packages` | `plugins.read` | 已缓存的插件包列表 |
| POST | `/admin/plugin-packages` | `plugins.write` | 下载并缓存：`{pluginId, version?}`（省略版本 = 索引里的最新版）；镜像逐个回退，MD5 与包内 manifest 都要对上 |
| DELETE | `/admin/plugin-packages/{id}/{version}` | `plugins.write` | 删除缓存（文件一并删） |
| GET | `/admin/plugin-inventory` | `plugins.read` | 全部设备的已装插件清单（含上报时间与教室端 ClassIsland 版本） |
| POST | `/admin/plugin-inventory/refresh` | `plugins.write` | 让在线设备重新上报清单（批量 `plugin.refresh`） |
| POST | `/admin/plugin-deployments` | `plugins.write` | 批量下发：`{action, pluginId, version?, deviceIds?, includeOffline?, restartAfter?}`，`action ∈ install / uninstall / enable / disable`；返回 `{deploymentId, affected, skipped, skips[]}` |
| GET | `/admin/plugin-deployments` | `plugins.read` | 最近 30 条下发记录（含逐台进度统计） |
| GET | `/admin/plugin-deployments/{id}` | `plugins.read` | 单条下发的逐台明细（状态 / 失败分类 / 教室端原文） |

> ⚠️ **安装的落点是宿主的待安装目录，重启 ClassIsland 才生效**：宿主只在启动时把
> `<缓存目录>/PluginPackages/*.cipx` 解压到插件目录，没有「安装这个文件」的公开接口
> （`ProcessPluginsInstall` 在宿主主程序集、非 public 非 async）。因此教室端回报的成功文案里
> 必定带「重启后生效」，`restartAfter` 只是顺带再下发一条 `restart`。

> ⚠️ **版本与平台在 A 端预校验**：插件声明的 `apiVersion` 高于教室端 ClassIsland 版本、插件不支持
> Windows、已装着同一版本——这三种被**跳过**并在 `skips[].reason` 写明原因。卸载 / 启停刻意不做
> 「是否已安装」判断：清单是上一次上报的快照，拿它拦人会把「刚装好还没上报」的设备误拦。

**插件图标（免鉴权）**

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/api/v1/plugin-icons/{pluginId}` | **免鉴权** | 返回该插件已缓存的图标（`image/png` 等），`Cache-Control: public, max-age=604800`；没缓存则 404 |

- 为什么要免鉴权：`<img src>` **带不了 bearer 令牌**；图标本身是公开的官方索引内容
  （插件 ID 也公开），免鉴权才能让浏览器按 URL 缓存它们。
- 市场列表里每个条目的 `iconLocalUrl` 就是它；为空表示图标还没缓存到本地
  （界面显示首字母头像）。**不要**用 `iconUrl`（那是上游地址，指向 GitHub）。

**插件自述文件（免鉴权，按需取）**

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/api/v1/plugin-readmes/{pluginId}` | **免鉴权** | 返回自述文件 Markdown 原文（`text/markdown; charset=utf-8`，`Cache-Control` 7 天）。**首次访问会触发 A 端去取**（可能几秒），之后走本地缓存；取不到返回 404 |

- 与图标同一套理由：免鉴权是为了让前端能直接 `fetch`，内容本身是公开的索引数据。
- **只取索引里登记的 `readme_path`**（不接受任何调用方给的 URL），否则这就是个开放抓取代理。
- 市场列表里的 `readmeLocalUrl` 就是它；**空**表示索引里这个插件没有自述文件。
  取不到时前端会回落到 `repositoryUrl`（「去仓库看 →」）。

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
| POST | `/client/lessons/catalog` | `HubDevice` | 上报课表目录（当天各节、可选科目与课表），供「快捷换课」页填充下拉框 |
| GET | `/client/plugin-packages/{id}/{version}/download` | `HubDevice` | 从 A 端的代理缓存下载插件包（地址由 `plugin.install` 指令下发；**必须带设备令牌**） |
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

## 六、机器可读的规范（OpenAPI）

路线图 **#95**：服务端在**运行时端点表**上生成 OpenAPI 3.1 规范——所以不会出现「加了接口忘了写文档」，
新路由自动出现在里面（当前 199 条操作 / 163 条路径，含全部管理端、教室端与公开接口）。

```bash
# 1) 登录拿会话令牌（账号需要 settings.read 权限）
TOKEN=$(curl -s -X POST "http://<服务器>:29800/api/v1/admin/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"…"}' | jq -r .data.token)

# 2) 取规范
curl -s "http://<服务器>:29800/api/v1/admin/openapi/v1.json" \
  -H "Authorization: Bearer $TOKEN" -o islandhub-openapi.json
```

`islandhub-openapi.json` 可以直接导入 Apifox / Postman / Swagger Editor，或用 `openapi-generator` 生成客户端。

规范里额外写了四样标准字段之外、但对接时最要紧的东西：

| 位置 | 含义 |
|---|---|
| `components.securitySchemes` | 四种鉴权方式：`AdminBearer`（管理端会话）、`DeviceToken`（教室端 `HubDevice`）、`LocalShell`（桌面外壳本机信任）、`MetricsToken`（`/metrics` 抓取） |
| 每个操作的 `security` | 这条接口**谁能调**；公开接口是空数组 |
| 每个操作的 `x-public` | 明确标成「无需鉴权」（登录、存活探测、注册码换取设备令牌等） |
| 管理端操作的 `x-permission` | 需要的权限键，与「账号与权限」页显示的键一致——据此申请**最小权限** |

⚠️ 两条注意：

- 规范挂在**管理端分组**下：需要登录，且账号要有 `settings.read`。它等价于「全部接口 + 权限清单」，
  刻意不对整个内网公开（没权限就是 403）。想给某个只读观察号看，给它 `settings.read` 即可。
- 整站不想提供这份清单时，把 `ControlHub:EnableOpenApi` 设为 `false`（改动需重启）。
