using MatFile.Web.Controls;
using MatFile.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Pages.Admin.Roles;

public class IndexModel : PageModel
{
    private readonly RoleService _roles;
    private readonly IStringLocalizer<SharedResource> _text;

    public IndexModel(RoleService roles, IStringLocalizer<SharedResource> text)
    {
        _roles = roles;
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

    [BindProperty]
    public List<long> SelectedIds { get; set; } = [];

    public PagedResult<RoleService.RoleListItem> Result { get; private set; } = new();
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<TableColumn> Columns => new List<TableColumn>
    {
        new(string.Empty, null, "mf-col-select", "36px"),
        new(_text["Role.Name"], "name"),
        new(_text["Role.Description"], "description"),
        new(_text["Role.Permissions"]),
        new(_text["Role.UserCount"], null, "mf-col-number", "90px"),
        new(string.Empty, null, "mf-col-actions", "60px")
    };

    public string CurrentUrl => Request.Path + Request.QueryString;

    public string PermissionLabel(Data.Permission permission) => _roles.PermissionLabel(permission);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (SelectedIds.Count == 0)
        {
            ErrorMessage = _text["Common.NothingSelected"];
            await LoadAsync();
            return Page();
        }

        var deleted = 0;
        foreach (var id in SelectedIds)
        {
            var result = await _roles.DeleteAsync(id);
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
            StatusMessage = _text["Role.Deleted", deleted];
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Result = await _roles.ListAsync(new ListQuery
        {
            Search = Search,
            Sort = Sort,
            Dir = Dir,
            Page = PageNumber,
            PageSize = PageSize
        });
    }
}
