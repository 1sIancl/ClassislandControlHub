# 示例配置

> 独立成篇（路线图 #101）。三部分：一份**可直接抄的 `appsettings.json`**、**示例档案**怎么用、
> 以及**设备导入模板**。目标是让第一次部署的人「照着填就能跑起来」。

## 一、`appsettings.json` 完整示例

配置节名是 **`ControlHub`**（`appsettings.json` 的键、`ControlHub__` 环境变量、`--ControlHub:` 命令行三者是同一回事）。
完整配置项清单见 [`deployment.md`](deployment.md)；下面这份是**中学机房的常见取值**，注释写了每项的取舍。

```json
{
  "ControlHub": {
    "ServerName": "实验楼 3 楼机房",
    "BindAddress": "0.0.0.0",
    "HttpPort": 29800,
    "DataDirectory": "D:\\IslandHub\\data",
    "EnableDiscovery": true,
    "DiscoveryPort": 29810,
    "EnableNtpServer": false,
    "RequireEnrollCode": true,
    "SessionLifetimeHours": 12,
    "OnlineTimeoutSeconds": 90,
    "AutoCheckUpdates": false,
    "EnableOpenApi": true,
    "BackupRetention": 10,
    "LogJson": false
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

逐项说明（JSON 不支持注释，所以放这里）：

| 配置项 | 这份取值意味着什么 |
|---|---|
| `ServerName` | 显示在界面顶栏与大屏看板上，建议写「楼栋 + 楼层」，多机房时一眼分清。 |
| `BindAddress` / `HttpPort` | 监听地址与端口。只给本机用就写 `127.0.0.1`；**改这两项需要重启**。 |
| `DataDirectory` | `controlhub.db` 与 `secrets.key` 的位置。**绝对路径最稳妥**（相对路径按程序目录解析，升级时容易被误解）。 |
| `EnableDiscovery` / `DiscoveryPort` | 教室端靠 UDP `29810` 自动发现服务器。跨网段（不同 VLAN）时发现不可用，改用「手动填地址」。 |
| `EnableNtpServer` | 教室大屏要从 A 端对时间时才开（默认关：Windows 的 123 端口通常被系统时间服务占着）。 |
| `RequireEnrollCode` | `true` = 教室端接入必须填注册码。**推荐保持 true**，否则同网段任何机器都能接进来。 |
| `SessionLifetimeHours` | 登录会话有效期。公用电脑上可以调小（如 4）。 |
| `OnlineTimeoutSeconds` | 超过这个秒数没心跳算离线。大屏看板上的「离线」用的就是它。 |
| `AutoCheckUpdates` | 能上外网就开着；纯内网服务器建议关掉，改手动替换（见 [`upgrade.md`](upgrade.md)）。 |
| `EnableOpenApi` | 机器可读接口规范开关。默认开，只对已登录且有 `settings.read` 的账号开放。 |
| `BackupRetention` | 自动备份保留份数。 |
| `LogJson` | `true` = JSON 行日志（便于采集）；`false` = 人类可读文本。**改动需重启**。 |

⚠️ **密钥类项不要写在这里明文发给别人**：`MetricsToken`（`/metrics` 抓取令牌）、
`SecretsKeyPath`（自定义密钥位置）。它们更适合用**环境变量**给（`ControlHub__MetricsToken=…`），
免得配置文件被截图 / 打包时一起泄露。

### 常用环境变量速查

| 变量 | 作用 |
|---|---|
| `ControlHub__HttpPort` | 覆盖监听端口（容器里最常用） |
| `ControlHub__BindAddress` | 覆盖监听地址 |
| `ControlHub__DataDirectory` | 覆盖数据目录 |
| `ControlHub__AdminIpAllowList` | 管理端来源白名单（逗号 / 分号分隔，支持 CIDR） |
| `ControlHub__MetricsToken` | `/metrics` 抓取令牌 |

## 二、示例档案

「配置档案 → **+ 新建示例档案**」会一键生成一套**能直接下发的标准配置**：

- **时间表**：早读 / 上午四节 / 午休 / 下午四节 / 晚自习（含课间）；
- **课表**：周一至周五各一张，科目取常用组合；
- **科目**：语文 / 数学 / 英语等，带首字母；
- **自定义设置**：一条公告 + 一条键值对，演示「自定义配置项」怎么下发。

**建议用法**：先建示例档案 → 点进去改成自己学校的作息与课表 → 保存并下发。
不要直接在示例档案上小改小补——它的价值是「结构齐全、照着改不会漏字段」。
「新建空白档案」适合从 `.cidata` / CSES 导入现成课表的场景。

## 三、设备导入模板

「设备管理 → 导入」用的 CSV 表头（导入页可下载带示例的模板）：

```csv
name,machineName,group,remark
高一(1)班,PC-A101,教学楼 A,1 楼东
高一(2)班,PC-A102,教学楼 A,1 楼西
高二(1)班,PC-B201,教学楼 B,2 楼东
```

- `name` 必填（教室名，会显示在设备列表与大屏看板上）；
- `machineName` 是**教室那台机器的机器名**，教室端重新注册时按它认领分组与档案——**建议与教室机实际机器名一致**；
- `group` 写分组名，导入时不存在会自动创建；
- `remark` 可选。

## 四、照着做的第一次部署

1. 按第一节写 `appsettings.json`，启动服务（`IslandHub.Server`）。
2. 浏览器打开 `http://<服务器>:29800`，用初始管理员账号登录（**首次登录会要求改密码**）。
3. 「配置档案 → + 新建示例档案」，改成自己学校的作息与课表。
4. 「设备管理 → 注册码」生成一个设备注册码（别用错成账号邀请码）。
5. 教室端插件里填服务器地址 + 注册码，保存并立即同步。
6. 回到「设备管理」确认设备在线、已同步；「配置下发」推一次全量。
7. 把「系统 → 大屏看板」加到值班室那台机器的收藏夹——以后每天看它就够了。
