using System.Net;
using System.Text.Json;

namespace Conclave.Infrastructure.Mesh;

/// <summary>
/// 实时评审日志的网页版。
/// </summary>
/// <remarks>
/// <para>
/// 服务于飞书「开始评审」卡片上那个按钮：作者点它就能看自己的 PR 评到哪一步了，
/// <b>不需要装 Conclave</b> —— 评审跑在别人的机器上，要求作者先装一个客户端
/// 才能看自己 PR 的进度，这个门槛比它解决的问题还高。
/// </para>
/// <para>
/// 页面自己去轮询同源的 <c>/log</c>，所以这里只发一次静态 HTML，不做服务端推送：
/// 那要么常驻连接（<see cref="System.Net.HttpListener"/> 上每条都占一个并发槽，
/// 而槽是有限的），要么引一个 WebSocket 栈。而 <c>/log</c> 本来就是按序号给增量的，
/// 两秒一次的轮询跟面板用的是同一条路 —— 面板就是这么刷的。
/// </para>
/// <para>
/// ⚠️ 身份校验同 <c>/log</c>：<b>没有</b>。同网段能连上这个端口的都读得到，
/// 而日志里有源码路径、命令行和模型的分析原文。要收紧就跟 <c>/chain</c>、<c>/state</c>、
/// <c>/log</c> 一起加签名校验，只给 electors.allow 里的节点 —— 单独收紧这一个没有意义，
/// 它背后的数据本来就从 <c>/log</c> 拿得到。
/// </para>
/// </remarks>
internal static class LiveLogPage
{
    /// <summary>
    /// 页面全文。
    /// </summary>
    /// <remarks>
    /// <paramref name="revisionId"/> 来自查询串，也就是说它是<b>请求方给的</b>。
    /// 进 JS 走 <see cref="JsonSerializer"/>（它负责转义引号和反斜杠），进 HTML 走
    /// <see cref="WebUtility.HtmlEncode(string?)"/>；日志行在前端用 <c>textContent</c> 写入，
    /// 不碰 <c>innerHTML</c>。三处都不能图省事 —— 这个端点不校验身份，
    /// 谁都能构造一个带脚本的 revision 参数。
    /// </remarks>
    internal static string Html(string revisionId)
    {
        ArgumentNullException.ThrowIfNull(revisionId);

        return Template
            .Replace("__REVISION_JS__", JsonSerializer.Serialize(revisionId), StringComparison.Ordinal)
            .Replace("__REVISION_HTML__", WebUtility.HtmlEncode(revisionId), StringComparison.Ordinal);
    }

    private const string Template = """
<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>评审日志 · __REVISION_HTML__</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; background:#12141a; color:#d7dae0;
         font:13px/1.55 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; }
  header { position:sticky; top:0; background:#1a1d26; border-bottom:1px solid #2a2e3a;
           padding:12px 16px; display:flex; gap:12px; align-items:baseline; flex-wrap:wrap; }
  h1 { margin:0; font-size:14px; font-weight:600; color:#e8eaee; }
  .rev { color:#8b91a0; }
  .dot { width:8px; height:8px; border-radius:50%; display:inline-block; margin-right:6px; }
  .live .dot { background:#3fb950; animation:pulse 1.6s ease-in-out infinite; }
  .done .dot { background:#8b91a0; }
  .lost .dot { background:#f0883e; }
  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:.35} }
  #status { margin-left:auto; font-size:12px; color:#8b91a0; }
  #log { padding:12px 16px; white-space:pre-wrap; word-break:break-word; }
  #log div { padding:1px 0; }
  #empty { color:#6a7080; padding:12px 16px; }
</style>
</head>
<body>
<header>
  <h1>评审日志</h1>
  <span class="rev">__REVISION_HTML__</span>
  <span id="status" class="live"><span class="dot"></span><span id="statusText">连接中…</span></span>
</header>
<div id="empty">还没有输出。评审刚开跑时要先拉代码，这一段通常没有日志。</div>
<div id="log"></div>
<script>
(function () {
  var SETTLE_POLLS = 3;   // 认定「真的结束了」之前，空的 not-running 响应要连着收到几次
  var revision = __REVISION_JS__;
  var from = 0;
  var settling = 0;
  var log = document.getElementById('log');
  var empty = document.getElementById('empty');
  var status = document.getElementById('status');
  var statusText = document.getElementById('statusText');

  function setStatus(cls, text) {
    status.className = cls;
    statusText.textContent = text;
  }

  function atBottom() {
    return window.innerHeight + window.scrollY >= document.body.scrollHeight - 40;
  }

  function append(lines) {
    if (!lines.length) { return; }
    empty.style.display = 'none';
    var stick = atBottom();
    var frag = document.createDocumentFragment();
    lines.forEach(function (line) {
      var row = document.createElement('div');
      // textContent 而不是 innerHTML：日志里有模型原样吐出来的代码片段。
      row.textContent = line;
      frag.appendChild(row);
    });
    log.appendChild(frag);
    if (stick) { window.scrollTo(0, document.body.scrollHeight); }
  }

  function poll() {
    fetch('/log?revision=' + encodeURIComponent(revision) + '&from=' + from)
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (chunk) {
        // ⚠️ 字段名是 PascalCase。/log 走 ActaJson，而那份 options 刻意没有设命名策略 ——
        // 区块哈希覆盖 PayloadJson 的字面文本，改命名会让链上所有老区块的哈希失效。
        // 写成 chunk.next / chunk.lines / chunk.running 的话三个全是 undefined：
        // 页面一行都不显示，而且第一次响应就把自己判成「已结束」。
        from = chunk.Next;
        var lines = chunk.Lines || [];
        append(lines);

        if (chunk.Running) {
          settling = 0;
          setStatus('live', '评审中');
          setTimeout(poll, 2000);
          return;
        }

        // Running=false 不等于「评完了」：本节点重启过、这一版被环形缓冲挤掉
        // （ReviewProgressLog 只留最近 8 个 revision）、或者评审换了台机器，
        // 读回来的都是一个空的 not-running chunk。所以再多问几拍才认 ——
        // 真评完了不过是多几个空响应，而认错了这一页就再也不会动了。
        // 收尾这几拍也正好接住最后几行：它们常常跟结束标志同一拍落下来。
        settling = lines.length ? 0 : settling + 1;
        if (settling < SETTLE_POLLS) {
          setStatus('live', '收尾中…');
          setTimeout(poll, 2000);
          return;
        }

        setStatus('done', '已结束');
      })
      .catch(function () {
        // 节点重启或换网段都会走到这里。不停重试 —— 评审要跑十几分钟，
        // 中间断一下就永久放弃的话，这个页面的意义就没了。
        setStatus('lost', '连不上评审节点，重试中…');
        setTimeout(poll, 5000);
      });
  }

  poll();
})();
</script>
</body>
</html>
""";
}
