using System.Security.Cryptography;

namespace LBS.Domain;

/// <summary>
/// 站内密码的铸与验（M5.4）：PBKDF2-SHA256、210000 轮、16B 随机盐，
/// 存形 "pbkdf2$210000$&lt;saltHex&gt;$&lt;hashHex&gt;" ——参数就写在串里，日后调轮数可逐行验旧铸新。
/// 比对走 FixedTimeEquals，不给旁路测密留缝。
/// </summary>
public static class MemberPasswords
{
    public const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToHexString(salt).ToLowerInvariant()}${Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>验密：格式不对/没设过密码一律 false，绝不抛。</summary>
    public static bool Verify(string? stored, string? candidate)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || parts[1] != Iterations.ToString())
        {
            return false;
        }

        try
        {
            var salt = Convert.FromHexString(parts[2]);
            var expected = Convert.FromHexString(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>站规：密码至少 8 位，除此之外不教人做人（无复杂度强制）。</summary>
    public static bool MeetsPolicy(string? password) => !string.IsNullOrEmpty(password) && password.Length >= 8;
}
