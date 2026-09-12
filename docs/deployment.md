# 部署与使用指南

## 1. 构建环境

- .NET SDK **10.0**（本项目在 `10.0.401` 上验证通过）。
- 无需其它依赖；A 端 Web 界面为原生前端，无构建步骤。

```bash
# 编译整个解决方案
dotnet build ClassIsland.ControlHub.slnx -c Release

# 打包 B 端插件（产出 src/ControlHub.Plugin/cipx/ControlHub.Plugin.cipx）
dotnet build src/ControlHub.Plugin/ControlHub.Plugin.csproj -c Release -p:CreateCipx=true -p:GenerateHashSummary=false
```

> `GenerateHashSummary=false` 会跳过 MD5 摘要生成（该步骤依赖 `pwsh`）。
> 若要生成含摘要的正式包，请安装 PowerShell 7 后去掉该参数。

## 2. A 端部署

### 方式一：内网本地运行（推荐日常使用）

```bash
cd src/ControlHub.Server
dotnet run -c Release
```

- 默认监听 `0.0.0.0:29800`，同一局域网内用 `http://本机IP:29800` 访问。
- 局域网自动发现使用 UDP `29810`，请确保防火墙放行这两个端口。

### 方式二：发布为自包含目录

```bash
dotnet publish src/ControlHub.Server/ControlHub.Server.csproj -c Release -o dist/server
cd dist/server
./ControlHub.Server
```

发布目录即一个完整的服务器应用，可拷贝到任意 Windows/Linux/macOS 机器运行。

### 方式三：部署到公网/服务器

- 上传发布目录到服务器，运行即可。
- 建议在前面加 Nginx 反向代理提供 HTTPS，并反向代理 `/` 到本机 `29800`。
- 在 `appsettings.json` 中设置 `ControlHub:PublicBaseUrl`（例如 `https://hub.example.edu`），
  这样自动发现与客户端提示中显示的公网地址才正确。
- 长轮询是普通 HTTP/1.1 请求，Nginx 无需特殊配置，但请把 `proxy_read_timeout` 调到 60s 以上。

### 配置项（`appsettings.json` 的 `ControlHub` 节）

| 键 | 默认 | 说明 |
|---|---|---|
| `ServerName` | ClassIsland 集控服务器 | 展示名称 |
| `HttpPort` | 29800 | HTTP 端口 |
| `BindAddress` | 0.0.0.0 | 监听地址 |
| `DataDirectory` | data | 数据目录（SQLite 文件所在） |
| `RequireEnrollCode` | true | 是否强制注册码 |
| `EnableDiscovery` | true | 是否开启 UDP 自动发现 |
| `DefaultAdminUser` / `DefaultAdminPassword` | admin / admin123 | 首次启动创建的管理员 |
| `EnableHttps` / `HttpsPort` | false / 29801 | 是否启用 HTTPS |

### 备份

所有数据都在数据目录的 `controlhub.db`（SQLite）。备份即复制该文件；建议在低峰期执行。

## 3. B 端部署

1. 构建出 `ControlHub.Plugin.cipx`（见上文）。
2. 在教室电脑上安装 ClassIsland（2.1+）。
3. 把 `.cipx` 放入 ClassIsland 的插件目录（或通过插件市场分发安装）。
4. 在 A 端 Web 界面生成注册码。
5. 打开 ClassIsland【应用设置 → 集控客户端】：
   - 点「自动发现服务器」，或手动填写服务器地址；
   - 填入注册码，点「保存设置」，再点「立即同步」。

## 4. 使用流程建议

1. 首次部署：登录 A 端 → 「配置档案 → 新建示例档案」→ 按学校作息/课表调整 → 保存。
2. 建分组（如「高一年级」）并绑定默认档案。
3. 生成注册码，到各教室安装插件并填入注册码。
4. 在「配置下发」查看同步进度，对未同步设备可定向推送。
5. 日常调整课表/作息后保存即可，客户端会自动同步；也可手动「立即推送」。
6. 定期在「审计日志」查看操作记录，在「设备管理 → 日志」排查异常终端。

## 5. 常见问题

- **设备列表看不到新设备**：确认防火墙放行 29800/29810；确认注册码未过期/用尽。
- **设备在线但「待同步」**：客户端可能被手动关闭了自动同步；到设备日志查看。
- **Web 界面打不开**：确认服务器进程在运行、端口未被占用；本地访问用 `http://127.0.0.1:29800`。
- **插件提示「无法连接服务器」**：检查地址是否可 ping 通、是否 http/https 写错。
