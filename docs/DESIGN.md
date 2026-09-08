# Conclave — 设计文档

> **con·clave** /ˈkɒŋkleɪv/ — 拉丁语 *cum clave*「以钥匙锁闭」。
> 闭门独立评议、投票形成决议、决议封存可查证的会议。

分布式 code review 系统。每个接入的客户端都是对等节点（Elector），主动轮询
Azure DevOps 上的活跃 PR，按确定性规则分配评审席位，各自独立调用 Claude Code
产出结论，多数决形成最终结论，全过程记录在不可篡改的哈希链账本（Acta）上。

---

## 1. 为什么是这个形态

现状是一个飞书机器人桥接（`~/lark-claude-bridge/bridge.sh`）：人肉把 PR 号发给机器人，
机器人在**单台机器**上跑 `claude -p "/az-pr-review <PR号>"`。三个已知痛点：

| 痛点 | Conclave 的解法 |
|---|---|
| 同一个飞书应用**不能在多台机器同时订阅事件**（事件随机分流） | 不依赖飞书事件。每个节点**主动轮询 AzDO**，谁都能发现 PR |
| 要人肉发 PR 号，漏掉的 PR 就是漏掉了 | 轮询 34 个 project 的活跃 PR，**自动发现、自动去重** |
| 单台机器是瓶颈也是单点 | 评审工作按规则分散到 mesh 里所有合格节点 |

额外拿到的东西：多节点独立评审同一个 PR，**能压掉 LLM 的输出方差**——被 3 个节点
独立提到的 finding 几乎必然是真问题，只被 1 个提到的大概率是噪音。这是本设计里
最有实际价值的部分，也是引入 quorum 的**唯一**理由。

## 2. 明确不做的事

这不是区块链产品。借用的只有三个部件，其余一律不要：

| 采用 | 理由 |
|---|---|
| ✅ P2P 对等网络，人人是 host | 核心需求 |
| ✅ 哈希链 + 签名账本 | review 记录可审计、不可篡改 |
| ✅ quorum 多数决 | 压 LLM 方差（见上） |

| 不做 | 理由 |
|---|---|
| ❌ 挖矿 / PoW / PoS / 代币 | 内网可信节点，不需要防女巫攻击 |
| ❌ 全局单一账本 | 换成**每个 PR 一条独立链**，绕开全局排序难题（见 §4） |
| ❌ 拜占庭容错 | 节点白名单已假定成员不作恶；只防「掉线」不防「撒谎」 |
| ❌ 智能合约 | 规则是编译进二进制的纯函数，版本靠规则哈希上链留痕 |

## 3. 领域词汇

隐喻贯彻到类型名，代码自解释：

| 类型 | 含义 |
|---|---|
| `Elector` | 节点 —— 有评议资格的成员 |
| `ElectorIdentity` | 节点身份 —— ECDsa P-256 密钥对 |
| `Revision` | 评审的最小单元 = PR 的某个版本 |
| `Summons` | 区块：发现 PR，召集评议 |
| `Seating` | 区块：认领席位 |
| `Ballot` | 区块：签名的评审结论 |
| `Promulgation` | 区块：公布最终结论并投递回 AzDO |
| `Recess` | 区块：超时弃权，席位重新分配 |
| `Acta` | 会议录 —— 那条哈希链 |
| `ReservedMatter` | 敏感路径 —— 强制拉满 quorum |

## 4. 幂等键：`(prId, srcCommit)`

**整个设计最容易做错的地方。** 用 PR ID 当 key，PR 被 push 新 commit 后就不会重
review；每轮轮询都重跑，又会重复烧 token。

```csharp
public sealed record Revision(string Project, int PrId, string SrcCommit)
{
    public string Id      => $"{PrId}@{SrcCommit[..8]}";   // 2721@dc1d1d47
    public string ChainId => $"pr:{Project}:{PrId}";       // 一个 PR 一条链
}
```

`SrcCommit` 取 `az repos pr show` 的 `lastMergeSourceCommit.commitId`。

- 链上有该 `Revision` 的 `Promulgation` → 跳过
- 作者 push 了新 commit → `SrcCommit` 变 → 新 `Revision` → 自动触发新一轮

**不需要任何额外的「是否已处理」状态表，Acta 本身就是。** 一个 PR 的多个
revision 追加在同一条 `ChainId` 上，PR 的完整评审史一目了然。

## 5. Acta：每个 PR 一条链

不做全局单链，就不需要共识算法——这是能砍掉 PoW/BFT 的根本原因。

```csharp
public sealed record Block(
    string         ChainId,
    long           Index,
    string         PrevHash,
    DateTimeOffset At,
    BlockKind      Kind,
    string         PayloadJson,
    string         ElectorId,
    string         PublicKey,
    string         Signature);
```

`Hash = SHA256(ChainId|Index|PrevHash|At|Kind|PayloadJson|ElectorId)`

一条链的典型形态：

```
[0] Summons      rev=2721@dc1d1d47  quorum=2   由发现节点写入
[1] Seating      round=0  elector=A
[2] Seating      round=1  elector=B
[3] Ballot       round=0  elector=A  reject  findings=5   [signed]
[4] Ballot       round=1  elector=B  reject  findings=3   [signed]
[5] Promulgation decision=reject  merged=6  threadId=8821
```

**落库**：SQLite，主键 `(ChainId, Index)`。append 前校验三件事——签名有效、
`ElectorId` 在白名单、`PrevHash` 等于本地链尾哈希。

**冲突解决**（网络分区合并后同 `(ChainId, Index)` 收到两个块）：取 **blockHash
字典序小**者胜出，另一个 rebase 到下一个 index。确定性，无需协商。

## 6. 发现层：34 个 project 怎么分

不要每个节点都轮 34 个 project。**project 也按 HRW 分片**：

```csharp
IEnumerable<string> MyProjects(IReadOnlyList<Elector> alive) =>
    allProjects.Where(p => Hrw.Pick($"discover:{p}", 0, alive) == self.Id);
```

成本：5 节点 / 34 project → 每节点约 7 个，60 秒一轮 = **7 req/min/节点**。
节点掉线时 HRW 自动把它的份额重分给别人，**没有故障切换代码**。

> ⚠️ `az repos pr list` 打到 ADO **Server** 时每次会往 stderr 写一行
> `WARNING: ... does not support Azure DevOps Server`，但功能正常。解析时只读 stdout。

## 7. 席位分配规则

硬规则做过滤，软规则做权重。两段都必须是**纯函数**——每个节点要独立算出同一结果。

### 7.1 硬规则（不满足直接出局）

```csharp
bool Eligible(Elector n, Revision rev, PrMeta pr) =>
    n.Repos.Contains(pr.Repo)                       // 本机有 clone
 && n.Projects.Contains(rev.Project)                // az 身份对该 project 有读权限
 && !AzIdentity.SamePerson(n.AzIdentity, pr.Author) // ★ 不 review 自己的 PR
 && n.RunningJobs < n.MaxConcurrent                 // 没打满
 && n.IsAlive(now);                                 // 心跳 90 秒内
```

第三条是硬性的：`createdBy.uniqueName` 实测形如 `LIONMAIL\youngsun`，
节点心跳里带上自己的 `az` 登录身份做比对。

### 7.2 软规则（加权 rendezvous 哈希）

```csharp
score(n) = Weight(n) / -ln(u(key, round, n))     取最大者

Weight(n) = 1 / (1 + n.RunningJobs)      // 负载均衡：忙的少拿
              / (1 + n.Reviews24h * 0.1) // 公平性：最近干得多的少拿
```

用 HRW 而不是 `hash % count`：节点上下线时**只有该节点的份额会重排**，
其余分配保持稳定。

### 7.3 quorum 自适应

不是所有 PR 都值得跑 3 遍：

| 条件 | quorum |
|---|---|
| `IsDraft` | 0（不评审） |
| 触及 `ReservedMatter`（支付/认证/密钥/对外 API 契约） | 3 |
| `LinesChanged > 500` 或 `FilesChanged > 20` | 3 |
| `LinesChanged > 80` | 2 |
| 其余 | 1 |

**敏感路径清单哈希后写进 `Summons` 块**，这样链上能证明当时用的是哪一版规则。

## 8. 异常处理

| 情况 | 处理 |
|---|---|
| **认领后节点挂了** | 任何节点见到 `Seating` 超 10 分钟无 `Ballot` → 写 `Recess` → `round+1` 重新 HRW（候选集排除掉线者）→ 自动接管 |
| **同 index 双重认领** | blockHash 字典序小者胜出，另一个 rebase（§5） |
| **合格节点不足** | 降级执行，`Promulgation` 标注 `degraded=true, actualQuorum=1`，链上留痕，**不假装跑满了** |
| **claude 子进程失败** | 写 `Ballot` 且 `decision=error` + stderr 摘要，计入 quorum 分母但不计入多数决 |

## 9. 执行器：只有一个节点投递

**最容易翻车的地方。** quorum=3 意味着 3 个节点跑 `az-pr-review`，若 skill 每次都
发评论 + 投票，一个 PR 会收到 3 条重复评论和 3 次投票。

```csharp
Environment = { ["REVIEW_MODE"] = "collect" }   // 只产出，不投递
```

`~/.claude/skills/az-pr-review/SKILL.md` 里已有 **Dry run** 分支
（"if the user says 'just show me' / 'don't post'"），改成读 `REVIEW_MODE`
环境变量即可，改动很小。

只有 `round=0` 的节点在收齐 quorum 后投递，且**不再跑一次 Claude**——直接拿合并后的
findings 调 `az repos pr set-vote` + 发 comment thread。

## 10. Quorum 合并

```
decision = 多数决(ballots.decision)

findings 按 (file, line/10) 分组   // 容忍行号小偏差
  Confidence = 独立提到该 finding 的节点数 / 有效 ballot 数
  排序：Confidence 降序，然后 Severity 降序
```

`Confidence == 1.0` 的 finding 基本不用人工复核；`Confidence < 0.5` 的建议只作提示
不作阻塞。**这是引入 quorum 的全部回报。**

## 11. 进程模型

单进程，Generic Host 同时托管 UI 与后台任务：

```
Host.CreateApplicationBuilder()
  ├─ AddHostedService<DiscoveryService>()      // 轮询 AzDO
  ├─ AddHostedService<ReviewOrchestrator>()    // 认领、执行、投票
  └─ AddHostedService<MeshService>()           // P1 起：mDNS + gRPC
        ↓ host.RunAsync()  (后台线程)
AppBuilder.Configure<App>().StartWithClassicDesktopLifetime()   // UI 主线程
```

UI 与后台**通过 `ActaStore` 和 `NodeState` 两个 singleton 通信**，不直接互调。

## 12. 分层

```
Conclave.App              Avalonia 12 UI + Generic Host 组装
Conclave.Application      DiscoveryService / ReviewOrchestrator / QuorumEngine
                          + Ports（IPrSource / IReviewRunner / IActaStore）
Conclave.Domain           Revision / Block / Elector / Hrw / SeatAssignment
                          零依赖，全纯函数
Conclave.Infrastructure   SqliteActa / AzCliPrSource / ClaudeReviewRunner
```

依赖方向单向朝内。Domain 不引用任何包——因为它必须能在单元测试里毫无环境依赖地
验证「同样输入，不同节点算出同样席位」。

## 13. 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P0** | Avalonia 壳 + az 轮询 + 本地 Acta + 手动触发 review | ✅ 2026-09-08 |
| **P0.5** | `srcCommit` 幂等键跑通：PR 新 push 自动重跑，不 push 不重复烧 token | ✅ 2026-09-08 |
| **P1** | mDNS 发现 + gRPC 心跳，心跳带 `Repos/Projects/AzIdentity/RunningJobs` | ⬜ |
| **P2** | Block gossip + PullChain 补链 + 冲突解决 | ⬜ |
| **P3** | project 分片轮询 + 硬规则过滤 + 加权 HRW 认领 | ⬜ |
| **P4** | quorum 自适应 + findings 合并 + 单节点投递 | ⬜ |
| ~~P5~~ | ~~飞书 gateway~~ —— 主动轮询比被动等消息更完整，`bridge.sh` 只留「手动指定 PR 号插队」 | ❌ 砍掉 |

**P0.5 就已独立创造价值**：34 个 project 的活跃 PR 自动发现 + 去重，不漏不重。
这是现在的 `bridge.sh` 完全没有的能力。

### P0 / P0.5 的实测结论（2026-09-08）

- 一轮扫到 29 个活跃 PR 并落链，每个 PR 一条独立链，各自从 index 0 + 创世哈希起
- **二次运行 29 → 29，零新增召集** —— 幂等键按设计生效
- 私钥 `~/.conclave/elector.key` 权限 0600，区块签名逐块验证通过

尚未在运行时执行过的路径：`Seating` / `Ballot` / `Promulgation` / `Recess`。
它们有单元测试覆盖，但第一次真实执行要等 §9 的 skill 契约接上。

## 14. 安全约束

1. **节点白名单是 P1 的一部分，不是「以后再加」。** 别人的 job 会在你的机器上跑
   `Bash`。`Seating` 块必须验签且 `ElectorId` 在白名单内，否则拒绝 apply。
2. **`--disallowedTools "Bash"` 挡不住命令执行**——Claude 会用 Monitor/子代理绕过。
   要么锁全（含 Monitor/Agent/TaskCreate），要么按需正常放开。这条是踩过的坑。
3. **代码会离开本机。** node-B 评审 payment-center 意味着 node-B 上有完整代码。
   **只在本来就都有 repo 权限的同一团队内组网**，跨部门不要开。
4. **每个节点要有自己的 Claude 订阅**，不能共享账号。这是 mesh 规模的硬约束。
5. **私钥** `~/.conclave/elector.key`，权限 0600，已在 `.gitignore` 内。
   后续挪进 macOS Keychain。

## 15. 环境事实（实测）

| 项 | 值 |
|---|---|
| .NET SDK | 10.0.105 / ASP.NET Core 10.0.5 / **arm64 原生** |
| Avalonia | 12.1.2 |
| Claude Code | 2.1.263 |
| az CLI | 2.82.0 + azure-devops ext 1.0.5 |
| AzDO | **Server（自建）** `https://azdevops.liontravel.com/LionTechShanghai` |
| project 数 | 34 |
| Ed25519 | **.NET 10 无内置**（只有 MLDsa/SlhDsa）→ 用 ECDsa P-256 |
| AAD token 对 ADO Server | **不通**（TF400813）→ 走 az CLI，不自己实现 REST |
