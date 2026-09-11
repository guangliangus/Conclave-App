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
| ❌ 全局排序共识 | 账本确实是**一条全局链**（2026-09-08 改，见 §5），但同 index 冲突靠「取哈希字典序小者」让位重挂解决 —— 纯函数、确定性收敛，不需要共识协议 |
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

## 5. Acta：一条全局链

> **2026-09-08 变更。** 早期设计是「每个 PR 一条链」（单写者居多、索引冲突罕见）。
> 按需求改成**全局唯一一条链**，换来一条真正的全局时间线：谁在什么时候评审了什么，
> 按链序读一遍就是。
>
> **代价是并发写必然撞索引**：两个节点只要在听到彼此之前各写一块，就会争同一个 index。
> 让位重挂（§5 冲突解决）因此从异常路径变成常态路径。它是纯函数、确定性收敛，
> 功能上没问题，但代价要看得见 —— 账本记了 `index_conflicts` / `index_conflicts_lost`
> 两个计数，UI 的会议录页脚会显示。
>
> 「按 PR 查」不再靠 ChainId，靠 `revision_id` 冗余列 + 索引。

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

链的典型形态（一条链上交错着多个 PR）：

```
#0 Summons      2721@dc1d1d47  quorum=2   由发现节点写入
#1 Seating      2721@dc1d1d47  round=0  elector=A
#2 Summons      2946@476b95ce  quorum=1   ← 另一个 PR 插在中间
#3 Ballot       2721@dc1d1d47  round=0  A  reject  5 findings  128k token  $0.42
#4 Seating      2721@dc1d1d47  round=1  elector=B
#5 Ballot       2946@476b95ce  round=0  A  approve 0 findings   61k token  $0.19
#6 Ballot       2721@dc1d1d47  round=1  B  reject  3 findings   96k token  $0.31
#7 Promulgation 2721@dc1d1d47  reject  merged=6  threadId=8821
```

## 5.2 评审记录投影

链是唯一真相，但它不适合回答「这个月谁花了多少钱」。所以 Ballot 落链时同步写一层
可查投影：

```sql
reviews              -- 每票一行：who / when / status / findings / tokens / cost
review_model_usages  -- 分模型明细：opus 与 skill 里子代理用的小模型各花了多少
```

每一行都带 `block_hash` 指回来源区块 —— 任何时候都能从链上重建，也能验证没被改过。
**让位重挂会改块哈希，所以 rebase 之后必须整体重建投影**，否则报表里会留下指向
已不存在区块的幽灵行、金额重复计。

### 金额是折算，不是扣费

`claude -p` 报的 `costBasis` 通常是 `list`，即按 API 目录价折算；走 Max/Pro 订阅时
边际成本其实是 0。所以 `cost_basis` 必须一起入库，并在报表和 UI 上标出来 ——
否则「这个月花了 40 美元」会被读成账单。

token 分四类而不是简单的输入/输出：缓存读写的计价与新输入差一个量级，
混在一起就没法判断「是不是该把 review 的上下文做得更可缓存」。实测一次真实评审
缓存命中 92%，这个数就是优化空间的直接指标。

`cost_usd` 用 `REAL` 而不是全局规范里的 `numeric(10,2)`：单次评审常在 $0.001 量级，
两位小数会全部归零。

**落库**：SQLite，自增代理主键 + `(chain_id, block_index)` 唯一索引。append 前校验三件事——
签名有效、`ElectorId` 在白名单、`PrevHash` 等于本地链尾哈希。

### 冲突解决与 append-only 的边界

同 `(ChainId, Index)` 收到两个不同块时（网络分区合并后），取 **blockHash 字典序小**者胜出。
规则是纯函数，双方各自执行会得到同一结果，无需协商。

**「append-only」的准确说法是「无冲突时只追加」。** 让位必须真的删块，否则两条链不可能一致。
而且不能只删冲突那一块——它后面每一块的 `PrevHash` 都指向它，删掉就断链了，所以整段摘下来重挂。

重挂要改 `Index` 和 `PrevHash`，这两个字段在签名范围内，于是：

| 被挤下来的块 | 处理 |
|---|---|
| **本节点自己写的** | 改索引后重新签名，挂到新链尾 |
| **别人写的** | 丢弃 —— 我们没有它的私钥，签不回去；等其作者在自己那边做同样的 rebase 后重推 |

重挂出来的块**必须继续广播**（`ApplyResult.Rebased`）。否则对端不知道它们换了索引，
两边永远不会收敛——这一步漏了的话单测都是绿的，只有双节点跑起来才看得出。

整个过程在一个事务里：中途崩掉不能留下一条断了的链。

## 5.5 传输层：两处偏离本文档原方案

| 原方案 | 实际采用 | 理由 |
|---|---|---|
| mDNS / Bonjour | **UDP 多播信标**（`239.255.42.7:47707`） | 这里需要的只是「谁在线 + 它的 HTTP 端点」，不需要 DNS-SD 的服务/实例/TXT 那一整套。`Makaretu.Dns` 久未维护，自己发一个签名 JSON 约 120 行且零依赖，`nc -ul 47707` 就能看 |
| gRPC | **`HttpListener` 上的 HTTP/JSON** | 区块本来就是 JSON，上 gRPC 要多一个 `.proto` 并让它跟 `Block` record 保持同步；Kestrel 还要把 ASP.NET Core 框架引用拖进 Avalonia 进程、把 host 从 `HostApplicationBuilder` 改造成 `WebApplicationBuilder`。内网三个接口不值得 |

接口：`POST /blocks` 收区块、`GET /chains/{id}` 供对方补链、`GET /elector` 方便 curl 排查。

### 心跳必须签名

席位分配完全由心跳里的字段决定（有哪些 repo、哪些 project、当前负载），
伪造一个「空闲、什么 repo 都有」的心跳就能把席位全吸到自己名下然后永不出票——
那会让所有 PR 卡在弃权重试的循环里。所以 `Beacon` 带签名，收方验四件事：
签名有效、公钥指纹与自称 Id 一致、在白名单内、时间戳新鲜（挡重放）。

### gossip 只转发新块

转发前必须区分「刚落进来」和「本来就有」。对后者也转发会让区块在两个节点之间
**无限回弹**，每跳都新起 HTTP 请求——双节点实测时进程直接 `Out of memory` 崩了。
只转发新块也顺带解决了三节点以上的环路：转一圈回来时本地已经有了。

### 冷启动要先等成员表收敛

`DiscoveryService` 的第一轮轮询是立即执行的（do-while）。mesh 冷启动时谁都还没收到
别人的心跳，于是每个节点都以为所有 project 全归自己，同一批 PR 被所有节点各召集一遍，
在 index 0 上撞成一堆索引冲突。所以 mesh 开启时第一轮轮询前先等两个心跳周期。

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

每个新发现的 revision 会再花两次 API 调用取改动路径与文件数（见 §7.3），用来定 quorum。
只对**新**的 revision 花，幂等键命中就跳过 —— 所以稳态下每轮轮询只有 project 数那么多次调用。

## 7. 席位分配规则

硬规则做过滤，软规则做权重。两段都必须是**纯函数**——每个节点要独立算出同一结果。

### 7.1 硬规则（不满足直接出局）

```csharp
bool Eligible(Elector n, Revision rev, PrMeta pr) =>
    n.Projects.Contains(rev.Project)                // az 身份对该 project 有读权限
 && !AzIdentity.SamePerson(n.AzIdentity, pr.Author) // ★ 不 review 自己的 PR
 && n.RunningJobs < n.MaxConcurrent                 // 没打满
 && n.HasHeadroom                                   // ★ Claude 额度用量 < 80%
 && n.IsAlive(now);                                 // 心跳 90 秒内
```

「不 review 自己的 PR」是硬性的：`createdBy.uniqueName` 实测形如 `LIONMAIL\youngsun`，
节点心跳里带上自己的 `az` 登录身份做比对。

> **2026-09-09 变更：去掉了「本机有 clone」这一条。** 评审用的代码改为评审时拉进
> `~/.conclave/work/` 下的临时工作区、结束即删（见 §7.4），于是任何节点都能评任何仓库，
> 本机预先 clone 了什么与入席资格无关。`Projects` 保留 —— 它是 `az` 身份的读权限，
> 没权限连克隆和发评论都做不到，是真实约束而不是人为的本地性限制。
>
> `HasHeadroom` 是同时加进来的一条：Claude 额度用量超过 `Elector.MaxUtilization`（0.8）
> 的节点不再入席。它必须是硬规则而不只是权重 —— 权重再低也仍有概率中签，
> 而额度用尽的节点中签只会换来一张 Error 票。

### 7.2 软规则（加权 rendezvous 哈希）

```csharp
score(n) = Weight(n) / -ln(u(key, round, n))     取最大者

Weight(n) = 1 / (1 + n.RunningJobs)          // 负载均衡：正在跑的少拿
              / (1 + n.Reviews24h * 0.1)     // 长期公平：最近出票多的少拿
              * (1 - n.Utilization)          // 额度：快用完的少拿
```

用 HRW 而不是 `hash % count`：节点上下线时**只有该节点的份额会重排**，
其余分配保持稳定。

三个因子都是连乘（除掉，或乘一个 <1 的系数）而不是加权求和：求和要调一组量纲不同的
系数，连乘只要每个因子各自单调就行，**加一个新维度不必重新配平旧的**。

`(1 - Utilization)` 用乘法而不是 `1/(1+u)`，是为了在 `u → 1` 时趋于 0，跟 §7.3 的硬截断
同向收口 —— 越接近上限拿到的席位越少，到了上限直接出局，中间没有突变。合格节点的这个
因子恒 > 0.2，不会退化成零权重。

### Claude 额度用量从哪来

**跑 `claude -p "/usage"`。** 它在二进制里声明了 `supportsNonInteractive: true`，
是官方支持无头运行的本地命令；实测 `total_cost_usd=0`、`num_turns=0`、
`duration_api_ms=0`、墙钟约 0.8 秒 —— 纯本地，不打模型、不花额度、不需要任何配置。

```
Current session: 46% used · resets Sep 9 at 1:40pm (Asia/Shanghai)
Current week (all models): 40% used · resets Sep 14 at 2am (Asia/Shanghai)
Current week (Fable): 23% used · resets Sep 14 at 2am (Asia/Shanghai)
```

`ClaudeCliUsageProbe` 解析它，翻成 `five_hour` / `seven_day` / `seven_day:<模型>` 这套机器键
（显示名归展示层；这套词跟 statusLine JSON 与 `/api/oauth/usage` 一致，换来源不动界面），
自带 60 秒缓存。按模型细分的子额度只显示不判定 —— Fable 的周额度打满不妨碍用 Opus 评审。

### 每个窗口各有各的阈值，周额度当下只当硬顶

**不能对所有窗口用同一个 0.8。** 同一个阈值套在重置周期差 33 倍的两个窗口上，
含义完全不同：5h 到 80% 最多歇五小时、当天自愈；**7d 到 80% 要歇到本周重置，最长七天**。
而周额度是单调爬升的，周三爬到 80% 很常见，那时 5h 可能才 15% ——
这台机器本来还能评一整天，却被自己的规则关到周末。

**但也不能干脆不看周额度。** 周额度真到 100% 时是被真的拦住的，而此时消耗不了任何东西，
**5h 反而会读得很低**。只看 5h 会形成反馈陷阱：周额度耗尽 → 5h 掉到接近 0 →
规则认为这台最闲 → 加权 HRW 优先派活 → 每次评审都失败，一张张 Error 票。

所以 `UsagePressure` 给每个窗口各自的阈值，再取「离自己那条线最近」的那个：

```
pressure = max over gating windows of (utilization / gate)
上报 Utilization = pressure × Elector.MaxUtilization
```

| 窗口 | 阈值 |
|---|---|
| `five_hour` | `Elector.MaxUtilization`（0.8） |
| `seven_day` | **【临时】`WeeklyCeiling`（0.95），到线才算数，线以下一点压力都不贡献** |
| `seven_day:<模型>` | 不判定 |

**⚠️ 周额度的权重是临时摘掉的**（`UsagePressure.WeeklyCeilingOnly`）。摘之前它按工作日配速
判：`min(0.95, (已过工作日 + 1) / 总工作日)`，读作**一周的额度给五个工作日花，到第 N 个工作日
就该只花掉 N/5，允许超前一个工作日**。那条曲线仍然在（`UsagePressure.PaceGate`，照常有测试），
把那个布尔量改回 `false` 就恢复，`Tightest` 和界面会同步跟上。

摘的是配速，不是硬顶。0.95 那条必须留着：周额度真耗尽时 5h 反而读得很低，全摘掉就会掉进
「周额度耗尽 → 看起来最闲 → 优先派活 → 每次评审都失败」那个反馈陷阱。所以周额度现在是个
**二值判断** —— 95% 以下完全不参与（既不出局，也不通过 `Weight` 里的 `1 - 压力` 压低派活概率），
到 95% 就出局。

实测这台机器的周窗口正好是**周一 01:59 到周一 01:59**，五个工作日整。周几按本地时间判 ——
工作周是人的作息，不是 UTC 的。

关键性质：`SessionGate` 就取自 `Elector.MaxUtilization`，所以**会话窗口卡线时上报的数
恰好等于 5h 的原始读数**，「额度 57%」跟改造前是同一个意思。心跳里仍然只有一个标量，
`HasHeadroom` / `Weight` / `Beacon.SigningPayload` 一个字都不用改。

配速恢复之后还有个好副作用：被节流的节点**不用少烧一点就能自己走出来** —— 用量不动、
日子往前走，阈值自己涨上去。最早那版规则下那条曲线是平的，一旦越线只能等重置。

### 折算兜底不许盖掉真值

`ClaudeUsageMeter` 有两个来源，**真值优先**，折算只在真值拿不到时兜底 —— 早先这里取的是
「更紧的那个」，看着保守，实际上是唯一一次真把节点关在门外的原因：折算的分母
（`ClaudeUsageOptions.TokenBudget`，默认 4000 万 token / 7 天）是个没有出处的估计，
官方并没有公开 token 配额。实测这台机器 7 天出票 5285 万 token，折算 132% → 夹到 1.0 →
界面「额度满」，而同一时刻 Claude 自己报的是 5h 18% / 7d 76%，压力只有 64%。

折算照样算、照样写进 `Detail`（那是唯一能看出「本节点自己烧了多少」的数），只是不参与判定。

⚠️ 这些阈值是**节点间协议**，跟 `Elector.MaxUtilization` 同一性质：各机器算法不一致的话
上报的压力值就不可比，加权 HRW 会系统性偏袒某几台。所以是常量而不是配置项。

比较过的四条路：

| 途径 | headless | 官方百分比 | 要改全局配置 | 未公开 |
|---|---|---|---|---|
| 交互式会话的 statusLine（`rate_limits`） | ❌ | ✅ | **要** | 否 |
| `~/.claude/projects/**/*.jsonl` | ✅ | ❌ 只有分子 | 不用 | 否 |
| `GET /api/oauth/usage` | ✅ | ✅ | 不用 | **是**，还要读 keychain |
| **`claude -p "/usage"`** | ✅ | ✅ | **不用** | 否 |

三条排除路径都实测过：`claude -p` 不触发 statusLine（那是 TUI 组件）；在 `-p` 下确实会触发的
`Stop`/`SessionEnd` 钩子，其 JSON 里没有 `rate_limits`；`--output-format json` 的返回里只有
本次调用的 token 与金额。transcript 有精确的分子但**没有分母**（订阅上限官方不公开），
所以 ccusage 那一类工具的百分比都是拿历史最大值估的。

**代价是解析文本**，格式随版本可能变。所以宽松匹配、百分比与重置时刻分两级解析
（后者格式变了不影响前者，因为百分比才是席位规则要用的那个数）、认不出就返回空并说明原因。

### 兜底：按预算折算，有已知的严重偏差

真值拿不到时（找不到 `claude`、API key 用户、输出格式变了）按滚动窗口汇总本节点自己出过的
票，除以配置的预算。

⚠️ **它只看得见 Conclave 自己出的票。** 实测这台机器近 7 天真实用量在 45 亿 cache-read
token 量级，而 Conclave 自己只有 180 万 —— **低报约三个数量级**。所以它只是「真值彻底拿不到
时不至于完全没有数」，不能长期依赖；界面上标「（折算）」，日志与提示写明为什么没有真值。

| 项 | 归属 | 为什么 |
|---|---|---|
| 阈值 `MaxUtilization = 0.8` | **领域常量，全 mesh 一致** | 席位表要求所有节点算出同一结果。一台配 0.8、一台配 0.9，两边的合格节点集就不同，于是同一个 PR 被两个节点同时评审、或所有节点都以为该别人干。跟 `HeartbeatWindow` 同一性质：属于节点间协议，改它要全 mesh 同时升级 |
| 预算 `ClaudeUsage.TokenBudget` | **每台机器自己配** | 每个人订阅档位不一样。它只影响本节点报出去的 `Utilization`，不影响别人怎么解读它，所以不破坏一致性。默认值 4000 万 token / 7 天是个**待调的估计**，官方没有公开的 token 配额数字 |

全员额度都过线时席位表为空，PR 停在「已召集」不动 —— 这是想要的行为而不是故障。
日志与状态栏会写明用量、来源和**几点重置后自动恢复**（`resets_at` 的用处就在这里）。

### 7.3 quorum 自适应

不是所有 PR 都值得跑 3 遍：

| 条件 | 配置项 | 默认 | 三遍评审时 |
|---|---|---|---|
| `IsDraft` | — | 0（不评审） | 0 |
| 触及 `ReservedMatter`（支付/认证/密钥/对外 API 契约） | `Quorum.ReservedMatters` | 1 | 3 |
| `FilesChanged > Quorum.LargeChangeFiles`（20） | `Quorum.LargeChange` | 1 | 3 |
| `FilesChanged > Quorum.MediumChangeFiles`（5） | `Quorum.MediumChange` | 1 | 2 |
| 其余 | `Quorum.Default` | 1 | 1 |

> **2026-09-09 变更：默认改成「每个 PR 只评一次」。** 多节点独立评审能压掉 LLM 的方差
> （§10 那套合并逻辑完整保留），但那是成倍的额度 —— 所以默认不开，先把「不漏、不重复」
> 做好。想恢复原来的行为就把上表右列配上去。
>
> 多档同时命中取**最严**的那个，而不是取决于 `if` 的书写顺序。
>
> quorum 由发现节点算出来写进 Summons，之后所有节点读链上那个值 —— 策略配得不同不会让
> 席位表分叉，但会变成「谁发现决定跑几遍」，所以**策略应当全 mesh 一致**。
> 为了让分歧可见，策略指纹跟敏感路径清单一起写进 Summons（`RulesFingerprint`）。

> **2026-09-09 变更：档位从行数改成文件数。** 行数要靠本机 clone 跑一次
> `git diff --numstat` 才有，而评审用的代码现在是评审时才临时拉的 —— 为了给每个新发现的
> PR 定 quorum 就先拉一遍仓库，成本远大于收益。
>
> 改用 ADO 的 `pullRequestIterations` + `pullRequestIterationChanges` 两个接口，**零克隆**
> 拿到改动路径与文件数（`changeEntries[].item.path`，跳过 `isFolder`）。代价是
> **ADO 不提供行数**（实测确认，`diffs/commits` 也只给路径），所以行数那两档换成文件数档。
>
> 读不到统计时文件数留 0 → quorum 退到 1，是安全的降级方向：宁可少跑几遍，
> 不要因为读不到统计就把 PR 整个漏掉。

**敏感路径清单哈希后写进 `Summons` 块**，这样链上能证明当时用的是哪一版规则。

### 7.4 临时工作区：任何节点都能评任何仓库

评审前在 `~/.conclave/work/<revision>-r<round>-<rand>/` 现拉一份源分支，评完（含失败与超时）
立即删除。这换掉了原来「在本机已有的 clone 里跑」的做法，好处有三：任何节点都能评任何仓库、
不必预先 clone 34 个 project 的仓库、也不会动到本机正在用的工作副本
（早先直接在用户的 clone 里 `git fetch`，会碰到人家正在改的仓库）。

```
mkdir  ~/.conclave/work/2880@a6478fa5-r0-3f9c1a2b
git init && git remote add origin <repository.remoteUrl>
git fetch --no-tags --depth=50 origin <source> <target>
git merge-base origin/<target> origin/<source>     ← 必须校验！
git checkout -B <source> origin/<source>
… claude -p 在这个目录里跑 …
rm -rf ~/.conclave/work/2880@a6478fa5-r0-3f9c1a2b
```

三个实测结论决定了这套流程的形状：

1. **必须校验 merge-base。** `az-pr-review` 靠 `git diff origin/target...origin/source`
   取三点 diff。浅到共同祖先不在历史里时，`git merge-base` 退出码 1、输出为空，
   紧接着 `git diff a...b` 以**退出码 128** 报 `fatal: no merge base` —— 仓库白拉一遍、
   claude 白起一个，最后只落下一张 Error 票。所以拉完先校验，不够就 `--deepen`，
   再不够退到 `--unshallow`，真的没有共同祖先才明确失败。
2. **部分克隆在这台 ADO Server 上无效。** `--filter=blob:none` 只会打印
   `warning: filtering not recognized by server, ignoring` 然后退化成全量传输 ——
   省流量只能靠深度，不能靠 filter。
3. **`az repos pr list` 不给 `remoteUrl`**（返回 null，只有 `az repos pr show` 才给真值）。
   所以 `PrMeta.RemoteUrl` 刻意不是 `required`（否则老 Summons 块反序列化会直接抛），
   为空时按 `<组织地址>/<project>/_git/<repo>` 兜底拼出来，组织地址读
   `az devops configure --list` 的 `organization`。

**认证靠全局 git 凭据**（credential helper，或该 host 的 `http.<url>.extraheader`），
所以每台节点都要能在任意目录下免交互 `git clone` 那些仓库。子进程带
`GIT_TERMINAL_PROMPT=0` 起，凭据不对就明确失败，不会挂着等输入。

**残留清理**：正常路径上目录由 `IAsyncDisposable` 删掉，但进程被 kill、断电、`.app` 强退时
不会走到那里。工作区根目录整个是 Conclave 自己的，所以启动时整目录清一次
（这也是刻意不用系统临时目录的原因 —— 那里混着别人的东西，不敢整目录清）。

## 8. 异常处理

| 情况 | 处理 |
|---|---|
| **认领后节点挂了** | 任何节点见到 `Seating` 超 10 分钟无 `Ballot` → 写 `Recess` → `round+1` 重新 HRW（候选集排除掉线者）→ 自动接管 |
| **同 index 双重认领** | blockHash 字典序小者胜出，另一个 rebase（§5） |
| **合格节点不足** | 降级执行，`Promulgation` 标注 `degraded=true, actualQuorum=1`，链上留痕，**不假装跑满了** |
| **claude 子进程失败** | 写 `Ballot` 且 `decision=error` + stderr 摘要。**该轮算「烧掉」、换人重试**，不作为结论 |
| **重试到上限仍无有效票** | 拿手上的票收尾 —— 一个 `degraded` 的 `Error` 结论 |

### 8.1 一个 PR 同时只有一个节点在评

> **2026-09-09 新增。**

quorum=1 时席位表只有 round=0 一个人 —— 别的节点**没有席位可认**，机制上就插不进来，
不需要任何抢锁。只有两种情况让那一轮「烧掉」（`ChainState.SpentRounds`）、由下一轮接管：
超时（写 `Recess`）和执行失败（出 `Error` 票）。上限
`MaxReviewAttempts`（默认 3），到顶就拿手上的票收尾。

**Error 票的语义变了。** 以前它计入 quorum 分母，于是 quorum=1 时「claude 子进程挂了」
会直接被公布成一个 `Error` 结论 —— 明明只是这台机器上的一次偶发失败，换个节点重跑就好。
现在它只让出席位：`CanPromulgate` 的判据从「票数」改成「**有效**票数」。

### 8.2 作者 fix 之后仍由同一个节点复审

那个节点已经读过这份代码、提过这些 finding，复审的边际成本远低于换一个节点从头看；
对作者也是同一个「评审者」在跟进，而不是每次换一套意见。两层保证：

1. **HRW 种子按 PR 而不是 revision**（`Revision.SeatKey` = `seat:{project}:{prId}`）。
   作者 push 后 `revision.Id` 变了，种子不变，抽签天然落回同一节点。
2. **显式归属**：从 `reviews` 投影反查该 PR 上一次**有效**评审的节点，仍合格就直接坐
   round=0。光靠 HRW 不够 —— 权重随负载和额度漂移，两个节点分数接近时结果会翻。

归属只作用于 round=0：**重试轮次必须换人**，上一个已经失败过了。上一版是 Error 票的不算
归属 —— 那个节点当时没跑成，没有「读过这份代码」的优势，请回来只会重复同一个失败。

这是**投影表第一次参与行为决策**而不只是报表。之所以成立：投影完全由链推导（每行带
`block_hash`，让位重挂后整体重建），不是第二个真相来源。各节点在 gossip 收敛前可能算出
不同归属，跟 `extraRounds` 同一类瞬时不一致，收敛后自愈。

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

## 9.5 通知：结论要走到人眼前

投递回 PR 只解决了留痕。**没人会盯着 34 个 project 的 PR 列表刷新**，结论躺在 ADO 上
等于没出。所以公布之后再走一步：飞书私聊 PR 作者（`INotifier` / `LarkNotifier`）。

### 难点在 Azure DevOps 侧，不在飞书侧

飞书要的是 open_id 或邮箱，而 ADO 的 `createdBy` **两个都没有**：

```
uniqueName:  LIONMAIL\tobeyhuang     ← 域账号，不是邮箱
displayName: 黃偉
id:          0d66dd88-…               ← identity GUID
```

`IMS/Identities` 接口能给 `properties.Mail`，但实测这台 ADO Server 对 `az` 的认证方式
**直接返回 401**。可用的桥是 commits 接口——它给真邮箱，而且零克隆：

```bash
az devops invoke --area git --resource commits \
  --route-parameters project=<p> repositoryId=<id> --api-version 5.0
# → author.email = tobeyhuang@liontravel.com
```

实测本组织的形状就是 `AzIdentity.Normalize(uniqueName) + "@liontravel.com"`，所以
主路径直接拼（`LarkOptions.EmailDomain`），拼不对的人用 `UserMap` 单独兜。commits 接口
留作核对手段，不进热路径——为一条通知多打一次 az 不值。

### 实测：只有 open_id 能发

| 收件人来源 | 需要的权限 | 实测 |
|---|---|---|
| `UserMap` 里写死 `ou_…` | 只要 `im:message` | ✅ 通（`message_id` 已返回） |
| 邮箱 → open_id → 发 | 加 `contact:user.id:readonly` | 未验（scope 还没开） |
| `receive_id_type=email` 直投 | 需要应用能看到邮箱字段 | ❌ 一律 99992402 |

第三条曾被当成「零权限的那条路」，**是错的**。应用读得到通讯录记录
（`contact:user.base:readonly` 有），但**记录里连 `email` 键都没有**——邮箱归
`contact:user.email:readonly` 管，被剥掉了，于是收件人根本解析不出来。同一个 body
换成 open_id 立刻成功。

99992402 的坑在于它跟「这个人不存在」共用一个码，单看没有信息量。早期就是靠「拿不存在的
邮箱去探，看它报的是 scope 错误还是参数错误」来推断权限，而那个推断链在这里断了：
scope 检查确实先于参数校验，但邮箱路线过得了 scope 检查、死在解析上。所以
`LarkNotifier.BuildSendError` 在这个组合上专门补一句指路，不让下一个人再反推一遍。

99991672（scope 未申请）在一个进程生命周期内不会变——开权限要去开发者后台，改完得重启
节点——所以**只探一次**，之后不再为每条通知白打一个请求。

### 不用群机器人 webhook

webhook 零权限、零配置，但只能往群里发。一个 PR 的结论刷进公共群，看的人比该看的人多
得多，而这套将来是要 34 个 project 全开的。

### 通知失败绝不影响公布

跟 §8 里投递失败同样处理：结论已经在链上，补发的成本远低于让编排循环带着异常退出。
`PromulgateAsync` 吞掉通知的异常，只留一条 Error 日志。

## 10. Quorum 合并

```
decision = 多数决(ballots.decision)

findings 贪心聚类：同文件、行号在锚点 ±5 内、且该簇尚未收过这一轮的 finding
  Confidence = 独立提到该 finding 的节点数 / 有效 ballot 数
  排序：Confidence 降序，然后 Severity 降序
```

「**尚未收过这一轮**」这条约束不能省。同一个节点报的两条 finding 必然是两个不同的问题，
哪怕挨得很近。首次真实评审就撞上了：一票 4 条 finding，行号 9 / 13 / 17 / 13，
早期实现按 `line / 10` 分桶，把 13、17、13 全塞进同一个桶，**4 条真实问题只剩 2 条**。

顺带把分桶换成相对锚点的对称容差 —— 分桶的边界是任意的：9 和 13 差 4 却分属不同桶，
13 和 17 差 4 却同桶。

`Confidence == 1.0` 的 finding 基本不用人工复核；`Confidence < 0.5` 的建议只作提示
不作阻塞。**这是引入 quorum 的全部回报** —— 但只有 quorum ≥ 2 才拿得到。默认
quorum=1 时每条 finding 的 Confidence 都是 1.0，这一层等于直通；逻辑保留，配一下就能打开。

合并器只接**有效票**。Error 票已经在 §8.1 里让出过席位，再计入分母会让 `actualQuorum`
把失败次数也算成「评审次数」。

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

### 菜单栏常驻，不是窗口应用

启动**不开窗口**，只在菜单栏挂一个图标。三件事必须一起做，少一件就会「面板关了再也
叫不回来」：

| 做法 | 少了会怎样 |
|---|---|
| `ShutdownMode.OnExplicitShutdown` | 默认 `OnLastWindowClose`，关窗整个进程退，后台跟着死 |
| 不设 `desktop.MainWindow` | 设了启动就会被 `Show()`；改成第一次点图标才建窗口 |
| 关窗只隐藏 | 不进 Dock 之后，真关掉的窗口没有别的入口能打开 |

隐含第四条：accessory 应用没有自己的菜单栏，**`Cmd+Q` 失效**，而点图标直接出主面板、
没有右键菜单 —— 所以退出的唯一入口是面板顶栏上的按钮。

### 只有一个窗口，点图标直接出

原先是两层：图标点出一个 440 宽的浮层（`PanelWindow`），要在它的页脚再点一下「任务池」
才到 `MainWindow` 那张表。浮层装的东西 —— 状态、今日/累计、额度条、两个开关 ——
主面板的顶栏与指标条里全都有，所以那一跳只是多一次点击，浮层已经退役。

图标是个 toggle：没开就开，开着就关。「开着就关」不能只看 `IsVisible` —— 面板失焦即
隐藏，而点图标本身就会让它失焦，走到点击处理里时窗口早就不可见了。判据是
`_hiddenAt` 落在 350ms 内：这次隐藏就是这一下点击造成的，那就当成 toggle 的关闭。

**失焦即隐藏有一个例外**：行内动作菜单（「⋯」）开着时不收。弹出菜单会把焦点从窗口
拿走，不挡一下的话点开菜单的那一刻面板就连着菜单一起消失，四个动作永远点不到。
`MainWindow` 用 `MenuFlyout.Opened/Closed` 记一个计数，`App` 在 `Deactivated` 里查它。

定位只在建窗口时算一次，之后人自己挪过的位置不会被悄悄改回去。

### 没有系统标题栏

`WindowDecorations="None"`（Avalonia 12 里 `SystemDecorations` 已过时）：那三个红黄绿
按钮在一个失焦即隐藏、且只能从菜单栏图标打开的窗口上没有意义 —— 最小化之后没有 Dock
图标能把它叫回来，而「关闭」跟「切走」现在是同一件事。

代价是两件事得自己做：

- **圆角与边框**：窗口本体透明（`TransparencyLevelHint="Transparent"`），最外层
  `Border.shell` 画 12px 圆角、1px 边框和底色。不画的话四个角是直角实色块。
- **拖动**：顶栏 `Border` 接管 `PointerPressed` → `BeginMoveDrag`。只在 `e.Handled`
  为 false 时开始拖，所以顶栏上的两个开关和两个按钮不会被误当成拖把手。

### 点击必须自己实现

Avalonia 的 `TrayIcon` 在 macOS 上**收不到点击**。这不是文档过时——`nm` 看
`libAvaloniaNative.dylib`，`AvnTrayIcon` 一共只导出五个方法：

```
SetIcon  SetMenu  SetToolTipText  SetIsVisible  SetIsTemplateIcon
```

native ABI 里没有任何回调入口，`TrayIcon.Clicked` 与 `Command` 走的都是同一条
`ITrayIconImpl.OnClicked`，在 macOS 上永远不会被触发。所以要「点图标直接出面板」
只能绕过它，自己用 Objective-C runtime 建 `NSStatusItem`
（`Conclave.App.Interop.MacStatusItem`）：运行时 `objc_allocateClassPair` 注册一个
target 类，`class_addMethod` 挂上回调，再 `setTarget:` / `setAction:`。

**这层没有编译期保障。** 选择器名打错、方法签名跟 ABI 不符，都只在运行时表现为
「点了没反应」或直接 crash，而它又恰好是唯一没法用单元测试覆盖的部分（要主线程 + AppKit）。
所以有 `conclave tray-selftest`：`performClick:` 走的正是真人点击的同一条 target/action
通路，能盖住除「肉眼看图标」以外的全部。Avalonia / macOS 升级后跑一次。

两处刻意的取舍：

- **用 `[NSEvent mouseLocation]` 而不是 `[[button window] frame]`** 取锚点。后者是
  `NSRect`，32 字节，x86_64 上必须走 `objc_msgSend_stret`、arm64 上走普通
  `objc_msgSend` —— 一个没法在两种架构上都验证的分歧。`NSPoint` 只有 16 字节，两种 ABI
  都按寄存器返回，没这个坑。而点击那一刻鼠标就在图标上，所以它同时回答了
  「图标在哪」和「点在哪块屏」。
- **用 `DllImport` 而不是 `LibraryImport`**。后者要求整个项目开 `AllowUnsafeBlocks`，
  为一个互操作文件给整个 UI 项目放开 unsafe 不值；这些签名全是 blittable 的指针和 double，
  运行时 marshalling 与生成代码等价。

### 评审中的图标在呼吸

空闲闭眼、评审中睁眼，而睁着的那只眼还一秒一张一合 —— 一眼就能看出这台机器在干活。
macOS 没有「会动的状态栏图标」：`NSStatusItem` 只认一张 `NSImage`（animated GIF 塞进去
也不会动，`NSStatusBarButton` 只画静态帧），所以动画只能是定时换图（`TrayAnimator`）。

| 取舍 | 为什么 |
|---|---|
| 定时换图，不用 `CABasicAnimation` | layer 动画更省（CoreAnimation 跑，主线程零开销），但要从 P/Invoke 过 `CATransform3D` 的结构体 ABI —— 正是这层最难验的东西。换图只用已经验过的签名 |
| `NSImage` 按路径缓存在 `MacStatusItem` 里 | 原先每次换图都 `initWithContentsOfFile:` 重读。几分钟一次无所谓，12fps 就是每 80ms 一次读盘加一次 native alloc/release ——「per-tick 建 native 对象」正是字体那次泄出 40GB 的形状 |
| 定时器用 `DispatcherPriority.Normal`，不是 `Background` | `Background` 排在 Input/Render 后面，UI 一忙就被饿着。实测（`tray-selftest` 会打拍数）主面板正在建的那 280ms 里 12fps 的定时器只响了 1 拍，看起来就是「评审一开始图标先卡住」。一拍的活就是一次 `setImage:`，抢不走任何东西 |
| 空闲时定时器是停的 | 常驻动画会一直唤醒主线程，而菜单栏被全屏窗口盖住时还照跑。只在评审中动，回到空闲立刻停、落回静态那张 |
| 渐变做在 alpha 上，不在颜色上 | 模板图的 RGB 会被 macOS 整个丢掉（只拿 alpha 当蒙版按深浅色染色），所以彩色渐变在菜单栏上不成立；alpha 是被尊重的，于是做成纵向渐隐（上实下虚 1.0→0.65），菜单栏用它自己的颜色画出浓淡。要真彩就得 `setTemplate:NO` 并自己接管深浅色（两套帧 + 订 `AppleInterfaceThemeChangedNotification`），为一道 20px 高的渐变不值 |

帧是 `scripts/make-icon.py` 从同一份几何渲的半个周期（全睁 → 最眯 7 张），回程倒着放，
一轮 12 拍。为什么是 7 张、为什么最眯只到 0.45，见 `docs/logo/README.md`。

自检里加了一条：`tray-selftest` 会真的让动画跑几拍再看 `Ticks`。「帧都读得出来」跟
「动画在动」是两回事 —— 定时器没 Start、优先级建错、回调里静默抛异常，三种都表现为
图标停在第一帧，而那跟静态图标肉眼分不出来。

### 多屏：面板必须出现在点击的那块屏上

两套坐标系要对齐，而它们的差别不只是原点：

| | 单位 | 原点 | Y 方向 |
|---|---|---|---|
| Cocoa 全局 | pt | **主屏左下** | 向上 |
| Avalonia `Screen.Bounds` / `Window.Position` | 见下 | **主屏左上** | 向下 |

**实测（`conclave tray-selftest` 会把这些数打出来）：Avalonia 在 macOS 上报的是 pt 而不是
px。** 内置屏物理 3024x1964，它报 `1512x982` 且 `Scaling = 1`。所以换算只是一次 Y 翻转：

```
avaloniaX = cocoaX
avaloniaY = 主屏Bounds.Height - cocoaY      // 主屏 Bounds.Y 恒为 0
```

排在主屏上方的副屏，`Bounds.Y` 是负数（实测 `-350, -1080, 1920, 1080`），翻转后自然落进
那个负区间，`Screens.ScreenFromPoint` 就能认出来。

纵向不自己减菜单栏高度 —— `Screen.WorkingArea` **已经按屏**排除了菜单栏与 Dock
（实测主屏 33、副屏 30：内置屏有刘海，两块屏的菜单栏高度本来就不一样），写死一个高度反而会错。

真人点击一次只落在一块屏上，所以自检里还有一步**合成检查**：按每块已连接屏菜单栏上的
一个点反算出 Cocoa 坐标，再正着走一遍变换，看它是否回到同一块屏。
「多屏时面板跑到主屏去了」这个 bug 靠真实鼠标位置是验不出来的。

## 12. 分层

```
Conclave.App              Avalonia 12 UI + Generic Host 组装
Conclave.Application      DiscoveryService / ReviewOrchestrator / QuorumEngine
                          + Ports（IPrSource / IReviewRunner / IActaStore / IUsageMeter）
Conclave.Domain           Revision / Block / Elector / Hrw / SeatAssignment
                          零依赖，全纯函数
Conclave.Infrastructure   SqliteActa / AzCliPrSource / ClaudeReviewRunner
                          GitWorkspace（临时工作区）/ ClaudeUsageMeter（额度记账）
```

依赖方向单向朝内。Domain 不引用任何包——因为它必须能在单元测试里毫无环境依赖地
验证「同样输入，不同节点算出同样席位」。

## 13. 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P0** | Avalonia 壳 + az 轮询 + 本地 Acta + 手动触发 review | ✅ 2026-09-08 |
| **P0.5** | `srcCommit` 幂等键跑通：PR 新 push 自动重跑，不 push 不重复烧 token | ✅ 2026-09-08 |
| **P1** | mesh 发现 + 签名心跳，心跳带 `Repos/Projects/AzIdentity/RunningJobs` | ✅ 2026-09-08 |
| **P2** | Block gossip + 补链 + 索引冲突让位重挂 | ✅ 2026-09-08 |
| **P3** | project 分片轮询 + 硬规则过滤 + 加权 HRW 认领 | ⬜ |
| **P4** | quorum 自适应 + findings 合并 + 单节点投递 | ⬜ |
| ~~P5~~ | ~~飞书 gateway~~ —— 主动轮询比被动等消息更完整，`bridge.sh` 只留「手动指定 PR 号插队」 | ❌ 砍掉 |

**P0.5 就已独立创造价值**：34 个 project 的活跃 PR 自动发现 + 去重，不漏不重。
这是现在的 `bridge.sh` 完全没有的能力。

### P0 / P0.5 的实测结论（2026-09-08）

- 一轮扫到 29 个活跃 PR 并落链，每个 PR 一条独立链，各自从 index 0 + 创世哈希起（**当时**的形态；同日改成了一条全局链，见 §5）
- **二次运行 29 → 29，零新增召集** —— 幂等键按设计生效
- 私钥 `~/.conclave/elector.key` 权限 0600，区块签名逐块验证通过

尚未在运行时执行过的路径：`Seating` / `Ballot` / `Promulgation` / `Recess`。
它们有单元测试覆盖，但第一次真实执行要等 §9 的 skill 契约接上。

## 14. 安全约束

1. **节点白名单已随 P1 上线。** 别人的 job 会在你的机器上跑 `Bash`。名单是
   `~/.conclave/electors.allow`（每行一个 elector 指纹，`#` 注释，按 mtime 热重载）；
   文件不存在 = 只信任自己 = 单机模式。区块与心跳都要过这道闸，且**白名单检查在冲突判定之前**——
   否则外人只要造一个哈希更小的块就能改写别人的链。
2. **`--disallowedTools "Bash"` 挡不住命令执行**——Claude 会用 Monitor/子代理绕过。
   要么锁全（含 Monitor/Agent/TaskCreate），要么按需正常放开。这条是踩过的坑。
3. **代码会离开本机。** node-B 评审 payment-center 意味着 node-B 上出现过 payment-center
   的完整代码 —— 现在是评审时现拉、评完即删，但期间它确实落在对方磁盘上，而且
   **任何节点都能被派到任何仓库**（去掉「本机有 clone」之后，能评的范围只由 `az` 读权限决定）。
   所以**只在本来就都有 repo 权限的同一团队内组网**，跨部门不要开。
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
| ADO `createdBy` 里的邮箱 | **没有**；`IMS/Identities` 对 az 的认证 401 → 走 git commits 接口拿 `author.email` |
| 飞书 app `cli_a952edf34c38de17` | `im:message` + `contact:user.base:readonly` **已开**；`contact:user.id:readonly` / `user.email:readonly` **未开** |
| 飞书按 open_id 私聊 | ✅ 实测通 |
| 飞书 `receive_id_type=email` | ❌ 99992402 —— 应用看不到通讯录的 email 字段，收件人解析不出来 |
| Avalonia `TrayIcon` 点击（macOS） | ❌ native ABI 无回调入口 → 自建 NSStatusItem，见 §11 |
