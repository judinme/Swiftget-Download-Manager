using System;
using System.Text.RegularExpressions;

namespace SwiftGet.Engine.Diagnostics;

public static partial class RedactionEngine
{
    private static readonly Regex UrlCredentialsRegex = new(@"https?://([^:]+):([^@]+)@", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuerySecretsRegex = new(@"([?&](?:password|token|secret|key|sig|api[_-]?key|auth|bearer)=)[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AuthorizationHeaderRegex = new(@"(Authorization:\s*(?:Bearer|Basic)\s+)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenHeaderRegex = new(@"(X-SwiftGet-Token:\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CookieHeaderRegex = new(@"(Cookie:\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        // 1. Redact user home directory names (e.g. C:\Users\john_doe\... -> C:\Users\<USER>\...)
        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length > 1)
        {
            result = result.Replace(userName, "<USER>", StringComparison.OrdinalIgnoreCase);
        }

        // 2. Redact embedded credentials in URLs (http://user:pass@example.com -> http://<REDACTED>@example.com)
        result = UrlCredentialsRegex.Replace(result, "http://<REDACTED>@");

        // 3. Redact query string secrets (e.g. ?token=xyz123 -> ?token=<REDACTED>)
        result = QuerySecretsRegex.Replace(result, "$1<REDACTED>");

        // 4. Redact sensitive headers
        result = AuthorizationHeaderRegex.Replace(result, "$1<REDACTED>");
        result = TokenHeaderRegex.Replace(result, "$1<REDACTED>");
        result = CookieHeaderRegex.Replace(result, "$1<REDACTED>");

        return result;
    }
}
