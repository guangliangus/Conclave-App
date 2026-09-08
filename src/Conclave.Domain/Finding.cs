namespace Conclave.Domain;

public enum Severity
{
    Info = 0,
    Minor = 1,
    Major = 2,
    Critical = 3,
}

/// <summary>单个节点报出的一条问题。</summary>
public sealed record Finding(
    string File,
    int Line,
    Severity Severity,
    string Title,
    string Detail);

/// <summary>
/// 多个节点独立报出、被合并后的一条问题。
/// </summary>
/// <param name="Best">同组里 Severity 最高的那条原始 finding。</param>
/// <param name="Mentions">有多少个节点独立提到了它。</param>
/// <param name="Confidence"><paramref name="Mentions"/> / 有效 ballot 数。1.0 表示全体一致。</param>
public sealed record MergedFinding(
    Finding Best,
    int Mentions,
    double Confidence);
