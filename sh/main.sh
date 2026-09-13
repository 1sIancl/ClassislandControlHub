#!/usr/bin/env bash
#
# ClassIsland.ControlHub · A 端服务器 Linux 一键部署脚本
#
# 用法（在目标 Linux 服务器上，用 root 执行）：
#   curl -fsSL https://cdn.jsdelivr.net/gh/1sIancl/ClassislandControlHub@main/sh/main.sh -o /tmp/controlhub-install.sh && sudo bash /tmp/controlhub-install.sh
#   （jsDelivr CDN 国内访问快；备选 fastly.jsdelivr.net 或 raw.githubusercontent.com）
#
# 说明：脚本会依次完成——安装 .NET 10 SDK（如缺失）→ 拉取源码 → 编译发布 →
#       注册并启动 systemd 服务。数据保存在 /opt/classisland-controlhub/server/data。
#
# 注意：不要用 `sudo bash <(curl -sL ...)` 形式。进程替换依赖 /dev/fd/N，
#       sudo 提权后会关闭这些文件描述符，导致 bash 报
#       `bash: /dev/fd/63: No such file or directory`。
#
# 国内服务器：拉取源码时 github.com 直连常超时，脚本会依次尝试内置镜像；也可用 GIT_MIRROR 指定镜像，
#      例如：GIT_MIRROR=https://kkgithub.com sudo bash /tmp/controlhub-install.sh
#
set -euo pipefail

REPO_OWNER="1sIancl"
REPO_NAME="ClassislandControlHub"
INSTALL_DIR="/opt/classisland-controlhub"
SERVICE_NAME="classisland-controlhub"
HTTP_PORT="${HTTP_PORT:-29800}"
DOTNET_DIR="/opt/dotnet"
DOTNET_CHANNEL="10.0"
DOTNET_BIN=""

# 国内服务器访问 github.com 常因网络不通而超时（`curl 28 Couldn't connect to server`）。
# 可通过 GIT_MIRROR 指定可用的镜像前缀，例如：
#   GIT_MIRROR=https://kkgithub.com  bash install.sh                      # 域名镜像
#   GIT_MIRROR=https://ghfast.top/https://github.com  bash install.sh     # 前缀代理
# 未指定时，脚本会依次尝试直连与若干内置镜像，最后一个失败才报错。
GIT_MIRROR="${GIT_MIRROR:-}"

info() { printf '\033[36m[集控]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[警告]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[错误]\033[0m %s\n' "$*" >&2; exit 1; }

# ── 环境检查 ──────────────────────────────────────────────
[ "$(id -u)" -eq 0 ] || die "请以 root 运行：curl -fsSL https://raw.githubusercontent.com/$REPO_OWNER/$REPO_NAME/main/sh/main.sh -o /tmp/controlhub-install.sh && sudo bash /tmp/controlhub-install.sh"
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
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
  bash "$script" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR" --no-path
  rm -f "$script"

  export DOTNET_ROOT="$DOTNET_DIR"
  export PATH="$DOTNET_DIR:$PATH"
  DOTNET_BIN="$DOTNET_DIR/dotnet"
  info "安装完成：$("$DOTNET_BIN" --version)"
}

# ── 拉取源码 ──────────────────────────────────────────────
# 依次返回候选的 git 地址：用户镜像优先，其次直连，最后内置镜像兜底。
clone_urls() {
  local repo="$REPO_OWNER/$REPO_NAME"
  [ -n "$GIT_MIRROR" ] && printf '%s\n' "${GIT_MIRROR%/}/$repo.git"
  printf '%s\n' "https://github.com/$repo.git"
  printf '%s\n' "https://kkgithub.com/$repo.git"
  printf '%s\n' "https://ghfast.top/https://github.com/$repo.git"
  printf '%s\n' "https://gitclone.com/github.com/$repo.git"
}

# 逐个地址尝试 clone，直到成功。用 lowSpeed 限速让「连不上」快速失败（约 25 秒），
# 而不是像直连那样干等 133 秒才超时。
git_clone_with_fallback() {
  local dest="$1" url ok=0
  while IFS= read -r url; do
    [ -z "$url" ] && continue
    info "尝试拉取：$url"
    if git -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=25 \
           clone "$url" "$dest"; then
      ok=1
      break
    fi
    warn "该地址拉取失败，尝试下一个…"
    rm -rf "$dest" 2>/dev/null || true
  done <<< "$(clone_urls)"
  [ "$ok" -eq 1 ] || die "无法拉取源码（所有地址均失败）。可设置 GIT_MIRROR 指定可用镜像后重试。"
}

fetch_source() {
  info "拉取源码到 $INSTALL_DIR …"
  if [ -d "$INSTALL_DIR/.git" ]; then
    if git -C "$INSTALL_DIR" pull --ff-only 2>/dev/null; then
      return 0
    fi
    warn "更新失败，改用镜像重新拉取…"
    rm -rf "$INSTALL_DIR"
  fi
  git_clone_with_fallback "$INSTALL_DIR"
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

  info "等待服务启动并自检…"
  sleep 2
  if systemctl is-active --quiet "$SERVICE_NAME"; then
    if curl -fsS "http://127.0.0.1:$HTTP_PORT/" >/dev/null 2>&1; then
      info "HTTP 自检通过：http://127.0.0.1:$HTTP_PORT"
    else
      warn "服务已启动，但 HTTP 自检未通过（可能仍在初始化）。请稍后重试，或查看日志：journalctl -u $SERVICE_NAME -n 50"
    fi
  else
    warn "服务未能启动，请运行 journalctl -u $SERVICE_NAME -n 50 查看原因。"
  fi
}

# ── 主流程 ────────────────────────────────────────────────
main() {
  info "开始部署 ClassIsland.ControlHub A 端服务器"
  install_dotnet
  fetch_source
  publish
  install_service

  local ip
  ip="$(hostname -I 2>/dev/null | awk 'NR==1{print $1}')" || true
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
