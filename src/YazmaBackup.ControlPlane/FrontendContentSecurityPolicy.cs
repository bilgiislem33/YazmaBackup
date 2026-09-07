using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YazmaBackup.ControlPlane;

internal static partial class FrontendContentSecurityPolicy
{
    internal static string Build(string webRootPath)
    {
        var scriptSources = new List<string> { "'self'" };

        var indexPath = Path.Combine(webRootPath, "index.html");
        if (File.Exists(indexPath))
        {
            var html = File.ReadAllText(indexPath, Encoding.UTF8);

            foreach (Match match in InlineScriptRegex().Matches(html))
            {
                var body = match.Groups["body"].Value;
                if (body.Length == 0)
                {
                    continue;
                }

                var hash = Convert.ToBase64String(
                    SHA256.HashData(Encoding.UTF8.GetBytes(body)));

                scriptSources.Add($"'sha256-{hash}'");
            }
        }

        return
            "default-src 'self'; " +
            $"script-src {string.Join(" ", scriptSources.Distinct(StringComparer.Ordinal))}; " +
            "style-src 'self'; " +
            "img-src 'self' data:; " +
            "connect-src 'self'; " +
            "object-src 'none'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'none'; " +
            "form-action 'self'";
    }


    private static Regex InlineScriptRegex()
    {
        return new Regex(
            @"<script(?![^>]*\bsrc\s*=)[^>]*>(?<body>.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);
    }
}
