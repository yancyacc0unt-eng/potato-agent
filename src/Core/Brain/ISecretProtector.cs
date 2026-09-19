namespace PotatoAgent.Core.Brain;

/// <summary>
/// 密钥加解密抽象。生产环境用 <see cref="DpapiSecretProtector"/>；
/// 抽出来是为了能单测配置读写而不用真碰 DPAPI。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文，返回可以安全落盘的字符串（带算法前缀，方便将来换算法）。</summary>
    string Protect(string plaintext);

    /// <summary>解密；解不开 / 格式不认识 / 不是本机本用户加密的，一律返回 null，不抛异常。</summary>
    string? TryUnprotect(string? protectedValue);
}
