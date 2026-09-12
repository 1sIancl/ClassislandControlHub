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
curl -fsSL https://cdn.jsdelivr.net/gh/1sIancl/IslandManger@main/sh/main.sh -o /tmp/controlhub-install.sh && sudo bash /tmp/controlhub-install.sh
```

脚本会自动完成：安装 .NET 10 SDK → 拉取源码 → 编译发布 → 注册 systemd 服务并启动。
完成后访问 `http://服务器IP:29800` 即可。

> **国内服务器提示**：下载脚本走 jsDelivr CDN（国内有节点），比 raw.githubusercontent.com 快且稳；
> 若仍失败，换 `https://fastly.jsdelivr.net/gh/1sIancl/IslandManger@main/sh/main.sh`，
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
git clone https://github.com/1sIancl/IslandManger.git
cd ClassIsland.ControlHub
dotnet run --project src/ControlHub.Server -c Release
```

启动后浏览器访问 `http://localhost:29800`。局域网内其它机器用 `http://本机IP:29800` 访问。

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
5. 建分组（如「高一年级」）并绑定该档案，便于批量管理。

---

## 三、B 端接入教室终端

1. 构建插件包（或在 Releases 下载现成的 `.cipx`）：

   ```bash
   dotnet build src/ControlHub.Plugin/ControlHub.Plugin.csproj -c Release -p:CreateCipx=true
   # 产物：src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx
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
设备单独指定档案  →  设备所属分组默认档案  →  全局默认档案
```

也就是说：可以在「设备管理」里给单台设备单独指定档案，覆盖其分组默认值，适合个别班级的临时调整。

---

## 五、常见问题

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
