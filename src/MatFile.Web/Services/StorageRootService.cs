using System.Security.Claims;
using MatFile.Web.Controls;
using MatFile.Web.Data;
using MatFile.Web.Security;
using MatFile.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Services;

/// <summary>
/// Verwaltung der Home-/Root-Pfade und der Zugriffsrechte je Rolle,
/// plus die Auflösung "welche Roots darf dieser Benutzer sehen".
/// </summary>
public sealed class StorageRootService
{
    private readonly MatFileDbContext _db;
    private readonly MatFilePaths _paths;
    private readonly StorageProviderFactory _providers;
    private readonly SecretProtector _protector;
    private readonly IStringLocalizer<SharedResource> _text;

    public StorageRootService(
        MatFileDbContext db,
        MatFilePaths paths,
        StorageProviderFactory providers,
        SecretProtector protector,
        IStringLocalizer<SharedResource> text)
    {
        _db = db;
        _paths = paths;
        _providers = providers;
        _protector = protector;
        _text = text;
    }

    /// <summary>
    /// Prüft eine Verbindung, ohne sie zu speichern. Ohne neues Kennwort wird
    /// das bereits hinterlegte verwendet.
    /// </summary>
    public async Task<SaveResult> TestConnectionAsync(
        long? id, string name, StorageType storageType, string basePath, ConnectionSettings connection, string? password)
    {
        if (!StorageProviderFactory.IsSupported(storageType))
        {
            return new SaveResult(false, _text["Root.Error.TypeNotSupported"]);
        }

        var secret = password;

        if (string.IsNullOrEmpty(secret) && id is { } rootId)
        {
            var stored = await _db.StorageRoot.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootId);
            secret = _protector.Unprotect(stored?.SecretEncrypted);
        }

        var probe = new StorageRoot
        {
            Id = id ?? 0,
            Name = name ?? string.Empty,
            StorageType = storageType,
            // Leere Formularfelder kommen als null an.
            BasePath = (basePath ?? string.Empty).Trim()
        };

        try
        {
            var provider = _providers.Create(probe, connection, secret);
            var available = await provider.IsAvailableAsync();

            return available
                ? new SaveResult(true)
                : new SaveResult(false, _text["Root.Error.NotReachable"]);
        }
        catch (StorageException exception)
        {
            return new SaveResult(false, _text["Root.Error.NotReachable"] + " (" + exception.Code + ")");
        }
        catch (Exception exception)
        {
            return new SaveResult(false, _text["Root.Error.NotReachable"] + " (" + exception.GetType().Name + ")");
        }
    }

    /// <summary>
    /// Ordner einer noch nicht gespeicherten Verbindung auflisten - für die Auswahl
    /// im Dialog. Bei SMB ohne Freigabe kommen erst die Freigaben.
    /// </summary>
    public async Task<IResult> BrowseAsync(
        StorageType storageType,
        string? basePath,
        ConnectionSettings connection,
        string? password,
        string path,
        CancellationToken cancellationToken)
    {
        if (!StorageProviderFactory.IsSupported(storageType))
        {
            return Results.Json(new { error = "type_not_supported" }, statusCode: StatusCodes.Status400BadRequest);
        }

        // Beim Durchsehen zählt die ganze Gegenstelle, nicht der eingetragene Unterordner.
        var probe = new StorageRoot
        {
            Name = "browse",
            StorageType = storageType,
            BasePath = storageType == StorageType.Local ? "/" : string.Empty
        };

        var provider = _providers.Create(probe, connection, password);

        if (provider is SmbStorageProvider smb && string.IsNullOrWhiteSpace(connection.Share))
        {
            var shares = await smb.ListSharesAsync();

            return Results.Ok(new
            {
                mode = "shares",
                path = string.Empty,
                parentPath = (string?)null,
                entries = shares.Select(share => new { name = share, path = string.Empty })
            });
        }

        var listing = await provider.ListAsync(path, cancellationToken);

        return Results.Ok(new
        {
            mode = "folders",
            path = listing.Path,
            listing.ParentPath,
            entries = listing.Entries
                .Where(entry => entry.IsDirectory && !entry.IsHidden)
                .Select(entry => new { entry.Name, entry.Path })
        });
    }

    public record RootListItem(
        long Id,
        string Name,
        StorageType StorageType,
        string BasePath,
        bool IsEnabled,
        int SortOrder,
        bool IsAvailable,
        int RoleCount);

    public record RoleAccess(long RoleId, string RoleName, RootPermissions Permissions);

    // ---------------------------------------------------------------- Verwaltung

    public async Task<PagedResult<RootListItem>> ListAsync(ListQuery query)
    {
        var roots = _db.StorageRoot.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            roots = roots.Where(r => EF.Functions.Like(r.Name, $"%{search}%")
                                     || EF.Functions.Like(r.BasePath, $"%{search}%"));
        }

        roots = query.Sort?.ToLowerInvariant() switch
        {
            "path" => query.Descending ? roots.OrderByDescending(r => r.BasePath) : roots.OrderBy(r => r.BasePath),
            "type" => query.Descending ? roots.OrderByDescending(r => r.StorageType) : roots.OrderBy(r => r.StorageType),
            "enabled" => query.Descending ? roots.OrderByDescending(r => r.IsEnabled) : roots.OrderBy(r => r.IsEnabled),
            "name" => query.Descending ? roots.OrderByDescending(r => r.Name) : roots.OrderBy(r => r.Name),
            _ => query.Descending
                ? roots.OrderByDescending(r => r.SortOrder).ThenByDescending(r => r.Name)
                : roots.OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
        };

        var total = await roots.CountAsync();

        var page = await roots
            .Skip(query.Skip)
            .Take(query.NormalizedPageSize)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.StorageType,
                r.BasePath,
                r.IsEnabled,
                r.SortOrder,
                RoleCount = r.Access.Count()
            })
            .ToListAsync();

        return new PagedResult<RootListItem>
        {
            Items = page.Select(r => new RootListItem(
                r.Id, r.Name, r.StorageType, r.BasePath, r.IsEnabled, r.SortOrder,
                r.StorageType == StorageType.Local && Directory.Exists(r.BasePath),
                r.RoleCount)).ToList(),
            Page = query.NormalizedPage,
            PageSize = query.NormalizedPageSize,
            TotalCount = total
        };
    }

    public Task<StorageRoot?> GetAsync(long id)
        => _db.StorageRoot.Include(r => r.Access).FirstOrDefaultAsync(r => r.Id == id);

    /// <summary>Alle Rollen mit ihren aktuellen Rechten auf diesem Root (auch die ohne Zuweisung).</summary>
    public async Task<List<RoleAccess>> GetRoleAccessAsync(long? rootId)
    {
        var roles = await _db.Role.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

        var access = rootId is { } id
            ? await _db.RootAccess.AsNoTracking().Where(a => a.StorageRootId == id).ToListAsync()
            : [];

        return roles.Select(role =>
        {
            var entry = access.FirstOrDefault(a => a.RoleId == role.Id);
            return new RoleAccess(role.Id, role.Name,
                entry is null ? RootPermissions.None : RootPermissions.From(entry));
        }).ToList();
    }

    public async Task<SaveResult> SaveAsync(
        long? id,
        string name,
        StorageType storageType,
        string basePath,
        string? displayIcon,
        int sortOrder,
        bool isEnabled,
        IReadOnlyList<RoleAccess> access,
        ConnectionSettings? connection = null,
        string? password = null,
        bool clearPassword = false)
    {
        // Leere Formularfelder bindet ASP.NET als null.
        name = (name ?? string.Empty).Trim();
        basePath = (basePath ?? string.Empty).Trim();
        connection ??= new ConnectionSettings();

        if (name.Length == 0)
        {
            return new SaveResult(false, _text["Root.Error.NameRequired"]);
        }

        if (await _db.StorageRoot.AnyAsync(r => r.Name == name && r.Id != id))
        {
            return new SaveResult(false, _text["Root.Error.NameTaken"]);
        }

        if (!StorageProviderFactory.IsSupported(storageType))
        {
            return new SaveResult(false, _text["Root.Error.TypeNotSupported"]);
        }

        if (StorageProviderFactory.NeedsConnection(storageType))
        {
            var hasTarget = storageType == StorageType.WebDav
                ? !string.IsNullOrWhiteSpace(connection.Url) || !string.IsNullOrWhiteSpace(connection.Host)
                : !string.IsNullOrWhiteSpace(connection.Host);

            if (!hasTarget)
            {
                return new SaveResult(false, _text["Root.Error.HostRequired"]);
            }
        }

        if (storageType == StorageType.Local)
        {
            if (basePath.Length == 0)
            {
                return new SaveResult(false, _text["Root.Error.PathRequired"]);
            }

            if (!Path.IsPathRooted(basePath))
            {
                return new SaveResult(false, _text["Root.Error.PathNotAbsolute"]);
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));

            if (!Directory.Exists(fullPath))
            {
                return new SaveResult(false, _text["Root.Error.PathMissing"]);
            }

            // Das eigene Datenverzeichnis (Datenbank, Schlüssel) darf nie freigegeben werden.
            // Ein Pfad darüber - etwa "/" - ist erlaubt, der Provider blendet /data dann aus.
            var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.DataDirectory));
            if (PathGuard.IsInside(dataDirectory, fullPath))
            {
                return new SaveResult(false, _text["Root.Error.PathIsDataDirectory"]);
            }

            basePath = fullPath;
        }

        StorageRoot root;

        if (id is { } rootId)
        {
            var existing = await GetAsync(rootId);
            if (existing is null)
            {
                return new SaveResult(false, _text["Common.NotFound"]);
            }

            root = existing;
        }
        else
        {
            root = new StorageRoot();
            _db.StorageRoot.Add(root);
        }

        root.Name = name;
        root.StorageType = storageType;
        root.BasePath = basePath;
        root.DisplayIcon = string.IsNullOrWhiteSpace(displayIcon) ? null : displayIcon.Trim();
        root.SortOrder = sortOrder;
        root.IsEnabled = isEnabled;
        root.Persistence = StoragePersistence.Persistent;

        if (StorageProviderFactory.NeedsConnection(storageType))
        {
            root.SettingsJson = connection.ToJson();

            // Ein leeres Kennwortfeld lässt das gespeicherte Kennwort unangetastet.
            if (clearPassword)
            {
                root.SecretEncrypted = null;
            }
            else if (!string.IsNullOrEmpty(password))
            {
                root.SecretEncrypted = _protector.Protect(password);
            }
        }
        else
        {
            root.SettingsJson = null;
            root.SecretEncrypted = null;
        }

        await _db.SaveChangesAsync();
        await UpdateAccessAsync(root.Id, access);

        return new SaveResult(true, null, root.Id);
    }

    public async Task<SaveResult> DeleteAsync(long id)
    {
        var root = await _db.StorageRoot.FirstOrDefaultAsync(r => r.Id == id);
        if (root is null)
        {
            return new SaveResult(false, _text["Common.NotFound"]);
        }

        _db.StorageRoot.Remove(root);
        await _db.SaveChangesAsync();
        return new SaveResult(true);
    }

    private async Task UpdateAccessAsync(long rootId, IReadOnlyList<RoleAccess> access)
    {
        var current = await _db.RootAccess.Where(a => a.StorageRootId == rootId).ToListAsync();

        foreach (var item in access)
        {
            var permissions = item.Permissions;
            var entry = current.FirstOrDefault(a => a.RoleId == item.RoleId);

            var hasAnyRight = permissions.CanRead || permissions.CanWrite || permissions.CanDelete
                              || permissions.CanUpload || permissions.CanDownload;

            if (!hasAnyRight)
            {
                if (entry is not null)
                {
                    _db.RootAccess.Remove(entry);
                }

                continue;
            }

            entry ??= AddAccess(rootId, item.RoleId);

            entry.CanRead = permissions.CanRead;
            entry.CanWrite = permissions.CanWrite;
            entry.CanDelete = permissions.CanDelete;
            entry.CanUpload = permissions.CanUpload;
            entry.CanDownload = permissions.CanDownload;
        }

        await _db.SaveChangesAsync();
    }

    private RootAccess AddAccess(long rootId, long roleId)
    {
        var entry = new RootAccess { StorageRootId = rootId, RoleId = roleId };
        _db.RootAccess.Add(entry);
        return entry;
    }

    // ---------------------------------------------------------------- Zugriff im Explorer

    /// <summary>Alle Roots, die der Benutzer sehen darf, mit den effektiven Rechten.</summary>
    public Task<List<AccessibleRoot>> GetAccessibleRootsAsync(ClaimsPrincipal user)
        => user.GetUserId() is { } userId ? GetAccessibleRootsAsync(userId) : Task.FromResult(new List<AccessibleRoot>());

    /// <summary>
    /// Gleiche Auflösung ohne Anmeldekontext - wird von Hintergrundjobs gebraucht,
    /// die keinen ClaimsPrincipal haben.
    /// </summary>
    public async Task<List<AccessibleRoot>> GetAccessibleRootsAsync(long userId)
    {
        var roots = await _db.StorageRoot.AsNoTracking()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync();

        var roleIds = await _db.UserRole.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync();
        if (roleIds.Count == 0)
        {
            return [];
        }

        var managesRoots = await _db.Role
            .Where(r => roleIds.Contains(r.Id))
            .AnyAsync(r => (r.Permissions & Permission.ManageRoots) == Permission.ManageRoots);

        if (managesRoots)
        {
            return roots.Select(r => new AccessibleRoot(
                r.Id, r.Name, r.StorageType, r.DisplayIcon, r.SortOrder, RootPermissions.Full)).ToList();
        }

        var access = await _db.RootAccess.AsNoTracking()
            .Where(a => roleIds.Contains(a.RoleId))
            .ToListAsync();

        var result = new List<AccessibleRoot>();

        foreach (var root in roots)
        {
            var permissions = access
                .Where(a => a.StorageRootId == root.Id)
                .Aggregate(RootPermissions.None, (current, entry) => current.Merge(RootPermissions.From(entry)));

            if (permissions.CanRead)
            {
                result.Add(new AccessibleRoot(root.Id, root.Name, root.StorageType, root.DisplayIcon, root.SortOrder, permissions));
            }
        }

        return result;
    }

    /// <summary>Root samt Provider und Rechten - oder null, wenn kein Zugriff besteht.</summary>
    public Task<(StorageRoot Root, IStorageProvider Provider, RootPermissions Permissions)?> OpenAsync(
        ClaimsPrincipal user, long rootId)
        => user.GetUserId() is { } userId
            ? OpenAsync(userId, rootId)
            : Task.FromResult<(StorageRoot, IStorageProvider, RootPermissions)?>(null);

    public async Task<(StorageRoot Root, IStorageProvider Provider, RootPermissions Permissions)?> OpenAsync(
        long userId, long rootId)
    {
        var accessible = await GetAccessibleRootsAsync(userId);
        var match = accessible.FirstOrDefault(r => r.Id == rootId);
        if (match is null)
        {
            return null;
        }

        var root = await _db.StorageRoot.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootId);
        if (root is null)
        {
            return null;
        }

        return (root, _providers.Create(root), match.Permissions);
    }
}
