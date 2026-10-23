using System.Diagnostics;
using System.Text;

namespace TalosDesk.Core.Processes;

internal sealed class StreamingOutputRedactor
{
    internal const string HiddenMarker = "[已隐藏]";
    internal const string OmittedMarker = "[已省略：脱敏失败]";
    private const int MaximumChunkLength = 16_384;
    private static readonly string[] TokenPrefixes = ["Bearer ", "token=", "api_key="];
    private readonly string[] _secrets;
    private readonly TimeSpan _timeout;
    private string _pending = string.Empty;
    private int _knownMaskRemaining;
    private int _tokenPrefixRemaining;
    private bool _suppressToken;
    private bool _failed;

    internal StreamingOutputRedactor(IEnumerable<string> secrets, TimeSpan? timeout = null)
    {
        _secrets = secrets.Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        if (_secrets.Any(value => value.Length > 4096)) throw new ArgumentException("A sensitive value exceeds the redaction limit.", nameof(secrets));
        _timeout = timeout ?? TimeSpan.FromMilliseconds(50);
    }

    internal string Append(string input)
    {
        if (_failed || input.Length == 0) return string.Empty;
        var result = new StringBuilder();
        try
        {
            for (var offset = 0; offset < input.Length; offset += MaximumChunkLength)
            {
                var length = Math.Min(MaximumChunkLength, input.Length - offset);
                result.Append(Process(string.Concat(_pending, input.AsSpan(offset, length)), final: false));
                if (_failed) return OmittedMarker;
            }
            return result.ToString();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failed = true;
            _pending = string.Empty;
            return OmittedMarker;
        }
    }

    internal string Finish()
    {
        if (_failed) return string.Empty;
        try { return Process(_pending, final: true); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failed = true;
            _pending = string.Empty;
            return OmittedMarker;
        }
    }

    private string Process(string input, bool final)
    {
        var result = new StringBuilder(input.Length);
        var timer = Stopwatch.StartNew();
        var offset = 0;
        while (offset < input.Length)
        {
            if (timer.Elapsed >= _timeout)
            {
                _failed = true;
                _pending = string.Empty;
                return OmittedMarker;
            }

            var remaining = input.AsSpan(offset);
            string? fullSecret = null;
            var partialSecret = false;
            foreach (var secret in _secrets)
            {
                var commonLength = Math.Min(remaining.Length, secret.Length);
                if (!remaining[..commonLength].SequenceEqual(secret.AsSpan(0, commonLength))) continue;
                if (remaining.Length >= secret.Length) fullSecret ??= secret;
                else partialSecret = true;
            }
            if (partialSecret && !final) break;

            string? fullPrefix = null;
            var partialPrefix = false;
            foreach (var prefix in TokenPrefixes)
            {
                var commonLength = Math.Min(remaining.Length, prefix.Length);
                if (!remaining[..commonLength].Equals(prefix.AsSpan(0, commonLength), StringComparison.OrdinalIgnoreCase)) continue;
                if (remaining.Length > prefix.Length) fullPrefix ??= prefix;
                else partialPrefix = true;
            }
            if (partialPrefix && !final) break;
            if (fullPrefix is not null && !char.IsWhiteSpace(remaining[fullPrefix.Length]))
                _tokenPrefixRemaining = fullPrefix.Length;

            if (fullSecret is not null)
            {
                if (_knownMaskRemaining == 0 && !_suppressToken) result.Append(HiddenMarker);
                _knownMaskRemaining = Math.Max(_knownMaskRemaining, fullSecret.Length);
            }
            if (char.IsWhiteSpace(input[offset])) _suppressToken = false;
            if (_knownMaskRemaining > 0)
            {
                // 已遮蔽的区域也逐字符匹配，覆盖从其内部开始、延伸到末尾之外的已知值。
                _knownMaskRemaining--;
            }
            else if (!_suppressToken)
            {
                result.Append(input[offset]);
            }

            if (_tokenPrefixRemaining > 0 && --_tokenPrefixRemaining == 0)
            {
                // 前缀本身也可能与已知值交叠；到前缀末尾才启用令牌省略，不跳过任何字符。
                if (!_suppressToken && _knownMaskRemaining == 0) result.Append(HiddenMarker);
                _suppressToken = true;
            }
            offset++;
        }

        _pending = final ? string.Empty : input[offset..];
        if (_pending.Length > 4095)
        {
            _pending = string.Empty;
            _failed = true;
            return OmittedMarker;
        }
        return result.ToString();
    }
}
