using MatFile.Web.Data;
using MatFile.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatFile.Web.Pages.Admin;

/// <summary>Leitet in den ersten Bereich weiter, für den Rechte vorliegen.</summary>
public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.HasPermission(Permission.ManageUsers))
        {
            return RedirectToPage("/Admin/Users/Index");
        }

        if (User.HasPermission(Permission.ManageRoles))
        {
            return RedirectToPage("/Admin/Roles/Index");
        }

        if (User.HasPermission(Permission.ManageRoots))
        {
            return RedirectToPage("/Admin/Roots/Index");
        }

        return Forbid();
    }
}
