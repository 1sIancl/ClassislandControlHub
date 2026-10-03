# 交接：工作准则与当前任务（给下一段对话的 AI / 接手的人）

> repo 版；另一份放在 AI 侧记忆目录（下个对话自动加载）。生成于 2026-10-03。

## 一、项目边界（别搞错）

| 部分 | 目录 | 平台 |
|---|---|---|
| A 端服务端 + Web 管理界面 | `src/ControlHub.Server` | 跨平台（Windows / Linux） |
| B 端插件（教室端） | `src/ControlHub.Plugin` | **仅 Windows** 上的 ClassIsland |
| 桌面外壳 | `src/ControlHub.Desktop` | 仅 Windows |

远端：`github.com/1sIancl/ClassislandControlHub`，分支 `main`。

## 二、硬性工作准则（每条都是踩过的坑）

1. **改完必须实测**：`dotnet build -c Release`（0 警告 0 错误）→ 前端 `.js` 复制成 `.mjs` 跑 `node --check` → HTTP 端到端。
   **语法检查查不出「用了没 import」**：`settings.js` 漏导入 `select`，导致「新建 Webhook 按钮点不开」藏了很久。
   界面改动**尽量用浏览器点一遍**（语法检查与构建都发现不了浏览器级问题）。
2. **数据路径改动必须整段完成**：加密注册码时「写入」与「查找」必须同一批改完——只改一半会让注册码**静默失效**。
3. **不要用短片段替换改易碎位置**：`settings.js` 的 `h('button…` 被局部替换改坏过两次（留下 `h('b` 残片）。
   这类地方用**整段/函数级替换**，或加包装函数（`guardUi` 就是这么加的）。
4. **提交前必过校验；推送后必核对远端**：`git fetch` 引用会陈旧，出现 `Everything up-to-date` 但远端其实是旧的
   → 用 `git rev-parse origin/main` 与本地对比。
5. **插件里碰宿主新增设置项一律走反射**：CI 用 ClassIsland **发行包**作参考程序集（比本机源码 `ClassIsland2_src` 少成员），
   直写属性会 CS1061 让「构建插件并打包 .cipx」失败（实测踩过三次）。已有 `TrySetSetting` 可复用。
6. **绝不直接写宿主 `Settings.json`**：宿主也在写同一文件，撞车会让宿主崩溃。
   改宿主设置走 `IAppHost.TryGetService<SettingsService>()` → 改 `Settings.*` → `SaveSettings(...)`。
7. **失败必须如实回报、不静默降级**：解密失败按「该值不可用」处理并计数，**绝不退回明文**；
   界面异常要弹原因（`guardUi`），不能「点了没反应」。
8. **中文注释与提交信息**，写清「为什么」；提交格式 `feat|fix|docs(范围): 一句话`，正文列实测证据。
9. **测试环境**：本地 A 端 `E:\classisland集控\本地A端`（`127.0.0.1:29800`，admin/admin123）；
   公网 A 端 `120.53.233.199:29800`；B 端宿主 `E:\classisland集控\ClassIsland2_publish`（**覆盖插件前先退出宿主**）。
   测完清理测试数据、把临时实例恢复默认配置。
10. **发版**：版本号三处（`HubProtocol.ProductVersion`、插件 `.csproj`、`manifest.yml`）→ CHANGELOG 定版 →
    README「最近更新/版本表」→ commit → `git tag -a 1.x.y.0` → push 标签触发流水线
    （产出插件 `cipx` + Win/Linux A 端**普通版与 `-full` 自包含版** + 桌面外壳）。

## 三、当前状态（2026-10-03）

- **`1.3.4.0` 已发布**：Webhook 两轮增强（投递明细 / 静默时段 / 去重 / @所有人 / 自定义头 / 独立超时重试 / 失败原因可读化）、
  外观下发修复并扩到 11 项、推送世代号修复、安全组（登录锁定、访问日志与追踪 ID、安全响应头、HTTPS 跳转、
  来源允许列表绑定修复）、设备 CSV 与预注册认领、`#39` 渗透测试清单。
- **已推送但未发版**（将进 `1.3.5`）：`#28` 密码到期提醒（接口 + 界面）、
  `#36` 加密组件 `SecretProtector` 与配置/DI 接入、ADR 0005 方案与实现清单。

## 四、待办（按优先级）

1. **`#36` 执行单元**（设计已锁定）：照 `docs/adr/0005-encrypt-secrets-at-rest.md` 的「实现清单」逐条实施——
   注册码加 `code_hash`（HMAC；因为 `code` 是主键而 AES-GCM 随机化，**不能**按明文查）→ 创建/查找改走 hash →
   Webhook 密钥读写加解密 → 启动幂等迁移（**必须先补 `code_hash` 再加密 `code`**）→ `server/info` 计数与启动日志 →
   三处文档 → 端到端验收（含**删掉 `secrets.key`** 的故障态：注册码失效 + 密钥不可用，但服务照常启动）。
2. **体验组 `#40-#58`**：仪表盘定制、全局搜索、配置冲突检测、自定义列、快捷键等。
3. **报表组 `#59-#68`**：在线率 / 同步率 / 指令 / 到达率报表、热点分析、备份可选内容与加密、VACUUM。
4. **工程化组 `#69-#89` 剩余**：`/metrics`、错误码体系、单元/集成测试补齐、健康分级、迁移与回滚。
5. **`#7` 通知回执 / `#22` 紧急通知需确认**：需 B 端插件协同，随 `1.3.5` 一起发。

## 五、关键文档

- `docs/adr/0005-encrypt-secrets-at-rest.md` —— `#36` 方案 + 实现清单（含文件/行号/SQL）+ 端到端验收脚本
- `docs/roadmap.md` —— 120 项状态；`CHANGELOG.md` —— 已定版到 `1.3.4.0`
- `docs/pentest-checklist.md`、`docs/security.md`、`docs/troubleshooting.md`
- `USAGE.md` —— 界面用法（含 Webhook「三步引导」）
