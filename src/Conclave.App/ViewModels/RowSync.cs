using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;

namespace Conclave.App.ViewModels;

/// <summary>
/// 把一张表的 <see cref="ObservableCollection{T}"/> <b>就地对齐</b>到新的一批行，
/// 只为真正变了的那几行发集合事件。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能再 <c>Clear()</c> 后重填。</b> <c>Clear()</c> 发的是
/// <c>NotifyCollectionChangedAction.Reset</c>，<c>ItemsControl</c> 收到之后会把<b>所有</b>
/// 容器拆掉，接着 N 次 <c>Add</c> 再新建 N 个 —— 一张 200 行的评审记录表就是两千多个
/// <c>TextBlock</c> 每 30 秒重建一遍。而实测（<c>vmmap</c> + heap 快照）Avalonia 12.1.2 的
/// macOS 后端每重建一轮界面会在 <c>MALLOC_SMALL</c> 里留下一批 CoreText 字体对象
/// （约每 100 秒 69 套 <c>TBaseFont</c>），于是「没人看的界面」以每分钟几十 MB 的速度
/// 往上漏，最终整机 OOM。
/// </para>
/// <para>
/// 稳态下（后台什么都没发生，只是定时器到点了）这里发<b>零</b>个集合事件 ——
/// 那正是绝大多数刷新的实际情况。有一行变了就只换那一行。
/// </para>
/// <para>
/// <b>行对象仍然是不可变的。</b> 没有把它们改成 <c>ObservableObject</c> 逐属性通知：
/// 那要给七个行类型共约一百个属性各写一遍 setter，而收益只是把「换掉一行的容器」
/// 变成「改那一行里的两个 TextBlock」—— 相比「不再重建整张表」，那点差别可以忽略，
/// 而半可变的行对象很容易出现「改了一半」的中间态。
/// </para>
/// </remarks>
internal static class RowSync
{
    /// <summary>
    /// 让 <paramref name="target"/> 变成 <paramref name="next"/>，尽量少动。
    /// </summary>
    /// <param name="target">界面绑着的那个集合。</param>
    /// <param name="next">这一轮算出来的行，顺序就是要显示的顺序。</param>
    /// <param name="key">
    /// 行的身份，跨刷新不变（revision id、区块哈希、节点指纹……）。
    /// <para>
    /// 必须在一批里唯一。不唯一不会出错，只是可能白换几行。
    /// </para>
    /// </param>
    public static void Apply<T>(
        ObservableCollection<T> target, IReadOnlyList<T> next, Func<T, string> key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(key);

        // ① 先删掉这一轮没有的。倒着删，索引才不会在循环里滑掉。
        var wanted = new HashSet<string>(next.Select(key), StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        // ② 按目标顺序逐位就位。
        for (var i = 0; i < next.Count; i++)
        {
            var want = next[i];
            var wantKey = key(want);

            if (i >= target.Count)
            {
                target.Add(want);
                continue;
            }

            if (!string.Equals(key(target[i]), wantKey, StringComparison.Ordinal))
            {
                var found = IndexOf(target, key, wantKey, i + 1);
                if (found < 0)
                {
                    target.Insert(i, want);
                    continue;
                }

                target.Move(found, i);
            }

            // 同一行：内容一样就<b>什么都不做</b>，界面上连一次布局都不会发生。
            if (!string.Equals(Signature(target[i]), Signature(want), StringComparison.Ordinal))
            {
                target[i] = want;
            }
        }

        // ③ 尾巴上多出来的。
        while (target.Count > next.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOf<T>(
        ObservableCollection<T> items, Func<T, string> key, string wanted, int from)
    {
        for (var i = from; i < items.Count; i++)
        {
            if (string.Equals(key(items[i]), wanted, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>每个行实例的内容指纹。行是不可变的，所以算一次就能一直用。</summary>
    private static readonly ConditionalWeakTable<object, string> Cache = new();

    /// <summary>
    /// 一行画出来的全部内容，压成一串。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>用反射遍历所有公开属性，而不是每个行类型手写一串拼接。</b> 手写的那种
    /// 会在「有人给行加了一个属性、忘了加进指纹」的那天静默失效 —— 表现是界面上
    /// 某一格永远停在旧值，而且没有任何报错。反射的失效方向是相反的：多算进来一个
    /// 属性只会让这一行白重建一次，退化回改造前的行为。
    /// </para>
    /// <para>
    /// 两个例外：<see cref="ICommand"/> 和 <see cref="TableLayout"/>。命令是每次刷新
    /// 都传同一批实例，取不出信息；而 <see cref="TableLayout"/> 是所有行共享的一个
    /// <c>ObservableObject</c>，列宽变化由它自己的属性通知推到 XAML，不需要换行对象。
    /// </para>
    /// </remarks>
    private static string Signature(object row)
    {
        if (Cache.TryGetValue(row, out var cached))
        {
            return cached;
        }

        var sb = new StringBuilder(256);
        foreach (var property in Readable(row.GetType()))
        {
            Append(sb, property.GetValue(row));
        }

        var signature = sb.ToString();
        Cache.AddOrUpdate(row, signature);
        return signature;
    }

    private static void Append(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                _ = sb.Append('∅');
                break;

            case string text:
                _ = sb.Append(text);
                break;

            // 行上的列表（指派候选）要逐项展开 —— 每轮刷新都是新的 List 实例，
            // 直接 ToString 只会得到类型名，等于把这一列排除在指纹外。
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    Append(sb, item);
                }

                break;

            default:
                _ = sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }

        _ = sb.Append('␞');   // RECORD SEPARATOR：正常内容里不会出现
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    private static PropertyInfo[] Readable(Type type) => Properties.GetOrAdd(
        type,
        static t => [.. t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => !typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Where(p => p.PropertyType != typeof(TableLayout))
            .OrderBy(p => p.Name, StringComparer.Ordinal)]);
}
