using System.Security.Claims;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppInfo _appInfo;
    private readonly MatFilePaths _paths;
    private readonly IHostEnvironment _environment;
    private readonly MatFileDbContext _db;

    public IndexModel(AppInfo appInfo, MatFilePaths paths, IHostEnvironment environment, MatFileDbContext db)
    {
        _appInfo = appInfo;
        _paths = paths;
        _environment = environment;
        _db = db;
    }

    public string Version => _appInfo.Version;
    public string Channel => _appInfo.Channel;
    public string Environment => _environment.EnvironmentName;
    public string DataDirectory => _paths.DataDirectory;
    public string DisplayName => User.GetDisplayName();
    public string Username => User.Identity?.Name ?? string.Empty;

    public IReadOnlyList<string> Roles { get; private set; } = [];
    public IReadOnlyList<string> Permissions { get; private set; } = [];
    public DateTime? SessionExpires { get; private set; }
    public int ActiveSessionCount { get; private set; }
    public bool DatabaseReady { get; private set; }

    public async Task OnGetAsync()
    {
        Roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        Permissions = User.FindAll(MatFileClaims.PermissionClaim).Select(c => c.Value).ToList();
        DatabaseReady = await _db.Database.CanConnectAsync();

        var userId = User.GetUserId();
        var token = User.GetSessionToken();
        var now = DateTime.UtcNow;

        if (userId is { } currentUserId)
        {
            ActiveSessionCount = await _db.UserSession
                .CountAsync(s => s.UserId == currentUserId && !s.IsRevoked && s.ExpiresDate > now);
        }

        if (token is { } currentToken)
        {
            SessionExpires = await _db.UserSession
                .Where(s => s.Token == currentToken)
                .Select(s => (DateTime?)s.ExpiresDate)
                .FirstOrDefaultAsync();
        }
    }
}
