namespace Conclave.Application;

/// <summary>
/// mesh 配置同步：改一处，全组跟上。
/// </summary>
/// <remarks>
/// <para>
/// <b>没有「发布节点」这个角色。</b> 每台机器都把自己手上那份配置摆出来
/// （<c>GET /config</c>），也都去问别人有没有更新的；谁的 <see cref="Version"/> 大，
/// 谁那份就赢，然后沿着 mesh 一跳一跳传开。离线过的机器上线后从<b>任何</b>邻居都能追上，
/// 不必等某一台特定的机器活着。
/// </para>
/// <para>
/// <b>改配置的办法：在任意一台上编辑 <c>~/.conclave/meshsettings.json</c>，把
/// <c>ConfigSync.Version</c> 调高。</b> 那台机器于是持有全网最大的版本，几十秒内扩散完。
/// 编辑 <c>appsettings.json</c> 不会扩散 —— 那份是每台机器自己的（路径、端口、身份）。
/// </para>
/// <para>
/// <b>信任边界就是 mesh 本身。</b> 任何能连上本节点的 Conclave 都能推一份配置过来，也都能
/// 读到本节点这份。挡住损失的是 <see cref="SyncableConfig"/> 那张白名单 —— 可执行文件路径、
/// 更新源、mesh 端口、<c>TrustAllElectors</c> 一律不在同步范围内，所以最坏情况是策略被改，
/// 不是机器被接管。这跟 <c>Mesh.TrustAllElectors</c> 默认为 <c>true</c> 是同一个量级的假设：
/// 同网段的机器本来就能让你的节点去跑 claude。
/// </para>
/// </remarks>
public sealed class ConfigSyncOptions
{
    /// <summary>关掉就既不发也不收。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 本机手上这份配置的版本号，写在 <c>meshsettings.json</c> 里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同步进来的会覆盖它；想把本机的改动推出去就手工调高。刻意不按文件 mtime 之类自动推导：
    /// 「什么时候把改动推给全组」该是个有意识的动作，自动推导会让一次误保存立刻扩散。
    /// </para>
    /// <para>
    /// 同版本号撞车（两台各自改了、都写 8）由 <c>ConfigDocument.Supersedes</c> 按发布者指纹的
    /// 字典序判，确定性收敛，不需要协商 —— 代价是输的那一方的改动会被悄悄盖掉，
    /// 所以别两个人同时改。
    /// </para>
    /// </remarks>
    public long Version { get; set; }

    /// <summary>多久问一次邻居有没有更新的配置。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}
