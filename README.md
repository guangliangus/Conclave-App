# Conclave

> **con·clave** /ˈkɒŋkleɪv/ — 拉丁语 *cum clave*「以钥匙锁闭」。
> 闭门独立评议、投票形成决议、决议封存可查证的会议。

分布式 AI code review。每台装上它的 Mac 都是一个对等节点（**Elector**）：主动轮询
Azure DevOps 上的活跃 PR，按确定性规则分配评审席位，起 Claude Code 产出结论，
全过程记进一条签名哈希链账本（**Acta**）。

没有服务端、没有调度器、没有共识算法 —— 席位分配是纯函数，每个节点各自算出**同一张
席位表**，所以「谁评哪个 PR」不需要任何协商消息。

- 上手与日常使用（面向使用者，给同事看这份）：**[docs/USAGE.md](docs/USAGE.md)**
- 设计文档（含每处取舍的理由）：**[docs/DESIGN.md](docs/DESIGN.md)**

## 它解决什么问题

**PR 的 code review 不再需要有人记得去做。**

- **自己找活。** 每台机器自己去问 Azure DevOps「哪些 PR 是活跃的」，不用人报 PR 号，
  也就没有「漏掉的那个」。
- **只评没评过的那一版。** 最小单位是「PR 的某一版」而不是 PR —— 同一版代码不会被评
  第二遍；作者 push 了修复，自动算成新的一版再评一次，而且还是原来那台机器接手，
  它已经读过这份代码、提过这些问题。
- **活儿分得开。** 谁评哪个 PR 由所有机器各自算出的**同一张席位表**决定，不用调度器、
  不用互相商量；那台超时或跑挂了，下一台自动接手。谁关机都不影响其他人。
- **结论送到人手上。** 评完往 PR 发一条评论加一次投票，同时飞书私聊作者 —— 不用等人
  自己想起来去翻 PR 页面。
- **过程全程留痕。** 谁在什么时候评了哪一版、结论是什么、提了哪几条问题，都记在一条
  改不了的账本上，事后随时能翻出来。

想压掉 AI 的随机性时还有一档：让多台机器**独立**评同一个 PR，再多数决合并 ——
被多台都提到的问题几乎必然是真问题，只被一台提到的大概率是噪音。默认关着（`Quorum.*` 配成 2 或 3 就打开）。

## 一次评审是怎么走完的

```
                 ┌────────────────── 每 30 秒一轮 ──────────────────┐
                 ↓                                                  │
  ① 发现   az repos pr list --status active      只轮询 HRW 分给自己的 project
                 ↓
  ② 去重   幂等键 (prId, srcCommit) 已在链上？ ── 是 ──→ 跳过，这一版评过了
                 ↓ 否           ┌─ Summons ─┐  写入 Acta 并 gossip 给邻居
  ③ 入席   SeatAssignment.Seats(...)  纯函数，各节点独立算、结果一致
                 ↓ 算到自己     ┌─ Seating ─┐  （额度 <80%、不是 PR 作者、没打满）
  ④ 评审   临时工作区现拉源分支 → claude -p "/az-pr-review <id>" → 评完即删
                 ↓              ┌─ Ballot ──┐  decision + findings + token/金额
  ⑤ 合并   有效票 ≥ quorum ？ 多数决 + findings 聚类 → Confidence
                 ↓ 否 → 换下一个节点重试，最多 MaxReviewAttempts 次
  ⑥ 公布   ┌─ Promulgation ┐ ──→ az：发一条评论 + set-vote（整个 PR 只做一次）
```

## 几处关键设计

**幂等键刻意不是 PR ID。** 用 PR ID 当 key，作者 push 新 commit 后就不会重评；每轮轮询都
重跑又会重复烧钱。所以 key 是 `(prId, lastMergeSourceCommit)`，链上有它的 Summons 就跳过。
**不需要任何「是否已处理」状态表，Acta 本身就是。**

**席位是算出来的，不是分配的。** 硬规则（有该 project 读权限、不是 PR 作者、没打满、
额度 <80%、心跳 90 秒内）过滤后，用加权 rendezvous 哈希取分数最大者：

```
score(n) = Weight(n) / -ln(u(revisionId, round, n.Id))
Weight(n) = 1/(1+RunningJobs) / (1+Reviews24h*0.1) * (1-Utilization)
```

三个因子连乘而不是加权求和 —— 求和要调一组量纲不同的系数，连乘只要每个因子各自单调，
**加一个新维度不必重新配平旧的**。HRW 而不是 `hash % count`：节点上下线时只有它那一份
会重排。这两段都必须是纯函数，否则各节点算出不同的席位表，表现为重复评审或集体旁观，
**而且不报错** —— 架构守卫测试专门盯着这一点。

**一个 PR 同时只有一个节点在评。** quorum=1 时席位表只有 round=0 一个人，别的节点没有
席位可认，机制上就插不进来，不需要抢锁。只有超时（10 分钟没出票）或执行失败会让那一轮
「烧掉」、round+1 换人，上限 `MaxReviewAttempts`。确定性失败（输出里没有契约块）标
`Retryable=false`，一轮就收尾 —— 再跑一遍是同一个结果，而每轮都是完整账单。

**作者 fix 之后仍由同一个节点复审。** 它已经读过这份代码、提过这些 finding，边际成本远
低于换一个节点从头看。HRW 的种子是 PR 而不是 revision，外加从投影反查上一次有效评审的
节点让它直接坐 round=0。重试轮次必须换人 —— 上一个已经失败过了。

**评审前现拉代码，评完即删。** `~/.conclave/work/` 下 `git init` + 浅 fetch 源/目标分支，
校验 merge-base（不够就加深，`az-pr-review` 靠三点 diff，浅到共同祖先之外会硬失败），
跑完删掉。任何节点都能评任何仓库，不必预先 clone，也不碰本机正在用的工作副本。

**子进程跑 `REVIEW_MODE=collect`**：skill 在这个模式下跳过发评论与投票，只在回复末尾输出
一个 JSON 块（decision + findings）。解析不到就出 Error 票，**刻意不从自然语言里猜结论**。
投递由持最低席位的那个节点在收齐票后做一次，所以 quorum=3 也只有一条评论、一次投票。

**Acta 是一条全局链**，区块哈希刻意不含签名（ECDSA 是随机化签名，排除它之后哈希才是内容的
确定性函数），于是并发撞索引时「取 blockHash 字典序小者」就是纯函数裁决，无需协商；输的
一段整体摘下来重挂并继续广播。链是唯一真相，但不适合回答「这个月谁花了多少钱」——
所以 Ballot 落链时同步写一层 `reviews` / `review_model_usages` 投影，每行带 `block_hash`
指回来源区块。

## 技术栈

| 层面 | 选型 | 为什么不是别的 |
|---|---|---|
| 运行时 / UI | .NET 10 + Avalonia + CommunityToolkit.Mvvm | macOS 后端自带 Objective-C++，不需要 .NET workload 也不需要 Xcode |
| 宿主 | `Microsoft.Extensions.Hosting` | UI 与 `BackgroundService` 同进程，无头模式就是「同一套服务不启动 Avalonia」 |
| 账本 | `Microsoft.Data.Sqlite` + 手写 SQL（WAL） | 两张 append-only 表加一层投影，手写 SQL 比 ORM 直白 |
| 签名 | BCL 内置 `ECDsa` P-256 | .NET 10 不含 Ed25519，为一个签名算法引入原生依赖不值得 |
| 节点发现 | UDP 多播 `239.255.42.7:47707` + 签名 JSON 信标 | 只需要「谁在线 + HTTP 端点」，自己发约 120 行且零依赖 |
| 节点通信 | `HttpListener` / `HttpClient` + JSON，`:47708` | 区块本来就是 JSON；上 gRPC 或 Kestrel 都要把一整套框架拖进 Avalonia 进程 |
| AzDO 接入 | `az` CLI 子进程 | 自建 ADO Server 的 REST 要 PAT + Basic，走 `az` 顺带免掉每台机器各自维护 PAT |
| 评审引擎 | Claude Code CLI + `az-pr-review` skill（随包分发） | 复用已有 skill 的全部评审规则，Conclave 只做编排与账本 |
| 额度来源 | `claude -p "/usage"`（官方无头本地命令，$0 / 0.8s）+ 按预算折算兜底 | 比较过 statusLine / transcript JSONL / `/api/oauth/usage`，只有这条既是官方百分比、又 headless、又不用改用户全局配置 |
| 测试 | xunit + Shouldly + **NetArchTest** | 「领域层必须纯函数」「依赖只朝内」这两条靠 reviewer 守不住 |
| 打包 / 分发 | `dotnet publish` self-contained + 手写 `.app` bundle；GitHub Release + app 内更新 | 刻意不用 `PublishSingleFile`（会漏原生库） |

集中包版本管理、`TreatWarningsAsErrors`、`EnforceCodeStyleInBuild` 全开。

## 代码地图

依赖方向单向朝内：

```
Conclave.App              Avalonia UI + Generic Host 组装 + 命令入口 + 终端报表
  ├ Program.cs              菜单栏常驻 / serve / review <id> / report / tray-selftest
  ├ Interop/MacStatusItem   自己建的 NSStatusItem（Avalonia 的 TrayIcon 在 macOS 收不到点击）
  ├ Styles                  设计令牌（深浅两套）+ 控件样式
  └ Views/MainWindow        主面板：PR 队列 · 我的 PR · 评审记录 · Acta · 在线节点 · 通知

Conclave.Application      编排与端口，不引任何 UI 框架
  ├ DiscoveryService        轮询 AzDO → 写 Summons
  ├ ReviewOrchestrator      认领 → 跑评审 → 出票 → 合并 → 公布
  ├ NodeState               后台与 UI 之间唯一的通信面（后台写、UI 读）
  ├ ConfigHotReload / MeshSettingsFile   配置热更新与 mesh 配置同步
  └ Ports/                  IPrSource · IReviewRunner · IActaStore · IReviewLog
                            IMesh · IUsageMeter · IElectorAllowList · INotifier

Conclave.Domain           零依赖、全纯函数
  ├ Revision · Block · Elector（MaxUtilization=0.8）· Beacon · PrMeta · ReservedMatters
  ├ Hrw                     加权 rendezvous 哈希
  ├ SeatAssignment          quorum 自适应 + 硬规则 + 席位表 + project 分片
  ├ QuorumEngine            多数决 + findings 聚类
  └ ActaProjection / QueueProjection / PointsProjection   区块序列 → 各种只读视图

Conclave.Infrastructure   适配器
  ├ SqliteActa              链 + 索引冲突让位重挂 + 投影；SqliteReviewLog 只读查询
  ├ AzCliPrSource           az 子进程：列 PR / 补 diff / 发评论 / 投票
  ├ ClaudeReviewRunner      claude 子进程 + collect 契约解析 + token 计量
  ├ GitWorkspace            临时工作区：拉取 · merge-base 校验/加深 · 删除 · 残留清扫
  ├ ClaudeUsageMeter        额度记账：/usage 真值 + 滚动窗口折算兜底
  ├ LarkNotifier            结论出来私聊 PR 作者
  ├ Mesh/                   信标 · HTTP 服务端 · gossip · 增量补链
  └ ExecutableResolver / ClaudeCli   az / claude / git 的路径与版本挑选
```

进程模型是单进程：Generic Host 在后台线程跑几个 `BackgroundService`，Avalonia 占主线程；
UI 与后台只通过 `IActaStore` 和 `NodeState` 两个 singleton 通信。

## 怎么用

前置：`az`（已 `az devops login`）+ azure-devops 扩展、`claude`（已登录）、`git` 能在任意
目录下**免交互**克隆那些仓库（全局 credential helper 或该 host 的 `http.<url>.extraheader`）。
源码方式另需 .NET 10 SDK。**不需要预先 clone 任何仓库。**

```bash
dotnet test Conclave.slnx                              # 单测 + 架构守卫
dotnet run --project src/Conclave.App                  # 菜单栏常驻：图标 + 后台服务，不进 Dock
dotnet run --project src/Conclave.App -- serve         # 无 UI 常驻（worker 机器 / 本机联调）
dotnet run --project src/Conclave.App -- review 2878   # 无头：只评这一个 PR 然后退出
dotnet run --project src/Conclave.App -- report        # 账单：谁评了什么、多少 token、多少钱
scripts/package-macos.sh                               # 打成 dist/Conclave.app
```

平时只有菜单栏右上角一个图标（**空闲闭眼打盹，评审中睁眼呼吸**），点一下出主面板，
失焦即隐藏。UI 右上角两个开关**默认都是关的**：「自动评审」（一开机全评一遍会烧掉可观额度）
和「投递到 AzDO」（确认合并质量之前不要往真实 PR 上发评论）。

配置四层叠加：程序目录 `appsettings.json` → `~/.conclave/appsettings.json` →
`CONCLAVE_` 前缀环境变量 → 命令行。模板与逐项说明见
[`scripts/appsettings.example.json`](scripts/appsettings.example.json)。常用默认值：
轮询 30s、`MaxConcurrent=1`、`SeatingTimeout=10m`、`ReviewTimeout=45m`、
`MaxReviewAttempts=3`、`StickyReviewer=true`、`Quorum.*=1`。

首次启动在 `~/.conclave/` 下生成：`elector.key`（P-256 私钥，0600）、`acta.db`（账本）、
`work/`（临时工作区，评完即删）、`logs/`（评审日志留档）；`electors.allow` 需要自己建。

## 组 mesh

1. 每台机器起一次，记下 UI 左上角的 `elector` 指纹（16 位十六进制）
2. 互相把对方的指纹写进 `~/.conclave/electors.allow`（每行一个，改动即时生效）
3. 打开 `Conclave.Mesh.Enabled`

```
UDP 多播 239.255.42.7:47707   ← 签名心跳（谁在线 + HTTP 端点 + 有权限的 project + 负载 + 额度）
HTTP     :47708               ← POST /blocks · GET /chain?from=N · GET /elector
```

- **心跳必须签名**：席位完全由心跳里的字段决定，伪造一个「空闲、额度很多、什么权限都有」
  的心跳就能把席位全吸过去然后永不出票，让所有 PR 卡在弃权重试里。收方验四件事：
  签名有效、公钥指纹与自称 Id 一致、在白名单内、时间戳新鲜。
- **gossip 只转发新块**：对「本来就有」也转发会让区块在两节点间无限回弹，实测把进程 OOM 过。
- **缺块按索引增量拉** `GET /chain?from=N`，不整链重传 —— 链会一直长。
- **冷启动先等两个心跳周期**：否则每个节点都以为所有 project 归自己，同一批 PR 被各召集
  一遍，在 index 0 上撞成一堆索引冲突。

本机起两个节点联调见 `scripts/local-mesh.sh` / `scripts/dev-cluster.sh`（`HttpPort` 必须
分开，`BeaconPort` 靠 `ReuseAddress` 共用；同机双节点 az 身份相同，要开 `AllowSelfReview`）。

## 安全约束

1. **别人的 job 会在你的机器上跑 `Bash`。** `~/.conclave/electors.allow` 决定「谁的评审任务
   可以在你这里执行」，区块与心跳都要过这道闸。只加同一团队、本来就有对应 repo 权限的机器。
   文件不存在 = 只信任自己 = 单机模式。`Mesh.TrustAllElectors` 关掉的正是这道闸，
   范围超出本组就必须改回 `false`。
2. **代码会离开本机。** 任何节点都能被派到任何仓库，能评的范围只由 `az` 读权限决定 ——
   评审期间那份代码确实落在别人的磁盘上（评完即删）。**跨部门不要组网。**
3. **`--disallowedTools "Bash"` 挡不住命令执行** —— Claude 会用子代理绕过。要么锁全，
   要么按需正常放开。
4. **每个节点要有自己的 Claude 订阅**，不能共享账号。这是 mesh 规模的硬约束。
5. **私钥** `~/.conclave/elector.key` 权限 0600，已在 `.gitignore` 内。

## 已知限制

- **跨网段不通**：UDP 多播只在同一网段内扩散，远程办公需要一个固定 IP 的种子节点（未实现）。
- **每次评审都要重新拉一遍仓库**：部分克隆在这台 ADO Server 上被静默忽略（`--filter` 不生效），
  省流量只能靠深度。大仓库上这是每次评审的固定开销。
- **额度真值靠解析 `/usage` 的文本**，格式随 Claude Code 版本可能变；变了会降级到折算，
  而折算只看得见 Conclave 自己出的票、**低报约三个数量级**，界面与日志会标明。
- **一台机器一份额度**：同一个人在两台机器上是两份订阅，所以用量按公钥指纹而不是 `az` 身份汇总。

踩过的坑都写在对应代码的注释里，取舍的理由在 [docs/DESIGN.md](docs/DESIGN.md)。
