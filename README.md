# Conclave

> **con·clave** /ˈkɒŋkleɪv/ — 拉丁语 *cum clave*「以钥匙锁闭」。
> 闭门独立评议、投票形成决议、决议封存可查证的会议。

分布式 AI code review。每台接入的机器都是对等节点（**Elector**），主动轮询 Azure DevOps
上的活跃 PR，按确定性规则分配评审席位，起 Claude Code 产出结论，全过程记录在一条签名
哈希链账本（**Acta**）上。

**默认一个 PR 只由一个节点评审**，作者 push 修复后仍由同一个节点复审；只有那个节点超时
或执行失败时，席位才让给下一个节点。多节点独立评审 + 多数决合并的逻辑完整保留，
配一下就能打开（见「② 召集」）。

没有服务端、没有调度器、没有共识算法 —— 席位分配是纯函数，每个节点各自算出**同一张
席位表**，所以「谁来评审哪个 PR」不需要任何协商消息。

上手与日常使用（面向使用者，给同事看这份）：**[docs/USAGE.md](docs/USAGE.md)**

设计文档（含每处取舍的理由）：**[docs/DESIGN.md](docs/DESIGN.md)**

## 它解决什么问题

现状是一个飞书机器人桥接：人肉把 PR 号发给机器人，机器人在单台机器上跑
`claude -p "/az-pr-review <PR号>"`。三个痛点：

| 痛点 | Conclave 的解法 |
|---|---|
| 同一个飞书应用**不能在多台机器同时订阅事件**（事件被随机分流） | 不依赖飞书事件。每个节点**主动轮询 AzDO**，谁都能发现 PR |
| 要人肉发 PR 号，漏掉的 PR 就是漏掉了 | 轮询 34 个 project 的活跃 PR，**自动发现、自动去重** |
| 单台机器既是瓶颈也是单点 | 评审工作按规则分散到 mesh 里所有合格节点 |

额外拿到的东西，也是引入 quorum 的**唯一**理由：多节点独立评审同一个 PR 能压掉 LLM 的
输出方差 —— 被 3 个节点独立提到的 finding 几乎必然是真问题，只被 1 个提到的大概率是噪音。

## 一次评审是怎么走完的

```
                 ┌────────────────── 每 60 秒一轮 ──────────────────┐
                 ↓                                                  │
  ① az repos pr list --status active        只轮询 HRW 分给自己的 project
                 ↓
  ② 幂等键 (prId, srcCommit) 已在链上？ ─── 是 ──→ 跳过，不重复烧 token
                 ↓ 否
     ADO pullRequestIterationChanges  →  quorum = 1 / 2 / 3（零克隆拿改动路径与文件数）
                 ↓
     ┌─ Summons ─────┐  写入 Acta 并 gossip 给邻居
                 ↓
  ③ SeatAssignment.Seats(...)   纯函数，各节点独立算、结果一致
                 ↓ 算到自己（额度 <80%、不是 PR 作者；上一版评审过的优先）
     ┌─ Seating ─────┐   ← quorum=1 时只有这一个席位，别的节点没得认
                 ↓
  ④ 在 ~/.conclave/work/ 现拉一份源分支（评完即删）
     REVIEW_MODE=collect  claude -p "/az-pr-review <id>"   ← 只产出，不投递
                 ↓ 解析回复末尾的 JSON 契约块
     ┌─ Ballot ──────┐  decision + findings + token/金额，同时写入评审记录投影
                 ↓
  ⑤ 有效票数 ≥ quorum ？（Error 票只让出席位，不算结论）
                 ↓ 否 → 换下一个节点重试，最多 MaxReviewAttempts 次
                 ↓ 是（且本节点持最低的未烧掉席位）
  ⑥ QuorumEngine.Merge   多数决 + findings 贪心聚类，算出 Confidence
                 ↓
     ┌─ Promulgation ┐ ──→ az：发一条评论 + set-vote（整个 PR 只做一次）
```

### ① 发现：幂等键刻意不是 PR ID

整个设计最容易做错的地方。用 PR ID 当 key，作者 push 新 commit 后就不会重评；每轮轮询都
重跑，又会重复烧 token。所以最小评审单元是 **PR 的某一个版本**：

```csharp
public sealed record Revision(string Project, int PrId, string SrcCommit)
{
    public string Id => $"{PrId}@{ShortCommit}";   // 2721@dc1d1d47
}
```

`SrcCommit` 取 `lastMergeSourceCommit.commitId`。链上有该 `Revision` 的 Summons 就跳过；
作者一 push，`SrcCommit` 变化，自然成为一个新 `Revision` 并触发新一轮。
**不需要任何「是否已处理」状态表，Acta 本身就是。**

34 个 project 也不是每个节点都全轮一遍 —— project 按 HRW 分片（`discover:<project>`），
5 节点 / 34 project 约 7 个/节点，节点掉线时 HRW 自动把它的份额重分给别人，**没有故障切换代码**。

每个**新**的 revision 会再花两次 ADO API 调用取改动统计（定 quorum 用）。只对新的花 ——
幂等键命中就跳过，所以稳态下每轮轮询只有 project 数那么多次调用。

### ② 召集：quorum 自适应

不是所有 PR 都值得跑 3 遍：

**默认所有 PR 都只评一次。** 多节点独立评审能压掉 LLM 的输出方差，但那是成倍的额度 ——
所以合并逻辑完整保留、随时可以打开，默认不开：先把「不漏、不重复」做好，方差是第二优先。

| 条件 | 配置项 | 默认 | 恢复三遍评审时 |
|---|---|---|---|
| `IsDraft` | — | 0（不评审；手动插队时除外） | 0 |
| 命中 `ReservedMatters`（支付 / 认证 / 密钥 / 对外 API 契约 / 迁移脚本…） | `Quorum.ReservedMatters` | 1 | 3 |
| `FilesChanged > Quorum.LargeChangeFiles`（20） | `Quorum.LargeChange` | 1 | 3 |
| `FilesChanged > Quorum.MediumChangeFiles`（5） | `Quorum.MediumChange` | 1 | 2 |
| 其余 | `Quorum.Default` | 1 | 1 |

多档同时命中时取**最严**的那个（25 个文件又碰了支付路径 → 按 3 跑），而不是取决于 `if`
的书写顺序。

quorum 由**发现节点**算出来写进 Summons，之后所有节点都读链上那个值 —— 所以策略配得不同
不会让席位表分叉，但会变成「同一个 PR 由谁发现，决定了它跑几遍」，那种不确定性没有意义，
**策略应当全 mesh 保持一致**。为了让分歧至少可见，策略指纹跟敏感路径清单一起写进 Summons。

改动统计走 ADO 的 `pullRequestIterations` + `pullRequestIterationChanges`，**零克隆**
（取最后一个 iteration，读 `changeEntries[].item.path`，跳过 `isFolder`）。
档位只看文件数而不看行数：行数要靠本机 clone 跑 `git diff --numstat` 才有，而评审用的代码
现在是评审时才临时拉的 —— 为了定 quorum 就先拉一遍仓库，成本远大于收益，而
**ADO 不提供行数**（实测确认）。读不到统计时文件数留 0 → quorum 退到 1，是安全的降级方向。

敏感路径清单的**哈希写进 Summons 块**，半年后回看「这个 PR 为什么只跑了 1 个节点」时不必猜配置。

### ③ 入席：硬规则过滤 + 加权 HRW

两段都必须是纯函数，否则各节点会算出不同的席位表 —— 表现为重复评审或集体旁观，且不报错。

```csharp
// 硬规则：任一条不满足即出局
   elector.HasProject(pr.Project)                 // az 身份对该 project 有读权限
&& !AzIdentity.SamePerson(elector.AzIdentity, pr.Author)   // ★ 不 review 自己的 PR
&& elector.RunningJobs < elector.MaxConcurrent    // 没打满
&& elector.HasHeadroom                            // ★ Claude 额度用量 < 80%
&& elector.IsAlive(now)                           // 心跳 90 秒内

// 软规则：加权 rendezvous 哈希，取分数最大者
score(n) = Weight(n) / -ln(u(revisionId, round, n.Id))

Weight(n) = 1 / (1 + RunningJobs)        // 负载均衡：正在跑的少拿
              / (1 + Reviews24h * 0.1)   // 长期公平：最近出票多的少拿
              * (1 - Utilization)        // 额度：快用完的少拿
```

**没有「本机有 clone」这一条了。** 代码是评审时拉进临时工作区的（见 ④），任何节点都能评
任何仓库。`HasProject` 保留 —— 它是 `az` 身份的读权限，没权限连克隆和发评论都做不到。

#### 一个 PR 同时只有一个节点在评

quorum=1 时席位表就只有 round=0 一个人 —— 别的节点**没有席位可认**，机制上就插不进来，
不需要任何「抢锁」。只有两种情况会让那一轮「烧掉」、由下一轮的节点接管：

| 情况 | 怎么发现 | 结果 |
|---|---|---|
| **超时** | 任何节点见到 `Seating` 超 `SeatingTimeout`（10 分钟）还没出票 | 写 `Recess`，round+1 换人 |
| **执行失败** | 那个节点自己出一张 `Error` 票（子进程挂了 / 契约解析不到 / 工作区拉不下来） | 该轮算烧掉，round+1 换人 |

两者合起来叫「已烧掉的轮次」（`SpentRounds`），上限 `MaxReviewAttempts`（默认 3）；到顶
就拿手上的票收尾，否则「claude 每次都起不来」会让同一个 PR 无限重试下去。

> **改造前 Error 票是计入 quorum 分母的**，于是 quorum=1 时「claude 子进程挂了」会直接
> 被公布成一个 `Error` 结论 —— 明明只是这台机器上的一次偶发失败，换个节点重跑就好。
> 现在它只让出席位，不再是结论。

#### 作者 fix 之后仍由同一个节点复审

那个节点已经读过这份代码、提过这些 finding，复审的边际成本远低于换一个节点从头看；
对作者来说也是同一个「评审者」在跟进，而不是每次换一套意见。两层保证：

1. **HRW 的种子是 PR 而不是 revision**（`Revision.SeatKey` = `seat:{project}:{prId}`）。
   作者 push 后 `revision.Id` 变了，但种子不变，所以抽签天然落回同一个节点。
2. **显式归属**：从 `reviews` 投影反查这个 PR 上一次**有效**评审的节点，只要它仍然合格
   就直接坐 round=0。光靠 HRW 还不够 —— 权重会随负载和额度漂移，两个节点分数接近时结果会翻。

归属只作用于 round=0：**重试轮次必须换人**，上一个已经失败过了。上一版是 Error 票的
也不算归属 —— 那个节点当时根本没跑成，没有「读过这份代码」的优势，请回来只会重复同一个失败。
`StickyReviewer: false` 可以关掉第 2 层（第 1 层仍在，所以通常还是同一个节点）。

三个权重因子都是连乘而不是加权求和：求和要调一组量纲不同的系数，连乘只要每个因子各自
单调就行，**加一个新维度不必重新配平旧的**。`(1 - Utilization)` 用乘法是为了在 `u → 1` 时
趋于 0，跟 80% 的硬截断同向收口 —— 越接近上限拿到的席位越少，到了上限直接出局，
中间没有突变。

用 HRW 而不是 `hash % count`：节点上下线时只有该节点那一份会重排，其余分配保持稳定。
「不评审自己的 PR」这条要注意实测形态 —— `createdBy.uniqueName` 形如 `LIONMAIL\youngsun`，
而 az 登录身份可能是 `youngsun@liontravel.com` 或裸 `youngsun`，`AzIdentity.Normalize`
把三种都归一。

席位表的第 N 个元素就是 round=N 的评审者。前 `quorum` 轮从「尚未入席的合格节点」里取，
所以正式席位互不重复；合格节点不足时席位表短于 quorum，公布时在链上标 `degraded=true`，
**不假装跑满了**。

#### Claude 额度用量：跑 `claude -p "/usage"` 直接拿

`/usage` 在二进制里声明了 `supportsNonInteractive: true`，是官方支持无头运行的**本地命令**。
实测跑一次：

```
total_cost_usd : 0
num_turns      : 0
duration_api_ms: 0      ← 纯本地，不打模型
duration_ms    : 829
```

**零成本、0.8 秒、不需要任何配置。** 输出：

```
Current session: 46% used · resets Sep 9 at 1:40pm (Asia/Shanghai)
Current week (all models): 40% used · resets Sep 14 at 2am (Asia/Shanghai)
Current week (Fable): 23% used · resets Sep 14 at 2am (Asia/Shanghai)
```

`ClaudeCliUsageProbe` 解析它，翻成 `five_hour` / `seven_day` / `seven_day:<模型>` 这套机器键
（跟 statusLine JSON 和 `/api/oauth/usage` 用的是同一套词，将来换来源不用动界面），
自带 **60 秒缓存**（面板每 30 秒刷、发现循环每 60 秒一轮，共用一份，不会每次都 fork）。

**取参与判定的窗口里的最大值** —— 任一窗口打满都会被限流。按模型细分的子额度
（`seven_day:Fable`）**只显示、不参与判定**：Fable 的周额度打满不妨碍用 Opus 评审。

比较过的四条路，这条最合适：

| 途径 | headless | 官方百分比 | 要改全局配置 | 成本 | 未公开 |
|---|---|---|---|---|---|
| 交互式会话的 statusLine | ❌ 不触发 | ✅ | **要** | 0 | 否 |
| `~/.claude/projects/**/*.jsonl` | ✅ | ❌ 只有分子 | 不用 | 0 | 否 |
| `GET /api/oauth/usage` | ✅ | ✅ | 不用 | 0 | **是**，还要读 keychain |
| **`claude -p "/usage"`** | ✅ | ✅ | **不用** | **$0 / 0.8s** | 否 |

（`claude -p` 不触发 statusLine，在 `-p` 下会触发的 `Stop`/`SessionEnd` 钩子其 JSON 里也没有
`rate_limits`，`--output-format json` 的返回里只有本次调用的 token 与金额 —— 三条都实测过。）

**唯一代价是解析文本**，格式随版本可能变。所以：宽松匹配、百分比和重置时刻分两级解析
（后者格式变了不影响前者）、认不出就返回空并说明原因，由折算兜底 —— **绝不猜一个数字出来**。

#### 兜底：按预算折算（有已知的严重偏差）

真值拿不到时（找不到 `claude`、API key 用户没有订阅额度、输出格式变了）回落到按滚动窗口
汇总本节点自己出过的票除以 `ClaudeUsage.TokenBudget`。

⚠️ **它只看得见 Conclave 自己出的票。** 实测这台机器近 7 天真实用量在 45 亿 cache-read
token 量级，而 Conclave 自己只有 180 万 —— **低报约三个数量级**。所以它只是「真值彻底拿不到
时不至于完全没有数」，不是可以长期依赖的额度判断。界面上会标「（折算）」，
日志与提示里会写明为什么没有真值。

| 项 | 归属 | 为什么 |
|---|---|---|
| 阈值 **80%** | **领域常量，全 mesh 一致** | 席位表要求所有节点算出同一结果。一台配 0.8、一台配 0.9，两边的合格节点集就不同 —— 于是同一个 PR 被两个节点同时评审，或所有节点都以为该别人干。跟 `HeartbeatWindow` 同一性质：改它要全 mesh 同时升级 |
| 预算 `ClaudeUsage.TokenBudget` | 每台机器自己配 | 只影响本节点报出去的用量，不影响别人怎么解读 |

额度过线时席位表把这个节点排除掉；全员过线则席位表为空，PR 停在「已召集」不动 ——
这是想要的行为而不是故障。日志与状态栏会写明用量、来源和**几点重置后自动恢复**。

### ④ 独立评审：临时工作区 + collect 模式的输出契约

评审前现拉一份代码，评完（含失败与超时）立即删除：

```bash
mkdir  ~/.conclave/work/2880@a6478fa5-r0-3f9c1a2b
git init && git remote add origin <repository.remoteUrl>
git fetch --no-tags --depth=50 origin <source> <target>
git merge-base origin/<target> origin/<source>     # ← 必须校验，见下
git checkout -B <source> origin/<source>
#   … claude -p 在这个目录里跑 …
rm -rf ~/.conclave/work/2880@a6478fa5-r0-3f9c1a2b
```

这换掉了原来「在本机已有的 clone 里跑」的做法。三个好处：任何节点都能评任何仓库、
不必预先 clone 34 个 project 的仓库、也不会动到本机正在用的工作副本（早先直接在用户的
clone 里 `git fetch`，会碰到人家正在改的仓库）。

三个实测结论决定了这套流程的形状：

1. **必须校验 merge-base。** `az-pr-review` 靠 `git diff origin/target...origin/source`
   取三点 diff。浅到共同祖先不在历史里时，`git merge-base` 退出码 1、输出为空，紧接着
   `git diff a...b` 以**退出码 128** 报 `fatal: no merge base` —— 仓库白拉一遍、claude 白起
   一个，最后只落下一张 Error 票。所以拉完先校验，不够就 `--deepen`，再不够退到
   `--unshallow`，真的没有共同祖先才明确失败。
2. **部分克隆在这台 ADO Server 上无效。** `--filter=blob:none` 只会打印
   `warning: filtering not recognized by server, ignoring` 然后退化成全量传输 ——
   省流量只能靠深度，不能靠 filter。
3. **`az repos pr list` 不给 `remoteUrl`**（返回 null，只有 `az repos pr show` 才给真值）。
   所以 `PrMeta.RemoteUrl` 刻意不是 `required`（否则老 Summons 块反序列化会直接抛），
   为空时按 `<组织地址>/<project>/_git/<repo>` 兜底拼出来。

**认证靠全局 git 凭据**（credential helper，或该 host 的 `http.<url>.extraheader`）——
每台节点都要能在任意目录下免交互 `git clone` 那些仓库。子进程带 `GIT_TERMINAL_PROMPT=0` 起，
凭据不对就明确失败，不会挂着等输入。

**残留清理**：正常路径上目录由 `IAsyncDisposable` 删掉，但进程被 kill、断电、`.app` 强退时
不会走到那里，所以启动时把工作区根目录整个清一次。

```csharp
claude -p "/az-pr-review <prId>"
      --output-format json
      --session-id <由 (revisionId, round) 确定性派生>
      --allowedTools Bash Read Grep Glob
// 工作目录 = 上面那个临时工作区
// 环境变量 REVIEW_MODE=collect / CONCLAVE_REVISION / CONCLAVE_ROUND
```

`~/.claude/skills/az-pr-review/SKILL.md` §7 在 `REVIEW_MODE=collect` 下**跳过发评论与投票**，
并在回复末尾输出恰好一个 json 代码块（必须是最后一个围栏、且本身是合法 JSON）：

```json
{"decision":"reject","findings":[
  {"file":"src/A.cs","line":12,"severity":"major","title":"…","detail":"…"}]}
```

解析不到这个块就出 `Error` 票 —— **刻意不从自然语言里猜结论**：猜错方向会放过该拦的 PR，
而 Error 票只会让这一轮不计入多数决（但仍计入 quorum 分母，所以 PR 不会永远等下去）。

`--session-id` 由 `(revisionId, round)` 确定性派生，日后要追某一票是怎么来的，
`claude --resume <这个 ID>` 就能翻出当时的完整会话。

**为什么 skill 必须跳过投递**：quorum=3 时有 3 个节点跑同一个 PR，若各自都投递，
一个 PR 会收到 3 条重复评论和 3 次投票。

### ⑤⑥ 合并与公布

```
decision = 多数决(ballots.decision)         平票取更保守的一方（Reject < WaitForAuthor < Approve）

findings 贪心聚类：同文件、行号在锚点 ±5 内、且该簇尚未收过这一轮的 finding
  Confidence = 独立提到该 finding 的节点数 / 有效 ballot 数
  排序：Confidence ↓ → Severity ↓ → 文件名 → 行号
```

`Confidence == 1.0` 的 finding 基本不用人工复核；`< 0.5` 的建议只作提示不作阻塞。
**这是引入 quorum 的全部回报** —— 但只有把 quorum 配到 ≥2 才拿得到；默认 quorum=1 时
每条 finding 的 Confidence 都是 1.0，合并器等于直通。

「尚未收过这一轮」这条约束不能省 —— 同一个节点报的两条 finding 必然是两个不同的问题，
哪怕挨得很近。首次真实评审就撞上了：一票 4 条 finding 行号 9/13/17/13，早期按 `line / 10`
分桶把 13、17、13 全塞进同一桶，4 条真实问题只剩 2 条。

投递由**持最低未弃权席位的那个节点**在收齐票后做一次，且不再跑一遍 Claude ——
直接拿合并后的 findings 发一条 markdown 评论（带 Confidence 表格）+ `az repos pr set-vote`。

### 异常处理

| 情况 | 处理 |
|---|---|
| 认领后节点挂了 | 任何节点见到 `Seating` 超 10 分钟无 `Ballot` → 写 `Recess` → `round+1` 重新 HRW（候选集已排除掉线者）→ 自动接管 |
| 同 index 双重认领 | blockHash 字典序小者胜出，输的一段 rebase（见下） |
| 合格节点不足 | 降级执行，`Promulgation` 标 `degraded=true, actualQuorum=1`，链上留痕 |
| claude 子进程失败 / 超时 / 契约缺失 | 写 `Ballot` 且 `decision=error` + stderr 摘要。**该轮算烧掉、换人重试**，不作为结论 |
| 重试到 `MaxReviewAttempts` 仍无有效票 | 拿手上的票收尾 —— 一个 `degraded` 的 `Error` 结论，链上留痕 |
| 投递到 AzDO 失败 | 不阻止公布 —— 结论已经算出来了，先写链，投递可人工补 |
| 单个 project 无权限 / 超时 | 跳过，不拖垮整轮轮询 |

## Acta：一条全局签名链

```csharp
Hash = SHA256(ChainId|Index|PrevHash|At|Kind|PayloadJson|ElectorId)
// 刻意不含 Signature：ECDSA 是随机化签名，排除它之后区块哈希才是内容的确定性函数 ——
// 这是「同 index 取哈希小者」能成立的前提
```

五种区块：`Summons`（召集）· `Seating`（认领席位）· `Ballot`（签名的评审结论）·
`Promulgation`（公布并投递）· `Recess`（超时弃权）。一条链上交错着多个 PR，
按链序读一遍就是一条全局时间线：

```
#0 Summons      2721@dc1d1d47  quorum=2
#1 Seating      2721@dc1d1d47  round=0  elector=A
#2 Summons      2946@476b95ce  quorum=1    ← 另一个 PR 插在中间
#3 Ballot       2721@dc1d1d47  round=0  A  reject  5 findings  128k token  $0.42
#4 Seating      2721@dc1d1d47  round=1  elector=B
#5 Ballot       2946@476b95ce  round=0  A  approve 0 findings   61k token  $0.19
#6 Ballot       2721@dc1d1d47  round=1  B  reject  3 findings   96k token  $0.31
#7 Promulgation 2721@dc1d1d47  reject  merged=6  threadId=8821
```

落库前校验四件事：**链 ID 是本链** → **签名有效** → **`ElectorId` 在白名单** →
`PrevHash` 等于本地链尾哈希。白名单检查刻意排在冲突判定**之前** —— 否则外人只要造一个
哈希更小的块就能改写别人的链。

### 冲突让位与 append-only 的边界

全局单链的代价是**并发写必然撞索引**：两个节点只要在听到彼此之前各写一块，就会争同一个
index。规则是纯函数 —— 同 `(ChainId, Index)` 收到两个不同块时取 **blockHash 字典序小**者
胜出，双方各自执行会得到同一结果，无需协商。

「append-only」的准确说法是「**无冲突时**只追加」。让位必须真的删块，而且不能只删冲突那
一块（后面每块的 `PrevHash` 都指向它），要整段摘下来重挂：

| 被挤下来的块 | 处理 |
|---|---|
| 本节点自己写的 | 改索引后**重新签名**，挂到新链尾，并**继续广播** |
| 别人写的 | 丢弃 —— 没有对方私钥签不回去；等其作者在自己那边做同样的 rebase 后重推 |

重挂出来的块必须继续广播（`ApplyResult.Rebased`），否则对端不知道它们换了索引，两边永远
不会收敛 —— 这一步漏了单测全绿，只有双节点跑起来才看得出。整个过程在一个事务里。

账本记了 `index_conflicts` / `index_conflicts_lost` 两个计数，UI 状态栏右下角会显示，
让这条常态路径的代价看得见。

### 评审记录投影

链是唯一真相，但它不适合回答「这个月谁花了多少钱」。所以 Ballot 落链时同步写一层可查投影：

```sql
reviews              -- 每票一行：who / when / status / findings / tokens / cost
review_model_usages  -- 分模型明细：主模型与 skill 里子代理用的小模型各花了多少
```

每行带 `block_hash` 指回来源区块，随时能从链上重建、也能验证没被改过。
**让位重挂会改块哈希，所以 rebase 之后整体重建投影** —— 否则报表里会留下指向已不存在
区块的幽灵行、金额重复计。

## 技术栈

| 层面 | 选型 | 为什么不是别的 |
|---|---|---|
| 运行时 | .NET 10（`net10.0`，arm64 原生） | — |
| UI | Avalonia 12.1.2 + CommunityToolkit.Mvvm 8.4.2 | macOS 后端自带 Objective-C++，不需要 .NET workload 也不需要 Xcode（只要 CLT） |
| 宿主 | `Microsoft.Extensions.Hosting` 10.0.11 | UI 与 `BackgroundService` 同进程共存，无头模式就是「同一套服务不启动 Avalonia」 |
| 账本 | `Microsoft.Data.Sqlite` 10.0.11 + 手写 SQL（WAL） | 刻意不上 EF Core：两张 append-only 表加一层投影，手写 SQL 比 ORM 直白，也不必为一张表把 EF 整栈拖进 Infrastructure |
| 签名 | BCL 内置 `ECDsa` P-256 | 实测 .NET 10 **不含 Ed25519**（只有后量子 MLDsa/SlhDsa），为一个签名算法引入 NSec/BouncyCastle 的原生依赖不值得 |
| 节点发现 | UDP 多播 `239.255.42.7:47707`，自定义签名 JSON 信标 | 这里只需要「谁在线 + 它的 HTTP 端点」，不需要 DNS-SD 的服务/实例/TXT 那一整套；`Makaretu.Dns` 久未维护，自己发约 120 行且零依赖，`nc -ul 47707` 就能看 |
| 节点通信 | `HttpListener` / `HttpClient` + JSON，`:47708` | 区块本来就是 JSON，上 gRPC 要多一个 `.proto` 并与 `Block` record 同步；Kestrel 还要把 ASP.NET Core 框架引用拖进 Avalonia 进程、把 host 改成 `WebApplicationBuilder`。内网三个接口不值得 |
| AzDO 接入 | `az` CLI 2.82 + azure-devops ext 1.0.5 子进程 | 这是**自建 ADO Server**，实测 AAD bearer token 打上去返回 TF400813（REST 要 PAT + Basic）；走 `az` 顺带免掉每台机器各自维护 PAT |
| 评审引擎 | Claude Code CLI（`claude -p --output-format json`）+ `az-pr-review` skill | 复用已有 skill 的全部评审规则，Conclave 只做编排与账本 |
| 工作区 | 每次评审现拉的临时 git 目录（`~/.conclave/work/`），评完即删 | 任何节点都能评任何仓库，且不碰本机正在用的工作副本。部分克隆在这台 Server 上无效（`--filter` 被忽略），所以省流量靠深度 + merge-base 校验 |
| 改动统计 | ADO `pullRequestIterationChanges`（零克隆） | 定 quorum 只需要路径与文件数，为它先拉一遍仓库不值得。代价是 ADO 不给行数 |
| 额度来源 | `claude -p "/usage"`（官方无头本地命令，$0）+ 按预算折算兜底 | 比较过 statusLine / transcript JSONL / `/api/oauth/usage` 三条，只有这条既是官方百分比、又 headless、又不用改用户的全局配置或读 keychain |
| 测试 | xunit 2.9.3 + Shouldly 4.3.0 + **NetArchTest 1.3.2** | 架构守卫是这个项目正确性的支柱之一，见下 |
| 打包 | `dotnet publish` self-contained + 手写 `.app` bundle + `iconutil`；LaunchAgent 常驻 | 刻意不用 `PublishSingleFile`（默认把原生库留在单文件外，只拷可执行文件会漏 `libe_sqlite3.dylib`） |
| CI | Azure Pipelines，`macos-latest` | 四道门禁，见下 |

集中包版本管理（`Directory.Packages.props`）、`TreatWarningsAsErrors`、
`EnforceCodeStyleInBuild` 全开。

## 代码地图

依赖方向单向朝内：

```
Conclave.App              Avalonia UI + Generic Host 组装 + 四个命令入口 + 终端报表
  ├ Program.cs              默认起菜单栏常驻 / serve / review <id> / report / tray-selftest
  ├ Interop/MacStatusItem   自己建的 NSStatusItem（Avalonia 的 TrayIcon 在 macOS 收不到点击）
  ├ UsageReport.cs          按显示宽度对齐的账单（中文占两格）
  ├ Styles                  设计令牌（深浅两套）+ 控件样式与模板
  └ Views/MainWindow        主面板（点菜单栏图标直接出）：PR 队列 · 我的 PR · 评审记录 · Acta · 在线节点 · 通知

Conclave.Application      编排与端口，不引任何 UI 框架
  ├ DiscoveryService        轮询 AzDO → 写 Summons
  ├ ReviewOrchestrator      认领 → 跑评审 → 投票 → 公布
  ├ NodeState               后台与 UI 之间唯一的通信面（后台写、UI 读）
  └ Ports/                  IPrSource · IReviewRunner · IActaStore · IReviewLog
                            IMesh · IUsageMeter · IElectorAllowList

Conclave.Domain           零依赖、全纯函数
  ├ Revision, Block, Elector（含 MaxUtilization=0.8）, Beacon, PrMeta, ReservedMatters
  ├ Hrw                     加权 rendezvous 哈希
  ├ SeatAssignment          quorum 自适应 + 硬规则 + 席位表 + project 分片
  ├ QuorumEngine            多数决 + findings 聚类
  └ ActaProjection          区块序列 → ChainState

Conclave.Infrastructure   适配器
  ├ SqliteActa              链 + 让位重挂 + 投影；SqliteReviewLog 只读查询
  ├ AzCliPrSource           az 子进程：列 PR / 补 diff / 发评论 / 投票
  ├ ClaudeReviewRunner      claude 子进程 + collect 契约解析 + token 计量
  ├ GitWorkspace            临时工作区：拉取 · merge-base 校验/加深 · 删除 · 残留清扫
  ├ ClaudeUsageMeter        额度记账：滚动窗口折算 + usage 覆盖文件
  ├ Mesh/                   MeshBeaconSocket · MeshHttpServer · HttpMesh · MeshService
  ├ ExecutableResolver      az / claude / git 的路径兜底（见「踩过的坑」8）
  └ FileElectorAllowList    ~/.conclave/electors.allow，按 mtime 热重载
```

进程模型是单进程：Generic Host 在后台线程跑三个 `BackgroundService`，Avalonia 占主线程；
UI 与后台只通过 `IActaStore` 和 `NodeState` 两个 singleton 通信，不直接互调。

## 怎么用

前置：`.NET 10 SDK`、`az`（已 `az devops login`）+ azure-devops 扩展、`claude`、`git`。

**不再需要预先 clone 任何仓库** —— 但 `git` 必须能在任意目录下免交互克隆那些仓库
（全局 credential helper，或该 host 的 `http.<url>.extraheader`）。验证一下：

```bash
git -C /tmp clone --depth=1 <某个仓库的 remoteUrl> /tmp/conclave-auth-probe && rm -rf /tmp/conclave-auth-probe
```

额度判定不需要任何额外配置 —— Conclave 自己跑 `claude -p "/usage"` 取真值（零成本、无头）。

```bash
dotnet test Conclave.slnx                            # 239 个测试（232 单测 + 7 架构守卫）
dotnet run --project src/Conclave.App                # 菜单栏常驻：图标 + 后台服务，不进 Dock
dotnet run --project src/Conclave.App -- serve       # 无 UI 常驻（worker 机器 / 本机联调）
dotnet run --project src/Conclave.App -- review 2878 # 无头：只评这一个 PR 然后退出
dotnet run --project src/Conclave.App -- report      # 账单：谁评了什么、多少 token、多少钱
dotnet run --project src/Conclave.App -- tray-selftest  # 验证「点图标 → 出主面板」这条原生通路
scripts/package-macos.sh                             # 打成 dist/Conclave.app
```

`review <id>` 是原 `bridge.sh` 唯一需要保留的能力：人手上只有一个 PR 号、不想等下一轮
轮询时插队。它刻意不看 `IsDraft` —— 明确指名要评的，草稿也评。

### 菜单栏常驻

平时只有菜单栏右上角一个图标，**不占 Dock、不进 Cmd+Tab**（`LSUIElement` + 运行时
`MacOSPlatformOptions.ShowInDock=false`）。后台的轮询与编排一直在跑。

图标本身就是状态：**空闲闭眼打盹，评审中睁眼，而且一秒一次一张一合地呼吸**
（定时换图，macOS 的 `NSStatusItem` 没有别的做法；空闲时定时器是停的，不占 CPU）。

**点一下图标就是主面板**（`MainWindow`，1280×840），没有中间那一跳；再点一下收起。
面板**失焦即隐藏** —— 它是「看一眼就走」的东西，切去别的应用就自己收起来。
行内动作菜单（「⋯」）开着时不收，否则点开菜单的那一刻面板会连着菜单一起消失。

窗口**没有系统标题栏**（`WindowDecorations="None"`），所以也没有那三个红黄绿按钮：
圆角与边框由最外层的 `Border.shell` 画，按住顶栏可以拖动窗口。

第一次打开时居中摆到**点击那块屏**上（多屏见 DESIGN §11），之后自己挪过的位置不会被改回去。
关它、切走都只是隐藏，不销毁 —— 不进 Dock 之后没有别的入口能再打开。

`Cmd+Q` 在 accessory 应用上是失效的，而点图标直接出面板、没有右键菜单 ——
所以**退出的唯一入口是面板顶栏右上角的「退出」按钮**。

点击这条路是自己写的 Objective-C runtime 互操作：Avalonia 的 `TrayIcon` 在 macOS 上
收不到点击，`libAvaloniaNative` 里的 `AvnTrayIcon` 只导出 `SetIcon` / `SetMenu` /
`SetToolTipText` / `SetIsVisible` / `SetIsTemplateIcon` 五个方法，native ABI 里没有回调入口。
那层没有编译期保障，所以有 `tray-selftest` —— 它用 `performClick:` 走真人点击的同一条
target/action 通路，验证「点图标 → 出主面板」，**Avalonia 或 macOS 大版本升级之后跑一次**。

### UI 里的两个默认关闭的开关（右上角）

| 开关 | 默认 | 为什么 |
|---|---|---|
| 自动评审 | **关** | 一开机就把所有活跃 PR 全评一遍会烧掉可观额度。先手动点几个确认链路 |
| 投递到 AzDO | **关** | 确认合并结果质量之前不要往真实 PR 上发评论、投票 |

窗口从上到下四层：**顶栏**（本节点身份：elector 指纹 + 此刻在评什么 + 两个开关）、
**额度与账单条**（每个额度窗口一条进度条 + 今日/累计的次数与金额、token、缓存命中）、
**六张平级的表**（tab 上带条数）、**状态栏**（最后一句状态 + 链长与索引冲突）。

六张表分别是：**PR 队列**（幂等键 / 阶段与收票进度 / 本节点席位 / 结论 + 手动触发与指派
按钮）、**我的 PR**（队列里作者是本机 az 身份的那些 —— 本节点评不了它们，只能指派出去）、
**评审记录**（账单，含缓存命中与分模型明细）、**Acta 会议录**（区块浏览器）、
**在线节点**（额度 / 在跑 / 近 24H —— 正好是席位权重的三个输入）、**通知**（见下）。

额度条**每个窗口各画一条**（会话额度 5h / 周额度 7d / 按模型细分的周额度），带各自的
重置时刻；过 80% 变红 —— 那正是 `Elector.MaxUtilization`、也就是本节点不再入席的那条线，
所以「界面变红」和「真的停止接活」是同一刻。真值拿不到时退一条「按预算折算」并标明
「（折算）」，同时说清为什么没有真值。

**下半区的刷新是定时的（30 秒）**，跟其他面板不同 —— 额度和账单都没有事件可订阅：
额度真值来自 `claude -p "/usage"`（探针自带 60 秒缓存），账单来自 `reviews` 投影表，
而写它的可能是 gossip 进来的别人的票。左上角有「更新于 HH:MM:SS」：数字冻住时界面上
必须看得出来，否则「没有变化」和「没在刷新」分不开。

### 通知

**「通知」那张表是收件箱，顶栏下面那条 3 秒的提示（toast）是即时反馈**，两者互补：
mesh 里的事多半在人不看屏幕时发生 —— 有人请你评审、对方接受或拒绝了你的指派、
评审出了结论、额度过了入席门槛、探针读不到额度。这些原先只写进日志和状态栏，
而**状态栏会被下一轮轮询覆盖**（默认 60 秒一轮），于是「刚才到底发生了什么」在界面上
无处可查。tab 上的角标是**未读数**，切到这一页就清零。

只在内存里，不进链也不广播：这些是「本机看到了什么」，不是需要全 mesh 共识的事实
（那些都在 Acta 和评审记录里，随时能查）。所以重启即清空，「清空」按钮也不需要二次确认。
一分钟内同样的内容不重复记 —— 轮询失败这类问题每轮都会复现，不去重会把真正的事件挤出去。

配色取自应用图标（墨蓝 / 金 / 羊皮纸白），深浅两套令牌在 `src/Conclave.App/Styles/`，
跟随系统主题。

### 配置

四层叠加，从弱到强：程序目录 `appsettings.json` → `~/.conclave/appsettings.json`
→ `CONCLAVE_` 前缀的环境变量 → 命令行。用户目录那份优先于程序目录，因为装好的 `.app`
里没法改文件，而每台机器的 repo 路径和 mesh 端口都不一样。

模板见 `scripts/appsettings.example.json`。常改的几项：

| 键 | 默认 | 说明 |
|---|---|---|
| `PollInterval` | `00:01:00` | 轮询 AzDO 的间隔 |
| `OrchestratorInterval` | `00:00:15` | 编排循环间隔 |
| `SeatingTimeout` | `00:10:00` | 认领后多久没出票判定弃权 |
| `ReviewTimeout` | `00:15:00` | 单个 claude 子进程的墙钟上限 |
| `CheckoutTimeout` | `00:10:00` | 拉临时工作区的墙钟上限 |
| `MaxConcurrent` | `2` | 本节点同时最多跑几个评审 |
| `AllowSelfReview` | `false` | 允许作者评自己的 PR。**只用于联调** —— 由发现节点写进队列项随 mesh 广播（不是各节点各判），并计入规则指纹 |
| `Mesh.Enabled` | `true` | 关掉就是单机模式：不发也不收心跳，界面上「在线节点」永远只有自己 |
| `Mesh.TrustAllElectors` | ⚠️ `true` | 信任所有验签通过的节点，不读 `electors.allow`。**当前默认开着**，为的是小范围试用不必先交换指纹 —— 代价是同网段任何一台 Conclave 都能让你的机器起 claude 跑 `Bash`、烧你的额度。**范围超出本组就必须改回 `false`** 并按下文互相写 `electors.allow` |
| `Quorum.*` | 全 `1` | 一个 PR 由几个节点独立评审。配成 2/3 就恢复多节点合并，见「② 召集」 |
| `MaxReviewAttempts` | `3` | 一个 revision 最多烧掉几个席位（超时与失败各算一次） |
| `StickyReviewer` | `true` | 作者 fix 后仍由上一版的评审者复审 |
| `FetchDepth` | `50` | 临时工作区的初始拉取深度；不够会自动加深，配小了只多一次往返 |
| `ClaudeUsage.Window` | `7.00:00:00` | 额度用量的滚动窗口 |
| `ClaudeUsage.CliProbeInterval` | `00:01:00` | 多久起一次 `claude -p "/usage"` 取真值 |
| `ClaudeUsage.TokenBudget` | `40000000` | 折算兜底的 token 预算。真值可用时基本不起作用；**它低报约三个数量级**，别依赖 |
| `ClaudeUsage.CostUsdBudget` | `0` | 金额预算；与 token 预算取更紧的那个 |
| `AzureDevOpsOrgUrl` | `""` | 留空则读 `az devops configure` 的 `organization` |
| `ProjectAllowList` | `[]` | 留空 = 轮询全部 project |
| `ExtraToolPaths` | `[]` | `az`/`claude` 装在非常规位置时补 |
| `Mesh.Enabled` | `false` | 关掉就是单机模式 |

生效配置会在启动日志里完整打一遍 —— 四层叠加出问题时，最先要确认的就是「到底生效了哪份」。

### 首次启动会在 `~/.conclave/` 下生成

| 文件 | 说明 |
|---|---|
| `elector.key` | 本节点的 ECDsa P-256 私钥，权限 0600，已在 `.gitignore` 内 |
| `acta.db` | 本节点的 Acta 账本（SQLite，WAL） |
| `work/` | 临时工作区。评审期间才有内容，评完即删；启动时整目录清一次残留 |
| `electors.allow` | 节点白名单，**需要自己建**（不存在 = 单机模式） |
| `appsettings.json` | 可选的本机覆盖配置 |

### 无头模式的退出码

给脚本用（`bridge.sh` 就依赖它）：

| 码 | 含义 |
|---|---|
| 0 | approve / approve-with-suggestions |
| 1 | reject / wait-for-author |
| 2 | 找不到这个 PR |
| 3 | 执行失败（claude 子进程或契约解析） |
| 4 | 超时前没出结论 |

### 账单

```
总计 2 次评审 · 555.7k token · 折合 $1.26
缓存命中 92%（命中的输入 token 计价远低于新输入，这个数越高越省）

⚠️  金额是按 API 目录价折算，不是实际扣费。走 Max/Pro 订阅时边际成本为 0。

按人                          按模型
  guangliangli  2  555.7k  $1.26    claude-opus-5  1  555.7k  $1.26

最近的评审
  时间          谁            PR    仓库         状态    问题  TOKEN   金额   耗时
  09-08 19:23  guangliangli  2880  edison-test  Reject    11  555.7k  $1.26  363s
```

`conclave report --since 2026-09-01` 限定起始日期。还有按月 / 按仓库 / 按模型三张汇总表。

两个刻意的设计：

- **`cost_basis` 必须一起入库并在报表上标出来**。`claude` 报的口径通常是 `list`（API 目录价
  折算），走 Max/Pro 订阅时边际成本其实是 0 —— 不标的话「这个月花了 40 美元」会被读成账单。
- **token 分四类**而不是简单的输入/输出。缓存读写的计价与新输入差一个量级，混在一起就
  没法判断「是不是该把 review 的上下文做得更可缓存」。实测一次真实评审缓存命中 92%，
  这个数就是优化空间的直接指标。
- `cost_usd` 用 `REAL` 而不是全局规范里的 `numeric(10,2)`：单次评审常在 $0.001 量级，
  两位小数会全部归零。

### 打包与常驻

```bash
scripts/package-macos.sh                                  # → dist/Conclave.app
xattr -dr com.apple.quarantine dist/Conclave.app && open dist/Conclave.app
```

脚本自己会跑一次 `report` 自检 —— 只 `test -f` 是不够的，缺原生库时文件全在、一跑就炸。
发给同事需要签名 + 公证（脚本末尾打印了完整命令），否则对方双击只会看到「已损坏」。

#### GitHub Release（正式分发走这条）

```bash
git tag v1.3.0 && git push origin v1.3.0
```

`.github/workflows/release.yml` 在 macOS runner 上跑完全部测试（含架构守卫门禁），
用 `scripts/ci/release-assets.sh` 打出 `osx-arm64` 与 `osx-x64` 两个 `Conclave.app` 的 zip，
连同 `SHA256SUMS` 和 `manifest.json` 发成 GitHub Release，说明里带安装命令。
所有打包逻辑都在那个脚本里，本机 `scripts/ci/release-assets.sh 0.0.1` 能原样跑通再推。

`manifest.json`（版本、提交、每个包的 sha256）是给日后 app 内更新器读的：
它查 `releases/latest`、比版本、校验和对上才装。仓库是公开的，这一步不需要 token。

每次 push / PR 另有 `ci.yml` 在 Linux 上跑测试，不打包。

#### app 内更新

装好的 `Conclave.app` 每小时查一次 `releases/latest`（`Conclave.Update` 配置节，可关）。
有比本机新的版本就在顶部出一条横幅并进「通知」—— **只提示，不自动装**：这个进程在后台
跑评审，换二进制的时机由人挑。点「立即更新」之后：

```
下载到 ~/.conclave/updates/ → 对 manifest.json 里的 sha256 → 等本节点评完手上的 PR
→ ditto 解到旁边 → xattr -dr（包没签名）→ 旧 .app 改名 .previous、新的挪进去
→ launchctl kickstart -k（有 LaunchAgent）或 open（没有），进程重启
```

- 校验不过一个字节都不留；替换失败 .app 保持原样；旧版留一份 `Conclave.app.previous` 可手动换回。
- 开发态（`dotnet run`）不在任何 .app 里，「立即更新」会明确报「没有可替换的目标」，不去猜 `/Applications`。
- 只做了 macOS。其他平台连提示都不出 —— 查到了也装不了。

**心跳带版本号**（`Elector.AppVersion` / `Elector.ProtocolVersion`）。协议版本不同的节点互相
连签名都验不过（载荷格式不同），以前那就是静默地「它掉线了」；现在收方在验签之前先认出
版本不对，记一条警告、进一条「通知」，十分钟一台机器最多说一次。**每次改心跳格式就把
`Beacon.ProtocolVersion` +1** —— 整个 mesh 仍然要一起升，但至少升没升齐看得见。

常驻用 `scripts/com.liontravel.conclave.plist`：

```bash
cp scripts/com.liontravel.conclave.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.liontravel.conclave.plist
```

⚠️ 必须是 **LaunchAgent** 而不是 LaunchDaemon —— Daemon 在没有图形会话的上下文里跑，
带 UI 的进程在那里起不来。plist 里显式写了 `PATH`，原因见「踩过的坑」8。

## 组 mesh

1. 每台机器起一次，记下 UI 左上角（或日志里）的 `elector` 指纹 —— 16 位十六进制
2. 互相把对方的指纹写进 `~/.conclave/electors.allow`（每行一个，`#` 注释，
   改动即时生效，模板见 `scripts/electors.allow.example`）
3. 在 `~/.conclave/appsettings.json` 里打开 `Conclave.Mesh.Enabled`

```
UDP 多播 239.255.42.7:47707   ← 签名心跳（谁在线 + HTTP 端点 + 有权限的 project + 负载 + 额度用量）
HTTP     :47708               ← POST /blocks · GET /chain?from=N · GET /elector
```

- **心跳必须签名**：席位分配完全由心跳里的字段决定，伪造一个「空闲、额度还很多、什么
  project 都有权限」的心跳就能把席位全吸到自己名下然后永不出票，让所有 PR 卡在弃权重试
  循环里。收方验四件事：签名有效、公钥指纹与自称 Id 一致、在白名单内、时间戳新鲜（挡重放）。
  额度用量在签名范围内按 `F4` 定点格式化 —— `double` 的默认 `ToString` 位数可能随运行时
  变化，而那段文本要逐字节参与签名。
- ⚠️ **心跳格式是破坏性变更**：这次去掉了 `Repos`、加上了 `Utilization`，新旧节点会互相
  验签失败。**整个 mesh 必须同时升级。**
- **gossip 只转发新块**：区块推给 `Fanout`（默认 3）个随机邻居，靠对方继续扩散。
  对「本来就有」也转发会让区块在两节点间**无限回弹**，每跳都新起 HTTP 请求 —— 实测把进程
  OOM 掉过。只转发新块也顺带解决了三节点以上的环路。
- **补链是按索引增量拉**：落不进去且本地链尾更短 = 缺块，向 mesh 里第一个能补上东西的
  节点拉 `GET /chain?from=N`。全局单链之后不能整链重传，链会一直长。
- **冷启动先等成员表收敛**：mesh 开启时第一轮轮询前先等两个心跳周期。否则谁都还没收到
  别人的心跳，每个节点都以为 34 个 project 全归自己，同一批 PR 被所有节点各召集一遍，
  在 index 0 上撞成一堆索引冲突。

本机跑两个节点联调（实测过的最短路径）：

```bash
dotnet build src/Conclave.App/Conclave.App.csproj
APP=$PWD/src/Conclave.App/bin/Debug/net10.0/conclave.dll

for n in a b; do
  port=$([ $n = a ] && echo 47708 || echo 47709)
  CONCLAVE_Conclave__HomeDirectory=/tmp/node-$n \
  CONCLAVE_Conclave__Mesh__Enabled=true \
  CONCLAVE_Conclave__Mesh__HttpPort=$port \
  CONCLAVE_Conclave__Mesh__TrustAllElectors=true \
  CONCLAVE_Conclave__Quorum__Default=2 \
  CONCLAVE_Logging__LogLevel__Conclave=Debug \
  nohup dotnet $APP serve >> /tmp/node-$n/serve.log 2>&1 & disown
done
```

四件只有真跑过才知道的事：

- **`HttpPort` 必须分开，`BeaconPort` 不用**。信标口靠 `ReuseAddress` + `MulticastLoopback`
  共用（就是为本机多节点留的）；HTTP 口是 `http://+:{port}/`，第二个节点撞上就起不来。
- **`TrustAllElectors` 是为这个场景存在的**。不开就得先各起一次拿指纹、互相写进对方的
  `electors.allow`、再重启 —— 而它关掉的是「谁能让你的机器跑 Bash」那道闸，
  **只在联调时用环境变量临时开，别写进 `~/.conclave/appsettings.json`**。
- **`Logging__LogLevel__Conclave=Debug` 基本是必须的**。心跳被丢弃的原因
  （`不在白名单` / `验签失败` / `时间戳不新鲜`）全是 DEBUG 级，默认什么都看不到，
  只会以为多播不通。
- **前 40 秒没动静是正常的**：`等 00:00:40 让 mesh 成员表收敛后再开始轮询`。
  连上的标志是 `发现节点 <指纹> @ http://…`（Information 级）。

⚠️ 同机双节点的 **az 身份是同一个**，所以「不评审自己的 PR」对两个节点同时生效：
拿自己提的 PR 做 quorum=2 联调会两个节点都出局，PR 停在队列里不动。`dev-cluster.sh`
默认开了 `AllowSelfReview` 正是为此；手工起节点时自己带上
`CONCLAVE_Conclave__AllowSelfReview=true`。

## 测试与 CI 门禁

`dotnet test Conclave.slnx` → **239 个测试**（232 单测 + 7 架构守卫）。分布：

```
SeatAssignment 38 · ReviewOrchestrator 23 · ClaudeUsageMeter 23 · SqliteActa 16
QuorumEngine 12 · ReviewLog 12 · DiscoveryService 11 · ActaProjection 9
ExecutableResolver 8 · GitWorkspace 7 · HttpMesh 7 · Beacon 7 · Hrw 6 · Rebase 5
Block 5 · ConclaveOptions 5 · AzIdentity 4 · Revision 3 · ClaudeReviewRunner 3
Layering 5 · DomainPurity 2
```

`GitWorkspaceTests` 用**本地裸仓库**（`file://` URL）当 origin，所以离线可跑、不依赖
Azure DevOps。走 `file://` 而不是裸路径是必须的：git 对本地路径会忽略 `--depth`，
那样就测不到浅历史，而浅历史正是这里唯一真正会出事的地方 —— 其中一条测试刻意造出
「共同祖先落在浅边界之外」，断言加深逻辑真的补上了 merge-base。

**架构守卫为什么不能靠 reviewer**，这个项目的正确性有两根支柱，都只由它守着：

1. **领域层必须是纯函数**。`SeatAssignment` / `Hrw` 只要沾上一点本机状态 —— 读一次文件、
   看一次 `DateTimeOffset.UtcNow`、问一次环境变量 —— 各节点的结果就会分叉，表现为
   「同一个 PR 被两个节点同时评审」或「所有节点都以为该别人干」。**这种分叉不会报错**，
   只会静默地多烧一倍额度或让 PR 永远没人评；等有人发现，现场早就过去了。
   守卫直接扫源码，因为这是调用点的约束、编译产物里看不出来。
   `Conclave.Domain.csproj` 也被断言不含任何 `PackageReference` / `ProjectReference`。
2. **分层依赖朝内**。最容易破的是「Application 不引 Avalonia」—— 无头进程里没有 UI，
   一旦某个服务为了弹个提示引了 `Avalonia.Threading.Dispatcher`，编译通过、测试通过、
   只有真正跑 `conclave review` 时才在运行时炸。

CI（`azure-pipelines.yml`，`macos-latest`）四道门禁，每一条都对应一类真实事故：

| 门禁 | 内容 |
|---|---|
| 01 | 构建零警告（`TreatWarningsAsErrors`） |
| 02 | 全部测试绿 |
| 02b | **架构守卫真的跑过** —— assembly 因改名或漏 `ProjectReference` 而没被发现时，「0 个测试」也是绿色。`scripts/ci/assert-architecture-tests-ran.sh` 读 trx 的 `Counters` 断言下限 |
| 03 | 无头入口可用（`review 999999` 应返回退出码 2） |
| 04 | `.app` 打包脚本可用，且打出来的 bundle 能真的启动 |

## 安全约束

1. **别人的 job 会在你的机器上跑 `Bash`。** 白名单 `~/.conclave/electors.allow` 决定
   「谁的评审任务可以在你这里执行」，区块与心跳都要过这道闸。只加同一团队、本来就都有
   对应 repo 权限的人的机器。文件不存在 = 只信任自己 = 单机模式。
2. **代码会离开本机，而且范围变宽了。** node-B 评审 payment-center 意味着 payment-center
   的完整代码在 node-B 磁盘上出现过（现在是现拉、评完即删，但期间确实落盘）。去掉
   「本机有 clone」之后，**任何节点都能被派到任何仓库** —— 能评的范围只由 `az` 读权限决定。
   所以跨部门不要组网。
3. **`--disallowedTools "Bash"` 挡不住命令执行** —— Claude 会用 Monitor/子代理绕过。
   要么锁全（含 Monitor/Agent/TaskCreate），要么按需正常放开。这条是踩过的坑。
4. **每个节点要有自己的 Claude 订阅**，不能共享账号。这是 mesh 规模的硬约束。
5. **私钥** `~/.conclave/elector.key`，权限 0600，已在 `.gitignore` 内。后续挪进 macOS Keychain。

## 当前状态

**代码已落地且有测试覆盖**：五种区块 + 签名哈希链 + 索引冲突让位重挂；project 分片轮询、
硬规则过滤、加权 HRW 席位、可配置 quorum（默认只评一次）、复审归属、失败/超时换人重试
与重试上限；临时工作区（拉取 + merge-base 校验/加深 + 删除 + 残留清扫）；Claude 额度
判定与 80% 硬截断；findings 聚类合并 + 多数决 + 单节点投递；UDP 签名心跳 + HTTP gossip
+ 增量补链；评审记录投影 + 终端账单 + UI 三面板；无头入口 + `.app` 打包 + LaunchAgent。

**已在真机上跑过**：

- 单节点一轮扫到 29 个活跃 PR 并全部落链；**二次运行 29 → 29，零新增召集** —— 幂等键按设计生效
- 一次真实评审全链路：PR 2880 → Reject / 11 findings / 555.7k token / 缓存命中 92% / 363s
- 双节点：gossip 回弹 OOM 与索引冲突让位重挂都是在双节点实测中暴露并修掉的
- 私钥权限 0600，区块签名逐块验证通过

**尚未验证**：

- `quorum ≥ 2` 的真实多节点合并 —— 需要至少两台各有 Claude 订阅的机器（默认不开这条路）
- 真实多节点下的「失败换人接管」与「fix 回到同一节点」（单元测试覆盖了，真机没跑过）
- **临时工作区打到真实 ADO 仓库**。工作区逻辑本身有 7 个测试（本地裸仓库），但整条
  「拉真实仓库 → 跑 claude → 出票」的链路在改造后还没真机跑过一次
- 折算兜底的预算默认值还没校准过（真值可用时用不到它）
- 投递到真实 PR（`PostToAzureDevOps` 默认关，`RenderComment` 的产物还没在真实 PR 上看过）

路线图与每个阶段的验收标准见 [docs/DESIGN.md §13](docs/DESIGN.md)。

## 已知限制

- **跨网段不通**。UDP 多播只在同一网段内扩散，远程办公需要一个固定 IP 的种子节点（尚未实现）。
- **每次评审都要重新拉一遍仓库**。部分克隆在这台 ADO Server 上被忽略，所以省不掉传输量 ——
  大仓库上这是每次评审的固定开销，并发 2 个评审就是 2 份磁盘占用。真的太重时，
  下一步可以在 `~/.conclave/` 下留一份持久的裸仓库镜像、只让工作树是临时的
  （现在按「评完即删」实现，还没做这层缓存）。
- **额度真值靠解析 `/usage` 的文本**，格式随 Claude Code 版本可能变。变了会降级到折算
  （而折算低报约三个数量级），日志和面板会说明原因，但需要有人来跟一下格式。
- **一台机器一份额度**。同一个人在两台机器上是两份订阅额度，所以用量按 `reviewer_id`
  （公钥指纹）而不是 `az` 身份汇总。
- `az repos pr list` 打到 ADO Server 时每次会往 stderr 写一行
  `WARNING: ... does not support Azure DevOps Server`，功能正常 —— 退出码 0 时一律忽略 stderr。

## 踩过的坑

都写在对应代码的注释里，这里留一份索引。

1. **`.NET 10` 没有内置 Ed25519**（只有后量子的 MLDsa/SlhDsa），所以签名用 ECDsa P-256。
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
   带默认值 + 配置文件再列一遍 = 两份。默认值要在绑定后判空回填（`ApplyDefaults`）。
7. **gossip 只能转发新块** —— 对「本来就有」也转发会让区块在两节点间无限回弹，
   实测把进程 OOM 掉过。
8. **从 Finder / `open` 启动 `.app` 时 PATH 只有 `/usr/bin:/bin:/usr/sbin:/sbin`**。
   `az` 在 `/opt/homebrew/bin`、`claude` 在 `~/.local/bin`，都不在里面 —— 同一个二进制
   在终端里一切正常，双击图标就变成「读不到 az 登录身份」和「0 个 project」，
   而提示还指向了错误的方向。`ExecutableResolver` 在进程内兜底（配置 → PATH →
   常见安装目录），launchd 也是同一个坑（plist 里显式写 PATH）。
9. **配置目录要先探出来再加载配置**。配置文件本身就放在 `HomeDirectory` 下，把路径写死成
   `~/.conclave` 会导致用 `CONCLAVE_Conclave__HomeDirectory` 换目录后，私钥和账本搬走了、
   配置还在读老地方 —— 实测双节点联调时两个节点都读到了「mesh 关」，正是这个原因。
10. **`BallotPayload` 的 `Error` 必须用命名实参传**。它排在 `ReviewerAz` 之后，按位置传会
    静默落到 `ReviewerAz` 上，然后被编排层的 `with { ReviewerAz = ... }` 覆盖 ——
    错误信息就此消失，链上只留下一张没有原因的 Error 票。
11. **`ActaSchema` 要独立于 `SqliteActa`**。DDL 原本只在 `SqliteActa` 构造函数里，于是
    `conclave report` 这种只解析 `IReviewLog` 的路径会撞上 `no such table: reviews` ——
    表的存在依赖了另一个类恰好被构造过。
12. **浅拉取会让三点 diff 硬失败**。`git merge-base` 找不到共同祖先时退出码 1、输出为空
    （那不是错误，是「拉得还不够深」）；紧接着 `git diff a...b` 以退出码 128 报
    `fatal: no merge base`。仓库白拉一遍、claude 白起一个，只落下一张 Error 票。
    所以拉完必须校验 merge-base 并按需加深。
13. **部分克隆在这台 ADO Server 上是静默无效的**。`--filter=blob:none` 不报错，只打印
    `warning: filtering not recognized by server, ignoring` 然后退化成全量传输 ——
    以为省了流量其实没省。
14. **`az repos pr list` 的 `remoteUrl` 是 `null`**，只有 `az repos pr show` 才给真值。
    所以 `PrMeta.RemoteUrl` 不能声明成 `required`：那会让 `System.Text.Json` 在反序列化
    改造之前落链的老 Summons 块时直接抛。
15. **`git` 子进程要带 `GIT_TERMINAL_PROMPT=0`**。没有终端的进程里，凭据不对会让 git
    挂着等输入直到超时，而不是明确失败。
