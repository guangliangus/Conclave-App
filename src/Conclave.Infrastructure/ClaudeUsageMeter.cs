using System.Globalization;
using Conclave.Application;
using Conclave.Application.Ports;

namespace Conclave.Infrastructure;

/// <summary>
/// 量出本节点用掉了多少 Claude 额度。
/// </summary>
/// <remarks>
/// <para>
/// 两个来源，取<b>更紧</b>的那个：
/// </para>
/// <list type="number">
/// <item>
/// <b>订阅额度真值</b>（<c>claude-cli</c>）—— <see cref="ClaudeCliUsageProbe"/> 跑
/// <c>claude -p "/usage"</c> 拿到，按 5h / 7d 窗口拆开，带重置时刻。
/// </item>
/// <item>
/// <b>按预算折算</b>（<c>budget</c>）—— 按滚动窗口汇总本节点自己出过的票除以配置的预算。
/// 真值拿不到时（找不到 claude、API key 用户、输出格式变了）的兜底。
/// </item>
/// </list>
/// <para>
/// <b>折算兜底有个已知的严重偏差</b>：它只看得见 Conclave 自己出过的票。实测这台机器近
/// 7 天真实用量在 45 亿 cache-read token 量级，而 Conclave 自己只有 180 万 ——
/// 低报约三个数量级。所以折算只是「真值彻底拿不到」时的最后一道，不是可以长期依赖的东西；
/// 界面上会明确标「折算」，日志里也会说明为什么没有真值。
/// </para>
/// <para>
/// <c>time</c> 是可注入的时钟。周额度的阈值按工作日配速算（见 <see cref="UsagePressure"/>），
/// 所以这个类的输出跟「今天周几、几点」有关 —— 不注进来的话它的测试会随运行日期
/// 给出不同的数，那种用例今天绿明天红，比没有还糟。
/// </para>
/// </remarks>
public sealed class ClaudeUsageMeter(
    ConclaveOptions options,
    ClaudeCliUsageProbe probe,
    IReviewLog reviewLog,
    TimeProvider? time = null) : IUsageMeter
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<UsageReading> ReadAsync(string electorId, CancellationToken ct)
    {
        var (real, missing) = await ReadRealAsync(ct).ConfigureAwait(false);
        var budget = await ReadBudgetAsync(electorId, ct).ConfigureAwait(false);

        if (real is null)
        {
            // 没有真值时必须把「为什么没有」带出去。折算值看起来跟真值一样是个百分比，
            // 界面上分不出来的话，会以为自己看的是订阅额度 —— 而两者可能差很远。
            return budget is null
                ? UsageReading.Unbounded with { Detail = missing }
                : budget with { Detail = $"{budget.Detail}；{missing}" };
        }

        if (budget is null || budget.Utilization <= real.Utilization)
        {
            return real;
        }

        // 折算比真值还高：真值只覆盖 Claude 订阅侧，而折算是按本节点出票算的，
        // 极端情况下（预算配得很小）可能更紧。取更紧的那个，方向上偏保守（宁可少接活）。
        //
        // 两边比的都是「上报口径」的数，量纲一致：折算那条的阈值本来就是
        // Elector.MaxUtilization，压力折回来恰好等于它自己的原始比例，所以不用换算。
        return budget with
        {
            Source = "claude-cli+budget",
            Detail = $"{budget.Detail}（Claude 报 {real.Utilization:P0}，取更紧的）",
            ResetsAt = real.ResetsAt,
            // 聚合值取了折算那个，但窗口明细只有真值有 —— 面板照样画出 5h / 7d 两条。
            Windows = real.Windows,
        };
    }

    /// <summary>
    /// 取订阅额度真值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 聚合值取<b>参与判定的窗口</b>里的最大值（<see cref="UsageWindow.Gates"/>）——
    /// 任一窗口打满都会被限流，所以决定「还能不能接活」的是最紧的那个。按模型细分的
    /// 子额度（<c>seven_day:Fable</c>）不参与：Fable 的周额度打满不妨碍用 Opus 评审。
    /// </para>
    /// <para>
    /// <c>ResetsAt</c> 取<b>触线那个窗口</b>的，不是最早的那个 —— 5h 窗口半小时后重置、
    /// 但卡住我们的是三天后才重置的周额度时，报「半小时后恢复」是误导。
    /// </para>
    /// </remarks>
    private async Task<(UsageReading? Reading, string Missing)> ReadRealAsync(CancellationToken ct)
    {
        var (windows, note) = await probe.ReadAsync(ct).ConfigureAwait(false);
        if (windows.Count == 0)
        {
            return (null, $"没有 Claude 额度真值（{note}）");
        }

        var now = _time.GetUtcNow();
        var live = windows.Where(w => w.ResetsAt is null || w.ResetsAt > now).ToList();
        if (live.Count == 0)
        {
            return (null, "额度窗口都已过期");
        }

        var gating = live.Where(w => w.Gates).ToList();
        if (gating.Count == 0)
        {
            // 只剩按模型细分的子额度：它们不参与判定，但也不该因此报「没有真值」。
            gating = live;
        }

        // 各窗口各有各的阈值，取「离自己那条线最近」的那个，不是用量最大的那个。
        // 周额度用工作日配速判，理由见 UsagePressure。
        var binding = UsagePressure.Tightest(gating, now)
            ?? new UsagePressure.Binding(gating[0], UsagePressure.SessionGate, 0);


        // 显式写 *100 加 % 而不是用 :P0 —— InvariantCulture 的百分号格式会插一个空格
        // （"46 %"），跟界面上 Format.Percent 出来的 "46%" 不一致。同一个数在两处两个样子。
        var detail = string.Join(" · ", live.Select(
            w => string.Create(CultureInfo.InvariantCulture, $"{Short(w.Key)} {w.Utilization * 100:F0}%")));

        // 卡在哪个窗口、那个窗口当下的线在哪 —— 不写出来的话，日志里「额度 74%」旁边
        // 跟着「5h 10% · 7d 88%」，没有一个数对得上，排查时只能去读代码。
        detail += string.Create(
            CultureInfo.InvariantCulture,
            $"；卡在 {Short(binding.Window.Key)}，阈值 {binding.Gate * 100:F0}%");

        return (
            new UsageReading(
                UsagePressure.Reported(binding),
                "claude-cli",
                detail,
                binding.Window.ResetsAt)
            {
                Windows = live,
            },
            string.Empty);
    }

    /// <summary>报表里的短标签。界面上的正式显示名归展示层。</summary>
    private static string Short(string key) => key switch
    {
        "five_hour" => "5h",
        "seven_day" => "7d",
        _ => key.StartsWith(UsageWindow.ScopedSevenDayPrefix, StringComparison.Ordinal)
            ? "7d/" + key[UsageWindow.ScopedSevenDayPrefix.Length..]
            : key,
    };

    private async Task<UsageReading?> ReadBudgetAsync(string electorId, CancellationToken ct)
    {
        var budget = options.ClaudeUsage;
        if (budget.IsUnbounded)
        {
            return null;
        }

        var from = DateTimeOffset.UtcNow - budget.Window;
        var used = await reviewLog.ReadElectorUsageAsync(electorId, from, ct).ConfigureAwait(false);

        // 两个预算取更紧的那个：先触线的说了算。
        var byToken = budget.TokenBudget > 0
            ? used.TotalTokens / (double)budget.TokenBudget
            : 0;
        var byCost = budget.CostUsdBudget > 0
            ? (double)(used.CostUsd / budget.CostUsdBudget)
            : 0;

        var detail = budget.TokenBudget > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{FormatWindow(budget.Window)}内 {used.TotalTokens / 1_000_000.0:F1}M/{budget.TokenBudget / 1_000_000.0:F1}M token，{used.Reviews} 次评审")
            : string.Create(CultureInfo.InvariantCulture,
                $"{FormatWindow(budget.Window)}内 ${used.CostUsd:F2}/${budget.CostUsdBudget:F2}，{used.Reviews} 次评审");

        return new UsageReading(Clamp(Math.Max(byToken, byCost)), "budget", detail);
    }

    /// <summary>
    /// 夹到 [0,1] 并取到 4 位小数。
    /// </summary>
    /// <remarks>
    /// 取整不是为了好看：这个数进 <see cref="Domain.Beacon.SigningPayload"/> 时按 <c>F4</c>
    /// 格式化，而算权重用的是原值。不在源头对齐的话，签名覆盖的数字和实际参与席位计算的
    /// 数字就不是同一个 —— 差异极小，但性质不对。
    /// </remarks>
    private static double Clamp(double ratio)
        => Math.Round(Math.Clamp(double.IsFinite(ratio) ? ratio : 0, 0, 1), 4);

    private static string FormatWindow(TimeSpan window) => window.TotalDays >= 1
        ? window.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 天"
        : window.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时";
}
