# 宣传素材

- 帖子正文：`forum-post.md`（论坛版）、`zhijiao-alliance-post.md`（智教联盟版）
- 粘贴用副本：同名 `.txt`（纯文本）与 `.html`（带样式），改完 md 后跑 `node _shot/_promo-export.js` 重新生成
- 配图放 `images/`

## 一、演示环境

本地 A 端 `http://127.0.0.1:29800`，`admin` / `admin123`，已准备：

| 项 | 内容 |
|---|---|
| 设备 | 14 台模拟终端（`ControlHub.LoadTest`，真实注册 + 心跳） |
| 命名 | 「高一(1)班 … 高三(5)班」，带备注 |
| 分组 | 教学楼A（1、2 层）、教学楼B（1 层），各层绑定档案 |
| 档案 | 示例档案：标准作息 + 周一至周五课表 + 常用科目 |
| 注册码 | 备注 `promo-shots*`，不限次数、4 小时有效 |

模拟端跑满时长后会转离线，重新拉起：

```powershell
dotnet exec src\ControlHub.LoadTest\bin\Release\net10.0\ControlHub.LoadTest.dll `
  --server http://127.0.0.1:29800 --code <注册码> --devices 14 --duration 3600
powershell -ExecutionPolicy Bypass -File docs\promo\tools\setup-demo-env.ps1
```

## 二、配图清单

| 文件名 | 页面 | 画面要点 |
|---|---|---|
| `01-login.png` | `/`（未登录） | 左栏介绍 + 右栏登录卡片 |
| `02-dashboard.png` | `#/dashboard` | 设备总数 / 在线率 / 待同步 / 最近事件 |
| `03-devices.png` | `#/devices` | 楼栋 → 楼层 → 教室三级看板 |
| `04-profiles.png` | `#/profiles` | 周课表网格、时间表、科目 |
| `05-remote.png` | `#/remote` | 指令面板 + 目标选择 + 诊断截图区 |
| `06-audit.png` | `#/audit` | 筛选条件与列表 |
| `07-settings.png` | `#/accounts` | 账号、角色模板、权限矩阵（现有文件是旧「系统设置」页截图，建议重拍） |
| `08-shell.png` | 桌面外壳 | 状态胶囊、按钮分组、日志面板 |

> `08-shell.png` 是旧版外壳截图，建议换新。

## 三、怎么截

1. Edge / Chrome 打开 `http://127.0.0.1:29800` 并登录；
2. 窗口调到约 1600×1000（想更紧凑可缩放到 90%）；
3. `Win + Shift + S` 或 Edge 的网页捕获，截浏览器内容区；
4. 存到 `images/`，**文件名与上表一致**；
5. 登录页要先退出登录再截。

## 四、发布

- 标题：**IslandHub：把几十间教室的 ClassIsland 收进一个网页**
- 正文用 `forum-post.md`；贴到不支持 Markdown 的地方用对应的 `.txt`
- 文中的括号占位（如「（图 1：…）」）换成实际图片
