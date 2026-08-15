using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    private readonly IStringLocalizer<SharedResource> _text;

    public ErrorModel(IStringLocalizer<SharedResource> text) => _text = text;

    [BindProperty(SupportsGet = true, Name = "code")]
    public int? ErrorCode { get; set; }

    public string? RequestId { get; private set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;

    /// <summary>
    /// Scheitert ein POST (etwa ein gebremster Anmeldeversuch), führt die Fehlerseite
    /// die ursprüngliche Methode aus - sonst bliebe die Seite ohne Text.
    /// </summary>
    public void OnPost() => OnGet();

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        Title = _text["Error.Title"];
        Message = _text["Error.Message"];

        switch (ErrorCode)
        {
            case 404:
                Title = _text["Error.NotFoundTitle"];
                Message = _text["Error.NotFoundMessage"];
                break;
            case 401:
            case 403:
                Title = _text["Error.ForbiddenTitle"];
                Message = _text["Error.ForbiddenMessage"];
                break;
            case 429:
                Title = _text["Error.TooManyTitle"];
                Message = _text["Error.TooManyMessage"];
                break;
            default:
                if (ErrorCode.HasValue)
                {
                    Title = _text["Error.CodeTitle", ErrorCode.Value];
                }
                break;
        }
    }
}
