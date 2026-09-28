using System.Security.Cryptography;
using System.Text;

namespace TalosDesk.Core.Configuration;

public sealed class SensitiveValueUnavailableException : Exception
{
    public SensitiveValueUnavailableException() : base("敏感值不可解密，请重新录入。") { }
}

public static class SensitiveValueProtector
{
    public const int MaximumValueLength = 4096;
    private const int MaximumProtectedLength = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("敏感值保护仅支持 Windows。");
        if (value.Length > MaximumValueLength || value.Contains('\0'))
            throw new ArgumentException("敏感值过长或包含无效字符。", nameof(value));

        var plaintext = StrictUtf8.GetBytes(value);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("敏感值保护仅支持 Windows。");
        if (protectedValue.Length > MaximumProtectedLength) throw new SensitiveValueUnavailableException();
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = Convert.FromBase64String(protectedValue);
            plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            var value = StrictUtf8.GetString(plaintext);
            if (value.Length > MaximumValueLength || value.Contains('\0')) throw new SensitiveValueUnavailableException();
            return value;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or DecoderFallbackException)
        {
            throw new SensitiveValueUnavailableException();
        }
        finally
        {
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
