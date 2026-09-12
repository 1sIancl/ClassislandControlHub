#!/usr/bin/env bash
#
# ClassIsland.ControlHub · A 端服务器 Linux 一键部署脚本
#
# 用法（在目标 Linux 服务器上，用 root 执行）：
#   sudo bash <(curl -sL https://raw.githubusercontent.com/1sIancl/ClassIsland.ControlHub/main/sh/main.sh)
#
# 说明：脚本会依次完成——安装 .NET 10 SDK（如缺失）→ 拉取源码 → 编译发布 →
#       注册并启动 systemd 服务。数据保存在 /opt/classisland-controlhub/server/data。
#
set -euo pipefail

REPO_OWNER="1sIancl"
REPO_NAME="ClassIsland.ControlHub"
INSTALL_DIR="/opt/classisland-controlhub"
SERVICE_NAME="classisland-controlhub"
HTTP_PORT="${HTTP_PORT:-29800}"
DOTNET_DIR="$HOME/.dotnet"
DOTNET_CHANNEL="10.0"
DOTNET_BIN=""

info() { printf '\033[36m[集控]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[警告]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[错误]\033[0m %s\n' "$*" >&2; exit 1; }

# ── 环境检查 ──────────────────────────────────────────────
[ "$(id -u)" -eq 0 ] || die "请以 root 运行：sudo bash <(curl -sL https://raw.githubusercontent.com/$REPO_OWNER/$REPO_NAME/main/sh/main.sh)"
command -v curl >/dev/null || die "缺少 curl，请先安装。"
command -v git  >/dev/null || {
  info "未检测到 git，尝试安装…"
  if command -v apt-get >/dev/null; then apt-get update && apt-get install -y git
  elif command -v yum >/dev/null; then yum install -y git
  elif command -v dnf >/dev/null; then dnf install -y git
  else die "无法自动安装 git，请手动安装后重试。"; fi
}

# ── 安装 .NET SDK ─────────────────────────────────────────
install_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    local v; v=$(dotnet --list-sdks 2>/dev/null | awk '{print $1}')
    if echo "$v" | grep -q '^10\.'; then
      info "已存在 .NET 10 SDK（$(dotnet --version)）"
      DOTNET_BIN="$(command -v dotnet)"
      return
    fi
  fi

  info "安装 .NET $DOTNET_CHANNEL SDK 到 $DOTNET_DIR …"
  local script; script="$(mktemp)"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o "$script"
  bash "$script" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR" --no-path
  rm -f "$script"

  export DOTNET_ROOT="$DOTNET_DIR"
  export PATH="$DOTNET_DIR:$PATH"
  DOTNET_BIN="$DOTNET_DIR/dotnet"
  info "安装完成：$("$DOTNET_BIN" --version)"
}

# ── 拉取源码 ──────────────────────────────────────────────
fetch_source() {
  info "拉取源码到 $INSTALL_DIR …"
  if [ -d "$INSTALL_DIR/.git" ]; then
    git -C "$INSTALL_DIR" pull --ff-only
  else
    git clone "https://github.com/$REPO_OWNER/$REPO_NAME.git" "$INSTALL_DIR"
  fi
}

# ── 编译发布 ──────────────────────────────────────────────
publish() {
  info "编译发布（Release）…"
  (cd "$INSTALL_DIR" && "$DOTNET_BIN" publish src/ControlHub.Server/ControlHub.Server.csproj -c Release -o "$INSTALL_DIR/server")
}

# ── 注册 systemd 服务 ─────────────────────────────────────
install_service() {
  info "注册 systemd 服务 $SERVICE_NAME …"
  cat > "/etc/systemd/system/$SERVICE_NAME.service" <<EOF
[Unit]
Description=ClassIsland ControlHub Server
After=network.target

[Service]
WorkingDirectory=$INSTALL_DIR/server
ExecStart=$DOTNET_BIN $INSTALL_DIR/server/ControlHub.Server.dll
Restart=always
RestartSec=3
Environment=ControlHub__HttpPort=$HTTP_PORT
Environment=ControlHub__DataDirectory=$INSTALL_DIR/server/data

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable "$SERVICE_NAME"
  systemctl restart "$SERVICE_NAME"
}

# ── 主流程 ────────────────────────────────────────────────
main() {
  info "开始部署 ClassIsland.ControlHub A 端服务器"
  install_dotnet
  fetch_source
  publish
  install_service

  local ip; ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  ip="${ip:-127.0.0.1}"

  cat <<EOF

============================================================
  ✅ 部署完成！

  本机访问：   http://127.0.0.1:$HTTP_PORT
  局域网访问： http://$ip:$HTTP_PORT

  默认账号：admin
  默认密码：admin123（登录后请立即在「系统设置」中修改）

  常用命令：
    查看状态   systemctl status $SERVICE_NAME
    查看日志   journalctl -u $SERVICE_NAME -f
    重启服务   systemctl restart $SERVICE_NAME

  数据目录：   $INSTALL_DIR/server/data/controlhub.db
              （备份服务器即复制此文件）

  升级：重新执行本脚本即可（会自动 git pull + 重新发布 + 重启服务）。
============================================================
EOF
}

main "$@"
