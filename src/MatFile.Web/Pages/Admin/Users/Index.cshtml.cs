using MatFile.Web.Controls;
using MatFile.Web.Security;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Admin.Users;

public class IndexModel : PageModel
{
    private readonly UserService _users;
    private readonly IStringLocalizer<SharedResource> _text;

    public IndexModel(UserService users, IStringLocalizer<SharedResource> text)
    {
        _users = users;
        _text = text;
    }

    [BindProperty(SupportsGet = true, Name = "search")]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = "dir")]
    public string? Dir { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "pageSize")]
    public int PageSize { get; set; } = ListQuery.DefaultPageSize;

    /// <summary>Filter: leer = alle, "active", "inactive".</summary>
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty]
    public List<long> SelectedIds { get; set; } = [];

    public PagedResult<UserService.UserListItem> Result { get; private set; } = new();
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<TableColumn> Columns => new List<TableColumn>
    {
        new(string.Empty, null, "mf-col-select", "36px"),
        new(_text["User.Username"], "username"),
        new(_text["User.DisplayName"], "displayname"),
        new(_text["User.Email"], "email"),
        new(_text["User.Roles"]),
        new(_text["User.Status"], "active"),
        new(_text["User.LastLogin"], "lastlogin"),
        new(string.Empty, null, "mf-col-actions", "60px")
    };

    public string CurrentUrl => Request.Path + Request.QueryString;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (SelectedIds.Count == 0)
        {
            ErrorMessage = _text["Common.NothingSelected"];
            await LoadAsync();
            return Page();
        }

        var currentUserId = User.GetUserId() ?? 0;
        var deleted = 0;

        foreach (var id in SelectedIds)
        {
            var result = await _users.DeleteAsync(id, currentUserId);
            if (result.Success)
            {
                deleted++;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }

        if (deleted > 0)
        {
            StatusMessage = _text["User.Deleted", deleted];
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUnlockAsync(long id)
    {
        var result = await _users.UnlockAsync(id);
        if (result.Success)
        {
            StatusMessage = _text["User.Unlocked"];
        }
        else
        {
            ErrorMessage = result.Error;
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        bool? isActive = Status switch
        {
            "active" => true,
            "inactive" => false,
            _ => null
        };

        Result = await _users.ListAsync(new ListQuery
        {
            Search = Search,
            Sort = Sort,
            Dir = Dir,
            Page = PageNumber,
            PageSize = PageSize
        }, isActive);
    }
}
