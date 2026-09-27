# 使用说明（ControlHub 集控系统）

一套面向 **ClassIsland 2.1+** 的教室大屏集中管理系统，分 A、B 两端：

- **A 端 · 集控服务器**：负责课表/时间表/科目的统一下发，自带网页管理后台。
- **B 端 · 客户端插件**：装在教室电脑的 ClassIsland 里，自动接收并应用配置。

> 建议先在一台机器上把 A 端跑起来，再逐步接入教室终端。

---

## 一、A 端部署（服务器）

### 方式 1：Linux 一键部署（推荐）

在目标 Linux 服务器（Ubuntu / Debian / CentOS 等，需 root）上执行：

```bash
curl -fsSL https://cdn.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh -o /tmp/controlhub-install.sh && sudo bash /tmp/controlhub-install.sh
```

脚本会自动完成：安装 .NET 10 SDK → 拉取源码 → 编译发布 → 注册 systemd 服务并启动。
完成后访问 `http://服务器IP:29800` 即可。

> **国内服务器提示**：下载脚本走 jsDelivr CDN（国内有节点），比 raw.githubusercontent.com 快且稳；
> 若仍失败，换 `https://fastly.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh`，
> 或对拉源码步骤显式指定镜像 `GIT_MIRROR=https://kkgithub.com sudo bash /tmp/controlhub-install.sh`。

| 操作 | 命令 |
|---|---|
| 查看状态 | `systemctl status classisland-controlhub` |
| 查看日志 | `journalctl -u classisland-controlhub -f` |
| 重启服务 | `systemctl restart classisland-controlhub` |
| 升级到最新 | 重新执行上面的一键命令 |

数据保存在 `/opt/classisland-controlhub/server/data/controlhub.db`，备份即复制该文件。

### 方式 2：Windows / 本地运行

前置：安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/1sIancl/ClassislandControlHub.git
cd ClassIsland.ControlHub
dotnet run --project src/ControlHub.Server -c Release
```

启动后浏览器访问 `http://localhost:29800`。局域网内其它机器用 `http://本机IP:29800` 访问。

**方式 2 · 附：注册为 Windows 服务（推荐长期运行）**

在仓库目录用**管理员身份**运行 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\sh\install-windows.ps1
```

脚本会自动发布服务端并注册 Windows 服务（开机自启、崩溃自动重启），并放行 HTTP 与自动发现端口。
数据默认保存在 `C:\ProgramData\ClassislandControlHub\data`，升级重新执行该命令即可。

| 操作 | 命令 |
|---|---|
| 查看状态 | `Get-Service ClassislandControlHub` |
| 重启服务 | `Restart-Service ClassislandControlHub` |
| 停止服务 | `Stop-Service ClassislandControlHub` |
| 卸载 | `powershell -ExecutionPolicy Bypass -File .\sh\uninstall-windows.ps1` |

### 方式 3：部署到公网（可选）

1. 用方式 1 或 2 部署后，用 Nginx 反向代理 `https://你的域名` → `127.0.0.1:29800`。
2. 修改 `appsettings.json`（或在 systemd 里加环境变量）设置 `ControlHub:PublicBaseUrl` 为你的公网地址。
3. 防火墙放行 `29800`（HTTP）与 `29810`（UDP，局域网自动发现）。

---

## 二、首次登录与初始化

1. 打开管理后台，用默认账号登录：
   - 用户名 `admin`　密码 `admin123`
2. **立即修改密码**：右上角头像 → 「修改密码」（左侧「系统设置」里也有）。
3. 建一套配置：左侧「配置档案 → 新建示例档案」，会自动生成标准作息 + 周一至周五课表 + 常用科目。
4. 按学校实际情况编辑：时间表（作息）、课表（排课）、科目，保存即可。
5. 到「设备管理」按「**楼栋 → 楼层 → 教室**」整理教室大屏：先「+ 新建楼栋」，再在楼栋里「+ 添加楼层」，
   最后把教室设备拖进对应楼层即可。楼栋与楼层都带标识色，顶部概览直接显示栋数 / 层数 / 教室数与在线率。
   点某台教室设备可以单独指定档案，或查看日志、停用、删除。

### 用现成课表快速建档案

**方式一：AI 导入（推荐，支持任意格式）**

手上已经有课表（Excel、教务系统网页、纸质课表的文字版）时，不必手工一格一格排：

1. 先到「系统设置 → AI 辅助导入课表」填入接口地址、模型与密钥（任何兼容 OpenAI Chat Completions 的服务都可以），
   点「**测试连接**」确认可用。
2. 到「配置档案 → **AI 导入课表**」，把课表原文粘贴进去（也可选择文件），点「**AI 解析**」。
3. 预览解析结果（作息节数、每天课表、科目），确认无误后点「导入到档案」。

解析结果会自动整理成一张作息时间表、每天的课表与用到的科目。同名科目与相同作息的时间表会自动复用，
同一天的课表会被本次结果覆盖；反复导入不会产生重复数据。

> 课表原文会发送到你配置的接口地址。若其中含教师姓名等信息，请使用可信服务或本地部署的模型。

**方式二：从 ClassIsland 导出的 CSES 导入**

ClassIsland 的档案编辑器支持导出 CSES（`.yml`）。在「配置档案 → 从 CSES 导入」中粘贴或选择该文件，
会并入其中的**科目、作息时间表与逐格课表**：按 `enable_day` 生成每天的课表，
各天作息一致的会自动合并成一张时间表。集控的课表模型不区分周次，单双周课表会合并处理。

---

## 三、B 端接入教室终端

1. 构建插件包（或在 Releases 下载现成的 `.cipx`）：

   ```bash
   dotnet build src/ControlHub.Plugin/ControlHub.Plugin.csproj -c Release -p:CreateCipx=true
   # 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx（同目录 checksums.md 为自动生成的 MD5 摘要）
   ```

2. 在教室电脑安装 ClassIsland（2.1+），把 `.cipx` 放入插件目录（或走插件市场分发）。
3. 回到管理后台「设备管理 → 生成注册码」，复制注册码。
4. 打开 ClassIsland【应用设置 → 集控客户端】：
   - 点「自动发现服务器」（同一局域网），或手动填写服务器地址（如 `http://192.168.1.5:29800`）；
   - 填入注册码 → 「保存设置」→「立即同步」。
5. 设备即出现在「设备管理」列表中，配置下发数秒内自动生效。

---

## 四、日常使用

| 场景 | 操作 |
|---|---|
| 调整作息 / 课表 / 科目 | 「配置档案」里编辑对应档案，保存后自动同步到绑定设备 |
| 立即让设备生效 | 「配置下发」→ 选范围 →「立即推送」 |
| 查谁还没更新 | 「配置下发」顶部进度条 / 设备列表的「待同步」标记 |
| 排查某台设备 | 「设备管理 → 该设备 → 日志」 |
| 追溯谁改了什么 | 「审计日志」 |

### 配置下发优先级

```
设备单独指定档案  →  所属楼层默认档案  →  所属楼栋默认档案  →  全局默认档案
```

也就是说：给楼栋指定档案，整栋教室都会跟着走；某一层要单独用别的档案（例如机房层）就在楼层上指定；
个别教室要临时调整，直接在「设备管理」里给那台设备单独指定，优先级最高。

---

## 五、多账号、权限与定时提醒

### 多账号与权限

- 首次部署只有一个超级管理员（`admin`）。超级管理员拥有全部权限，且不可被裁剪。
- 在「系统设置 → 账号与权限」点「+ 新建账号」：用户名创建后不可改；初始密码留空则由系统生成，并**只显示一次**。
- 权限按模块勾选「查看 / 修改」。勾上「修改」会自动带上对应的「查看」，不会出现「能改不能看」。
- 两条保护规则：**不能授予自己没有的权限**；**系统至少保留一个超级管理员**（也不能删除当前登录的账号）。
- 权限变更后该账号的登录会话会立即失效，需要重新登录才生效。
- **数据隔离范围**：账号、个人偏好与个人提醒按用户隔离；设备、楼栋楼层、配置档案、注册码等是**全校共享**数据，靠权限控制谁能改。

### 邀请码自助注册（默认关闭）

1. 「系统设置 → 注册邀请码」打开「允许凭邀请码自助注册」。
2. 点「+ 生成邀请码」，勾选注册后获得的权限（只能勾你自己拥有的），设置可用次数与有效期。
3. 把邀请码发给对方，对方在登录页点「使用邀请码注册」填入即可注册并自动登录。

> ⚠️ A 端是校内控制台。开启自助注册意味着**任何拿到地址的人，只要再有邀请码就能进入管理界面**。
> 建议只临时开启、用一次性邀请码，用完立即关掉开关。

### 定时提醒

- 「定时提醒」页可创建「仅一次 / 每天 / 每周 / 每月」的提醒，设定「日期 + 时间」与推送目标（单间教室 / 楼栋 / 楼层 / 全部在线教室），可选语音播报。
- 只推给**在线**教室——提醒讲时效，离线教室不做补发；离线台数会记在触发历史里。
- 触发时间按服务器**校正后**的时钟计算（含 NTP 授时与手动偏移），误差通常在 15 秒内。
- 「触发历史」标签页记录每次触发的时间、推送台数与目标，可用来核对是否准时。
- 提醒按账号隔离：每个账号只能看到并管理自己创建的提醒，彼此互不干扰。
- 想先验证配置是否正确，可以用列表里的「立即触发」推一次，它不会改变原有重复计划。

### 新手引导

新账号首次登录会自动播放分步引导，右上角随时可以点「跳过」；跳过后不再自动出现。
想再看一遍：「系统设置 → 账号安全 → 重新观看引导」。

## 六、常见问题

| 问题 | 处理 |
|---|---|
| 设备列表看不到新设备 | 检查防火墙是否放行 29800/29810；确认注册码未过期/用尽 |
| 设备在线但一直「待同步」 | 到该设备「日志」查看；可能客户端关闭了自动同步 |
| Web 后台打不开 | 确认服务在运行、端口未被占用；本机用 `127.0.0.1:29800` 试 |
| 插件连不上服务器 | 检查地址是否可 ping 通、http/https 是否写错 |
| 忘记管理员密码 | 服务端 `data/controlhub.db` 里 `users` 表可重置（或重新初始化数据目录） |

---

## 六、更多

- 完整协议与数据格式：见 [`docs/protocol.md`](docs/protocol.md)
- 架构与数据模型：见 [`docs/architecture.md`](docs/architecture.md)
- 开发/交接说明：见 [`HANDOFF.md`](HANDOFF.md)
