#!/usr/bin/env bash
# 门禁 02b：确认架构守卫真的执行过，而不是 0 个测试报绿。
#
# 这个项目的正确性有两根支柱，都只由架构测试守着：
#   1. 领域层是纯函数 —— 否则各节点算出不同席位表，表现为重复评审或集体旁观，且不报错
#   2. 分层依赖朝内 —— 否则无头进程（conclave review）在运行时才炸
# 一旦这个 assembly 没被测试发现（改名、漏 ProjectReference），"0 个测试" 同样是绿色。
set -euo pipefail

MIN_TESTS=${MIN_ARCHITECTURE_TESTS:-7}
RESULTS_DIR=tests/Conclave.ArchitectureTests/TestResults

# 取最新的一份，免得读到上一轮的陈旧结果而误判「跑过了」
trx=$(find "$RESULTS_DIR" -name '*.trx' 2>/dev/null | sort | tail -1)
if [ -z "$trx" ]; then
  echo "找不到 ${RESULTS_DIR} 下的 trx —— 架构守卫根本没跑" >&2
  exit 1
fi

# 用 sed 精确取捕获组：BSD grep 的 -o 配 [0-9]* 会吐出空匹配
counters=$(grep -o '<Counters[^>]*>' "$trx" | head -1)
field() { printf '%s' "$counters" | sed -n "s/.*$1=\"\([0-9]*\)\".*/\1/p"; }

executed=$(field executed)
passed=$(field passed)
: "${executed:=0}"
: "${passed:=0}"

# 中文紧贴变量必须加花括号：bash 把多字节字符当成变量名的合法字符，
# "$executed，" 会被解析成变量名 "executed，"，在 set -u 下直接报未绑定。
echo "架构守卫：执行 ${executed}，通过 ${passed}（下限 ${MIN_TESTS}）· ${trx}"

if [ "$executed" -lt "$MIN_TESTS" ]; then
  echo "架构守卫只跑了 ${executed} 个，少于下限 ${MIN_TESTS} —— 有守卫被静默跳过了" >&2
  exit 1
fi

if [ "$executed" != "$passed" ]; then
  echo "有架构守卫失败（执行 ${executed}，通过 ${passed}）" >&2
  exit 1
fi
