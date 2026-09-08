# Conclave

分布式 code review。每个接入的客户端都是对等节点（Elector），主动轮询 Azure DevOps
上的活跃 PR，按确定性规则分配评审席位，各自独立调用 Claude Code 产出结论，多数决形成
最终结论，全过程记录在不可篡改的哈希链账本（Acta）上。

设计文档：**[docs/DESIGN.md](docs/DESIGN.md)**

```
Conclave.App              Avalonia 12 UI + Generic Host 组装
Conclave.Application      DiscoveryService / ReviewOrchestrator / Ports
Conclave.Domain           Revision / Block / Elector / Hrw / SeatAssignment（零依赖，全纯函数）
Conclave.Infrastructure   SqliteActa / AzCliPrSource / ClaudeReviewRunner
```

## 跑起来

前置：`.NET 10 SDK`、`az`（已 `az devops login`）、`claude`、`git`。

```bash
dotnet test Conclave.slnx                            # 136 个测试
dotnet run --project src/Conclave.App                # 起 UI + 后台服务
dotnet run --project src/Conclave.App -- serve       # 无 UI 常驻（worker 机器 / 本机联调）
dotnet run --project src/Conclave.App -- review 2878 # 无头：只评这一个 PR 然后退出
scripts/package-macos.sh                             # 打成 dist/Conclave.app
```

无头模式的退出码给脚本用：

| 码 | 含义 |
|---|---|
| 0 | approve / approve-with-suggestions |
| 1 | reject / wait-for-author |
| 2 | 找不到这个 PR |
| 3 | 执行失败（claude 子进程或契约解析） |
| 4 | 超时前没出结论 |

首次启动会在 `~/.conclave/` 下生成：

| 文件 | 说明 |
|---|---|
| `elector.key` | 本节点的 ECDsa P-256 私钥，权限 0600 |
| `acta.db` | 本节点的 Acta 账本（SQLite，append-only） |

## 当前状态：P0 / P0.5

**已经能用的**：

- 跨 34 个 project 自动发现活跃 PR，按 `(prId, srcCommit)` 幂等去重
  —— 不漏、不重复烧 token，作者 push 后自动重评
- Summons / Seating / Ballot / Promulgation / Recess 五种区块，签名 + 前块哈希链接
- 席位分配规则全套（硬过滤 + 加权 HRW + quorum 自适应），跑在 mesh 语义上
- UI：PR 队列、Acta 账本浏览器、手动触发评审

**两个默认关闭的开关**（在 UI 右上角）：

| 开关 | 默认 | 为什么 |
|---|---|---|
| 自动评审 | **关** | 一开机就把所有活跃 PR 全评一遍会烧掉可观额度。先手动点几个确认链路 |
| 投递到 AzDO | **关** | 确认合并结果质量之前不要往真实 PR 上发评论、投票 |

## 与 az-pr-review skill 的契约

`ClaudeReviewRunner` 以 `REVIEW_MODE=collect` 起 `claude -p "/az-pr-review <id>"`，
skill（`~/.claude/skills/az-pr-review/SKILL.md` §7）在这个模式下**跳过发评论与投票**，
并在回复末尾输出：

```json
{"decision":"reject","findings":[{"file":"src/A.cs","line":12,"severity":"major","title":"…","detail":"…"}]}
```

解析不到这个块就出 `Error` 票 —— 刻意不从自然语言里猜结论：猜错方向会放过该拦的 PR，
而 Error 票只会让这一轮不计入多数决。

**为什么 skill 必须跳过投递**：quorum=3 时有 3 个节点跑同一个 PR，若各自都投递，
一个 PR 会收到 3 条重复评论和 3 次投票。投递只由最低未弃权席位的节点在收齐票后做一次。

## 组 mesh

1. 每台机器起一次，记下 UI 左上角或日志里的 `elector` 指纹
2. 互相把对方的指纹写进 `~/.conclave/electors.allow`（改动即时生效，见
   `scripts/electors.allow.example`）
3. 在 `~/.conclave/appsettings.json` 里打开 `Conclave.Mesh.Enabled`

```
UDP 多播 239.255.42.7:47707   ← 签名心跳（谁在线 + HTTP 端点 + 能力）
HTTP     :47708               ← POST /blocks · GET /chains/{id} · GET /elector
```

**白名单决定「谁的评审任务可以在你的机器上跑 Bash」**，只加同一团队、本来就都有对应
repo 权限的人的机器。文件不存在 = 只信任自己 = 单机模式。

本机跑两个节点联调：

```bash
CONCLAVE_Conclave__HomeDirectory=/tmp/node-a dotnet run --project src/Conclave.App -- serve
CONCLAVE_Conclave__HomeDirectory=/tmp/node-b dotnet run --project src/Conclave.App -- serve
```

## 已知限制

- **跨网段不通**。UDP 多播只在同一网段内扩散，远程办公需要一个固定 IP 的种子节点
  （尚未实现）。
- **diff 统计依赖本机 clone**。`RepoSearchRoots` 里找不到对应 repo 时统计为 0，
  quorum 退到 1（安全方向）。定位器按目录名**和** `origin` 解析出的远端仓库名两者登记，
  所以 `~/projects/user_svc`（远端叫 `liontrip-user`）也能认出来。
  本机仍缺 `payment-center`、`PIM-UI`、`cms-apostrophe` 的 clone。
- **索引冲突的 rebase 留到 P2**：单节点走不到那条路径。

## 两个踩过的坑（写在代码注释里，这里也留一份）

1. **`.NET 10 没有内置 Ed25519**（只有后量子的 MLDsa/SlhDsa），所以签名用 ECDsa P-256。
2. **`Conclave.Application`（分层名）会盖住 `Avalonia.Application`**，而 C# 名称解析里
   外层命名空间成员优先于编译单元级 using 别名 —— `using Application = Avalonia.Application;`
   在这里无效，只能全限定。见 `src/Conclave.App/App.axaml.cs`。
3. **`new Guid(byte[])` 前三段按小端读**，所以按 RFC 4122 的字节位置改 version/variant
   要用 `bigEndian: true` 重载，否则派生出来的 UUID 版本号是错的。
4. **测试里别调 `SqliteConnection.ClearAllPools()`** —— 它是进程全局的，会把并行跑的
   其他测试的连接池一起清掉，制造难查的偶发失败。
5. **中文紧贴 shell 变量要加花括号** —— bash 把多字节字符当成变量名的合法字符，
   `"$executed，"` 会被解析成变量名 `executed，`，在 `set -u` 下报未绑定。
6. **配置绑定对集合是追加语义**（接口类型集合有没有 setter 都一样），属性初始化器里
   带默认值 + 配置文件再列一遍 = 两份。默认值要在绑定后判空回填。
7. **gossip 只能转发新块** —— 对「本来就有」也转发会让区块在两节点间无限回弹，
   实测把进程 OOM 掉过。
