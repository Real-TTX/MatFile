using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppInfo _appInfo;
    private readonly MatFilePaths _paths;
    private readonly IHostEnvironment _environment;

    public IndexModel(AppInfo appInfo, MatFilePaths paths, IHostEnvironment environment)
    {
        _appInfo = appInfo;
        _paths = paths;
        _environment = environment;
    }

    public string Version => _appInfo.Version;
    public string Channel => _appInfo.Channel;
    public string Environment => _environment.EnvironmentName;
    public string DataDirectory => _paths.DataDirectory;
    public DateTimeOffset StartedAt => _appInfo.StartedAt;
    public bool DataWritable { get; private set; }

    public void OnGet()
    {
        DataWritable = _paths.CanWrite();
    }
}
