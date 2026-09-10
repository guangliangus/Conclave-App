#!/usr/bin/env bash
# 在本机跑两个 Conclave 节点，验证 mesh 的发现、实时状态互拉与席位分配。
#
#   scripts/local-mesh.sh init    第一次：各起一次生成身份，互相写白名单
#   scripts/local-mesh.sh up      同时跑两个节点（Ctrl+C 停）
#   scripts/local-mesh.sh peek    curl 两个节点的 /elector 与 /state
#   scripts/local-mesh.sh clean   删掉两个节点的目录
#
# 三件必须分开的东西：
#   HomeDirectory —— 私钥、账本、临时工作区。共用会让两个节点变成同一个身份
#   Mesh.HttpPort —— 同一台机器上第二个节点绑不上同一个端口
#   （心跳端口反而**必须相同**：MeshBeaconSocket 在 Bind 之前设了 ReuseAddress、
#     并打开了 MulticastLoopback，正是为了本机多节点联调）
set -euo pipefail
cd "$(dirname "$0")/.."

A_HOME=${A_HOME:-/tmp/conclave-node-a}
B_HOME=${B_HOME:-/tmp/conclave-node-b}
A_PORT=${A_PORT:-47708}
B_PORT=${B_PORT:-47709}
# 只轮一个 project，否则冷启动要给每个活跃 PR 花两次 az 调用（约 3 秒/个）
PROJECT=${PROJECT:-edison-test}

node() {
  local home=$1 port=$2; shift 2
  CONCLAVE_Conclave__HomeDirectory="$home" \
  CONCLAVE_Conclave__Mesh__Enabled=true \
  CONCLAVE_Conclave__Mesh__HttpPort="$port" \
  CONCLAVE_Conclave__ProjectAllowList__0="$PROJECT" \
  CONCLAVE_Logging__LogLevel__Conclave=${LOGLEVEL:-Information} \
  dotnet run --project src/Conclave.App -- serve "$@"
}

fingerprint() { grep -o '节点身份 [0-9a-f]*' "$1" | head -1 | awk '{print $2}'; }

case "${1:-}" in
  init)
    rm -rf "$A_HOME" "$B_HOME"
    for spec in "$A_HOME $A_PORT /tmp/cm-a.log" "$B_HOME $B_PORT /tmp/cm-b.log"; do
      set -- $spec
      node "$1" "$2" > "$3" 2>&1 & pid=$!
      sleep 10; kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true
    done
    a=$(fingerprint /tmp/cm-a.log); b=$(fingerprint /tmp/cm-b.log)
    [ -n "$a" ] && [ -n "$b" ] || { echo "取不到指纹，看 /tmp/cm-{a,b}.log" >&2; exit 1; }
    printf '# node-b\n%s\n' "$b" > "$A_HOME/electors.allow"
    printf '# node-a\n%s\n' "$a" > "$B_HOME/electors.allow"
    echo "node-a $a  ($A_HOME, :$A_PORT)"
    echo "node-b $b  ($B_HOME, :$B_PORT)"
    echo "白名单已互写。接着跑：scripts/local-mesh.sh up"
    ;;
  up)
    [ -f "$A_HOME/electors.allow" ] || { echo "先跑 init" >&2; exit 1; }
    node "$A_HOME" "$A_PORT" 2>&1 | sed 's/^/[a] /' &
    node "$B_HOME" "$B_PORT" 2>&1 | sed 's/^/[b] /' &
    trap 'kill 0' INT TERM
    wait
    ;;
  peek)
    for port in "$A_PORT" "$B_PORT"; do
      echo "── :$port ─────────────────────────────"
      curl -s --max-time 3 "http://localhost:$port/elector" \
        | python3 -c 'import json,sys; d=json.load(sys.stdin); print("  elector", d["Id"], d["Endpoint"], f"额度 {d[\"Utilization\"]:.0%}")' 2>/dev/null \
        || { echo "  (没在跑)"; continue; }
      curl -s --max-time 3 "http://localhost:$port/state" | python3 -c '
import json,sys
d = json.load(sys.stdin); st = json.loads(d["StateJson"])
print(f"  state   v{st[\"Version\"]}  队列 {len(st[\"Discovered\"])}  在评 {len(st[\"Reviewing\"])}  认领 {len(st[\"Claims\"])}  待确认 {len(st[\"Pending\"])}")
for x in st["Discovered"][:5]:
    print(f'"'"'     · {x["Revision"]["Id"]:20s} {x["Pr"]["Repo"]:16s} quorum={x["Quorum"]}'"'"')
for r in st["Reviewing"]:
    print(f'"'"'     ▶ {r["RevisionId"]} round={r["Round"]} 自 {r["StartedAt"][:19]}'"'"')
'
    done
    ;;
  clean) rm -rf "$A_HOME" "$B_HOME"; echo "已删除" ;;
  *) sed -n '2,12p' "$0"; exit 1 ;;
esac
