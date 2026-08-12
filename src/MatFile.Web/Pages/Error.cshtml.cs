using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true, Name = "code")]
    public int? ErrorCode { get; set; }

    public string? RequestId { get; private set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public string Title { get; private set; } = "Es ist ein Fehler aufgetreten";
    public string Message { get; private set; } = "Die Anfrage konnte nicht verarbeitet werden.";

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        switch (ErrorCode)
        {
            case 404:
                Title = "Seite nicht gefunden";
                Message = "Die aufgerufene Adresse existiert nicht.";
                break;
            case 401:
            case 403:
                Title = "Kein Zugriff";
                Message = "Für diesen Bereich fehlen die nötigen Rechte.";
                break;
            default:
                if (ErrorCode.HasValue)
                {
                    Title = $"Fehler {ErrorCode}";
                }
                break;
        }
    }
}
