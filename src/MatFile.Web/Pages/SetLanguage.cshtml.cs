using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatFile.Web.Pages;

/// <summary>Wechselt die Oberflächensprache. Für angemeldete Benutzer wird die Wahl gespeichert.</summary>
[AllowAnonymous]
public class SetLanguageModel : PageModel
{
    private readonly MatFileDbContext _db;

    public SetLanguageModel(MatFileDbContext db) => _db = db;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync(string culture, string? returnUrl)
    {
        var selected = MatFileCultures.Normalize(culture);
        MatFileCultures.SetCookie(Response, selected);

        if (User.GetUserId() is { } userId)
        {
            var setting = await _db.UserSetting
                .FirstOrDefaultAsync(s => s.UserId == userId && s.SettingKey == MatFileCultures.UserSettingKey);

            if (setting is null)
            {
                _db.UserSetting.Add(new UserSetting
                {
                    UserId = userId,
                    SettingKey = MatFileCultures.UserSettingKey,
                    SettingValue = selected
                });
            }
            else
            {
                setting.SettingValue = selected;
            }

            await _db.SaveChangesAsync();
        }

        return Redirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
