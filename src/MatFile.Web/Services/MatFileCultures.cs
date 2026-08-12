using Microsoft.AspNetCore.Localization;

namespace MatFile.Web.Services;

/// <summary>Unterstützte Oberflächensprachen. Englisch ist Standard, Deutsch ist auswählbar.</summary>
public static class MatFileCultures
{
    public const string Default = "en";
    public const string UserSettingKey = "Language";

    /// <summary>Sprachcode und Anzeigename für die Sprachauswahl.</summary>
    public static readonly (string Code, string Label)[] Supported =
    [
        ("en", "EN"),
        ("de", "DE")
    ];

    public static string[] Codes => Supported.Select(c => c.Code).ToArray();

    public static bool IsSupported(string? culture)
        => !string.IsNullOrWhiteSpace(culture)
           && Supported.Any(c => string.Equals(c.Code, culture, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? culture)
        => IsSupported(culture) ? culture!.ToLowerInvariant() : Default;

    /// <summary>Schreibt die Sprachwahl in das Standard-Culture-Cookie von ASP.NET Core.</summary>
    public static void SetCookie(HttpResponse response, string culture)
    {
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Normalize(culture))),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
    }
}
