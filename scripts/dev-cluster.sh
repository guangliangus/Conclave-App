#!/usr/bin/env bash
# 本机起多个 Conclave 节点做联调。
#
#   ./scripts/dev-cluster.sh up 3       # 起 3 个无界面节点（serve）
#   ./scripts/dev-cluster.sh ui 2       # 起 2 个带面板的节点（默认模式）
#   ./scripts/dev-cluster.sh status     # 每个节点的身份、发现了几个邻居、链有多长
#   ./scripts/dev-cluster.sh logs       # 跟踪全部日志
#   ./scripts/dev-cluster.sh down       # 停（只停本脚本起的，按 pid 文件）
#   ./scripts/dev-cluster.sh clean      # 停 + 删掉这些节点的 home（私钥与账本一起删）
#
# 每个节点只有两处不同：HomeDirectory 和 Mesh.HttpPort。其余都一样。
#
# ⚠️ 这个脚本开了 Mesh.TrustAllElectors —— 它关掉的是「谁能让你的机器起 claude 跑 Bash」
#    那道闸。这里只用环境变量传，不写进任何配置文件，所以随进程消失、不会忘在机器上。
#    不想开的话：DEV_TRUST_ALL=false，然后按 README 的做法互相写 electors.allow。
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="${CONCLAVE_DEV_ROOT:-/tmp/conclave-dev}"
APP="$REPO/src/Conclave.App/bin/Debug/net10.0/conclave.dll"
PIDS="$ROOT/pids"

BASE_PORT="${DEV_BASE_PORT:-47708}"
QUORUM="${DEV_QUORUM:-2}"                 # 默认 2：不然席位表只有一个人，另一个永远旁观
TRUST_ALL="${DEV_TRUST_ALL:-true}"
AUTO_REVIEW="${DEV_AUTO_REVIEW:-false}"   # 默认关：开了每个节点都会真跑 claude、各烧一份额度
# 同机节点的 az 身份都是你自己，拿自己提的 PR 联调会所有节点都出局、PR 卡在队列里。
# 默认开着正是为了让联调能跑起来；生产上绝不能开（等于作者给自己盖章）。
SELF_REVIEW="${DEV_ALLOW_SELF_REVIEW:-true}"
FAKE_PROJECT="${DEV_FAKE_PROJECT:-}"      # 设成非空则不轮询真实 AzDO（填一个不存在的 project）

# 注意：变量后面紧跟中文标点时必须写成 ${VAR} —— macOS 的 bash 会把全角标点的
# 首字节当成变量名的一部分（$QUORUM，会被读成 QUORUM\xef）。
die() { echo "错误：$*" >&2; exit 1; }

build() {
  echo "==> 构建"
  dotnet build "$REPO/src/Conclave.App/Conclave.App.csproj" -v q --nologo
  [ -f "$APP" ] || die "构建完却找不到 $APP"
}

# 某个节点已经认识几个邻居。按指纹去重 —— 同一个邻居重连会重复打印。
# 不用 grep -c：它无匹配时退出码为 1，配 `|| echo 0` 会拼出两行、把 [ 弄崩。
peer_count() {
  grep -o "发现节点 [0-9a-f]*" "$1" 2>/dev/null | awk '{print $2}' | sort -u | wc -l | tr -d ' '
}

# 起一个节点。$1 = 序号，$2 = 模式（serve / ui）
start_node() {
  local i="$1" mode="$2"
  local home="$ROOT/node-$i"
  local port=$((BASE_PORT + i - 1))
  mkdir -p "$home"

  # 走 dll 而不是 apphost：apphost 要在标准位置找 .NET，而 dotnet 常常装在
  # /opt/homebrew 之类的地方，那样会报 "You must install .NET to run this application"。
  local args=()
  [ "$mode" = serve ] && args+=(serve)

  local env=(
    "CONCLAVE_Conclave__HomeDirectory=$home"
    "CONCLAVE_Conclave__Mesh__Enabled=true"
    "CONCLAVE_Conclave__Mesh__HttpPort=$port"
    "CONCLAVE_Conclave__Mesh__TrustAllElectors=$TRUST_ALL"
    "CONCLAVE_Conclave__Quorum__Default=$QUORUM"
    "CONCLAVE_Conclave__AutoReview=$AUTO_REVIEW"
    "CONCLAVE_Conclave__AllowSelfReview=$SELF_REVIEW"
    # 心跳被丢弃的原因（不在白名单 / 验签失败 / 时间戳不新鲜）都是 DEBUG 级，
    # 不开就只能看到「什么都没发生」。
    "CONCLAVE_Logging__LogLevel__Conclave=Debug"
  )
  [ -n "$FAKE_PROJECT" ] && env+=("CONCLAVE_Conclave__ProjectAllowList__0=$FAKE_PROJECT")

  # nohup + disown 不能省：不脱离进程组的话，外壳一退就把节点一起带走。
  env "${env[@]}" nohup dotnet "$APP" "${args[@]}" >> "$home/serve.log" 2>&1 &
  local pid=$!
  disown "$pid" 2>/dev/null || true
  echo "$pid" >> "$PIDS"
  echo "  node-$i  pid $pid  :$port  $home"
}

up() {
  local n="${1:-2}" mode="${2:-serve}"
  [[ "$n" =~ ^[0-9]+$ ]] || die "节点数要是数字，收到 '$n'"
  [ "$n" -ge 1 ] || die "至少一个节点"
  [ -f "$PIDS" ] && die "已经有一组在跑了（${PIDS}）。先 down，或者 clean"

  build
  mkdir -p "$ROOT"
  : > "$PIDS"

  echo "==> 起 $n 个节点（$mode 模式，quorum=${QUORUM}，自动评审=${AUTO_REVIEW}，自评=${SELF_REVIEW}）"
  for i in $(seq 1 "$n"); do start_node "$i" "$mode"; done

  # mesh 开启后会先等两个心跳周期让成员表收敛，所以给足时间
  echo "==> 等互相发现（每个节点要看到 $((n - 1)) 个邻居）"
  local want=$((n - 1)) ok=0
  for _ in $(seq 1 90); do
    ok=0
    for i in $(seq 1 "$n"); do
      local seen
      seen=$(peer_count "$ROOT/node-$i/serve.log")
      [ "$seen" -ge "$want" ] && ok=$((ok + 1))
    done
    [ "$ok" -eq "$n" ] && break
    sleep 1
  done

  if [ "$ok" -eq "$n" ]; then
    echo "==> 成了：$n 个节点互相都看见了"
  else
    echo "==> 只有 $ok/$n 个节点看齐了邻居。看这几行找原因："
    grep -h "丢弃心跳\|白名单\|mesh 接口监听" "$ROOT"/node-*/serve.log | tail -8 || true
  fi
  status
}

status() {
  [ -d "$ROOT" ] || die "没有 ${ROOT}，先 up"
  printf "\n%-8s %-18s %-6s %-7s %-8s %s\n" node elector peers blocks reviews port
  for home in "$ROOT"/node-*; do
    [ -d "$home" ] || continue
    local name id peers blocks reviews port
    name=$(basename "$home")
    id=$(grep -o "节点身份 [0-9a-f]*" "$home/serve.log" 2>/dev/null | tail -1 | awk '{print $2}')
    # 同一个邻居重连会重复打印，所以按指纹去重才是真实邻居数
    peers=$(peer_count "$home/serve.log")
    port=$(grep -o "mesh 接口监听 http://[^ ]*" "$home/serve.log" 2>/dev/null | tail -1 | sed 's/.*://')
    blocks=$(sqlite3 "$home/acta.db" "select count(*) from blocks" 2>/dev/null || echo -)
    reviews=$(sqlite3 "$home/acta.db" "select count(*) from reviews" 2>/dev/null || echo -)
    printf "%-8s %-18s %-6s %-7s %-8s %s\n" \
      "$name" "${id:-?}" "$peers" "$blocks" "$reviews" "${port:-?}"
  done
  echo
}

# 停机宽限期。app 侧收到 SIGTERM 后要掐断在跑的评审、各补一张 Error 票再退，
# 那需要几秒（host.StopAsync 的预算是 5 秒）。给够，别把正常收尾当成卡死。
STOP_GRACE="${DEV_STOP_GRACE:-12}"

down() {
  [ -f "$PIDS" ] || { echo "没有在跑的节点"; return 0; }

  # 按 pid 文件停，不用 pkill -f —— 那会顺手杀掉你日常在跑的那个真实节点
  local pids=() pid
  while read -r pid; do
    [ -n "$pid" ] && pids+=("$pid")
  done < "$PIDS"

  for pid in "${pids[@]}"; do
    kill "$pid" 2>/dev/null && echo "已发 SIGTERM 给 pid $pid"
  done

  echo "==> 等优雅退出（最多 ${STOP_GRACE}s）"
  local alive
  for _ in $(seq 1 "$STOP_GRACE"); do
    alive=0
    for pid in "${pids[@]}"; do
      kill -0 "$pid" 2>/dev/null && alive=$((alive + 1))
    done
    [ "$alive" -eq 0 ] && break
    sleep 1
  done

  # 宽限期过了还活着的硬杀。这一步不能省：pid 文件一删，赖着的进程就再也定位不到了，
  # 而它们还占着 mesh 端口 —— 下次 up 会撞 Address already in use，然后那个节点
  # 静默退为单机模式，联调看起来「起来了」其实根本没组网。实测踩过。
  for pid in "${pids[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then
      kill -9 "$pid" 2>/dev/null && echo "  ⚠ pid $pid 宽限 ${STOP_GRACE}s 未退，已 kill -9"
    fi
  done

  rm -f "$PIDS"
}

case "${1:-}" in
  up)     shift; up "${1:-2}" serve ;;
  ui)     shift; up "${1:-2}" ui ;;
  status) status ;;
  logs)   tail -f "$ROOT"/node-*/serve.log ;;
  down)   down ;;
  clean)  down; rm -rf "$ROOT"; echo "已删 ${ROOT}（含私钥与账本）" ;;
  *)      sed -n '2,17p' "$0"; exit 1 ;;
esac
