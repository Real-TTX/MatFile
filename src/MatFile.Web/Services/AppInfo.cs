using System.Reflection;

namespace MatFile.Web.Services;

/// <summary>
/// Versions- und Laufzeitinformationen der Anwendung (Footer, About-Seite, /healthz).
/// </summary>
public sealed class AppInfo
{
    public const string ProductName = "MatFile";

    public string Version { get; }
    public string Channel { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public AppInfo()
    {
        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        // Der Buildhash, den das SDK anhängt (1.0.0+abc123), interessiert in der UI nicht.
        var plus = Version.IndexOf('+');
        if (plus > 0)
        {
            Version = Version[..plus];
        }

        Channel = Version.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase) ? "Development"
            : Version.StartsWith("local-", StringComparison.OrdinalIgnoreCase) ? "Local"
            : "Release";
    }
}
