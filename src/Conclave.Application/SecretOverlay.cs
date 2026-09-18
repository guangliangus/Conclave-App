namespace Conclave.Application;

/// <summary>
/// mesh 送来的机密，<b>只在内存里</b>。
/// </summary>
/// <remarks>
/// <para>
/// 同步来的 <c>Lark.AppSecret</c> 刻意不写进 <c>mesh.json</c>：传输已经加密了
/// （见 <c>Conclave.Domain.SecretSealing</c>），再明文落一份盘等于把那份加密白做一半。
/// 代价是进程重启后要等一次同步轮询才恢复，而那期间 <c>LarkOptions.IsConfigured</c>
/// 为假、通知静默不发 —— 所以启动日志里会说清楚当前机密是从哪来的。
/// </para>
/// <para>
/// <b>本机配的永远优先。</b> <see cref="ConfigHotReload"/> 只在配置链里那份为空时才
/// 拿这里的值兜底，跟「用户目录那份盖过程序目录那份」是同一个方向：远端给的是默认值，
/// 本机写的是决定。
/// </para>
/// </remarks>
public sealed class SecretOverlay
{
    private readonly object _gate = new();
    private string _larkAppSecret = string.Empty;
    private string _source = string.Empty;

    /// <summary>mesh 送来的飞书 App Secret；没收到过就是空串。</summary>
    public string LarkAppSecret
    {
        get { lock (_gate) { return _larkAppSecret; } }
    }

    /// <summary>这份机密是谁发的（elector 指纹），只用于日志与面板。</summary>
    public string Source
    {
        get { lock (_gate) { return _source; } }
    }

    /// <summary>收到一份新的。</summary>
    /// <returns>跟手上那份不同（也就是真的变了）。</returns>
    public bool Accept(string secret, string publisherId)
    {
        ArgumentNullException.ThrowIfNull(secret);

        lock (_gate)
        {
            if (string.Equals(_larkAppSecret, secret, StringComparison.Ordinal))
            {
                return false;
            }

            _larkAppSecret = secret;
            _source = publisherId;
            return true;
        }
    }
}
