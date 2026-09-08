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
dotnet test Conclave.slnx           # 76 个测试
dotnet run --project src/Conclave.App
```

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

## 还没接上的一环

`ClaudeReviewRunner` 以 `REVIEW_MODE=collect` 起 `claude -p "/az-pr-review <id>"`，
并期望回复末尾有一个 JSON 块：

```json
{"decision":"reject","findings":[{"file":"src/A.cs","line":12,"severity":"major","title":"…","detail":"…"}]}
```

`~/.claude/skills/az-pr-review/SKILL.md` 目前**还没有这个分支**，所以评审会出
`Error` 票（链上留痕，不参与多数决 —— 刻意不去猜结论）。该 skill 已有 **Dry run**
分支（"just show me" / "don't post"），改成读 `REVIEW_MODE` 环境变量并追加 JSON 输出即可。

这一步必须做对：quorum=3 时有 3 个节点跑同一个 PR，若各自都投递，一个 PR 会收到
3 条重复评论和 3 次投票。投递只由 round=0 的节点在收齐票后做一次。

## 已知限制

- **mesh 只有自己**（`LocalMesh`）。P1 才接 mDNS + gRPC，届时**节点白名单必须同步上线** ——
  别人的 Seating 块会让别人的评审任务在你机器上跑 `Bash`。
- **diff 统计依赖本机 clone**。`RepoSearchRoots` 里找不到对应 repo 时统计为 0，
  quorum 退到 1（安全方向）。实测本机缺 `payment-center`、`edison-test`、`PIM-UI`
  的 clone，这些 PR 目前都只会单跑。
- **索引冲突的 rebase 留到 P2**：单节点走不到那条路径。

## 两个踩过的坑（写在代码注释里，这里也留一份）

1. **`.NET 10 没有内置 Ed25519**（只有后量子的 MLDsa/SlhDsa），所以签名用 ECDsa P-256。
2. **`Conclave.Application`（分层名）会盖住 `Avalonia.Application`**，而 C# 名称解析里
   外层命名空间成员优先于编译单元级 using 别名 —— `using Application = Avalonia.Application;`
   在这里无效，只能全限定。见 `src/Conclave.App/App.axaml.cs`。
