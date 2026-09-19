using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 用 Windows DPAPI（<see cref="ProtectedData"/>）加解密密钥，作用域 <see cref="DataProtectionScope.CurrentUser"/>。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>密文只能被<b>同一个用户的同一台机器</b>解开：把 config.json 拷到别的机器/别的账户，密钥就解不出来了
/// （这时 <see cref="TryUnprotect"/> 返回 null，界面提示重新填一次，而不是崩）。</item>
/// <item>加了固定的 entropy（附加熵）：即使别的程序拿到密文，不知道这串 entropy 也解不开。</item>
/// <item>格式：<c>dpapi:v1:&lt;base64&gt;</c>。没有这个前缀的值一律<b>拒绝</b>解析 ——
/// 这样有人把明文密钥手写进 config.json 也不会被当成有效密钥使用。</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>密文前缀，同时也是算法版本号。</summary>
    public const string Prefix = "dpapi:v1:";

    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("PotatoAgent.config.v1");

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), OptionalEntropy, DataProtectionScope.CurrentUser);

        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <inheritdoc />
    public string? TryUnprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue) ||
            !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, OptionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // 换了机器/用户、密文被截断、base64 坏掉 —— 都当"解不开"，不往上抛。
            return null;
        }
    }
}
