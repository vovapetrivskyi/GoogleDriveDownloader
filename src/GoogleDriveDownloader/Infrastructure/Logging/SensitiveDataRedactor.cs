using System.Text.RegularExpressions;

namespace GoogleDriveDownloader.Infrastructure.Logging;

internal sealed class SensitiveDataRedactor
{
    private const string Replacement = "[REDACTED]";
    private static readonly Regex NamedSecret = new(
        "([\"']?\\b(?:access[_ -]?token|refresh[_ -]?token|id[_ -]?token|client[_ -]?(?:secret|id)|password|authorization|code[_ -]?verifier|resourcekey)[\"']?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|[^\\s,;&}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Bearer = new(
        @"\bBearer\s+[^\s""',;<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex GoogleToken = new(
        @"(?:\bya29\.|1//)[^\s""',;&<>]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly object _sync = new();
    private string[] _knownSecrets = [];

    public SensitiveDataRedactor(params string[] knownSecrets)
    {
        Register(knownSecrets);
    }

    public void Register(params string[] values)
    {
        lock (_sync)
        {
            var secrets = values.Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => new[] { value, Uri.EscapeDataString(value) });
            Volatile.Write(ref _knownSecrets, _knownSecrets.Concat(secrets)
                .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray());
        }
    }

    public string Redact(string value)
    {
        foreach (var secret in Volatile.Read(ref _knownSecrets))
        {
            value = value.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        value = Bearer.Replace(value, "Bearer " + Replacement);
        value = NamedSecret.Replace(value, "$1" + Replacement);
        return GoogleToken.Replace(value, Replacement);
    }
}
