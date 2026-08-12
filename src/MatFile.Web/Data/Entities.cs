namespace MatFile.Web.Data;

/// <summary>
/// Basis für alle Tabellen: Primärschlüssel "Id" (SQLite INTEGER = 64 Bit) und Audit-Felder,
/// die der <see cref="AuditInterceptor"/> automatisch füllt.
/// </summary>
public abstract class EntityBase
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
}

// ---------------------------------------------------------------- Benutzer & Rechte

/// <summary>Rechte einer Rolle. Feingranulare Zugriffe auf Roots regelt zusätzlich <see cref="RootAccess"/>.</summary>
[Flags]
public enum Permission
{
    None = 0,
    UseExplorer = 1 << 0,
    ManageUsers = 1 << 1,
    ManageRoles = 1 << 2,
    ManageRoots = 1 << 3,
    ManageMounts = 1 << 4,
    ManageSettings = 1 << 5,
    ViewActivityLog = 1 << 6,

    All = UseExplorer | ManageUsers | ManageRoles | ManageRoots | ManageMounts | ManageSettings | ViewActivityLog
}

public class User : EntityBase
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginDate { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }

    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserSession> Sessions { get; set; } = [];
}

public class Role : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Systemrollen können nicht gelöscht werden.</summary>
    public bool IsSystem { get; set; }
    public Permission Permissions { get; set; }

    public List<UserRole> UserRoles { get; set; } = [];
}

public class UserRole : EntityBase
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public long RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>
/// Anmeldesitzung. Der Token im Cookie ist das Sicherheitsmerkmal, nicht die Id.
/// Die Sitzung liegt in der Datenbank und übersteht damit einen Container-Neustart.
/// </summary>
public class UserSession : EntityBase
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public DateTime ExpiresDate { get; set; }
    public DateTime LastActivityDate { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool IsRevoked { get; set; }
}

// ---------------------------------------------------------------- Speicherorte

public enum StorageType
{
    Local,
    Smb,
    Nfs,
    Sftp,
    Ftp,
    WebDav
}

public enum StoragePersistence
{
    /// <summary>Bleibt dauerhaft eingebunden und wird beim Start wiederhergestellt.</summary>
    Persistent,
    /// <summary>Gilt nur für die aktuelle Sitzung; Zugangsdaten werden nicht gespeichert.</summary>
    Session
}

/// <summary>Ein Home-/Root-Pfad oder eine eingebundene Freigabe. Nur diese Pfade sind im Explorer sichtbar.</summary>
public class StorageRoot : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public StorageType StorageType { get; set; }
    public string? DisplayIcon { get; set; }
    /// <summary>Lokaler Pfad im Container bzw. Basispfad auf der Gegenstelle.</summary>
    public string BasePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public StoragePersistence Persistence { get; set; }
    /// <summary>Verbindungsdaten (Host, Port, Share, Benutzer, Optionen) als JSON.</summary>
    public string? SettingsJson { get; set; }
    /// <summary>Kennwort/Schlüssel, verschlüsselt über DataProtection.</summary>
    public string? SecretEncrypted { get; set; }

    public List<RootAccess> Access { get; set; } = [];
}

public class RootAccess : EntityBase
{
    public long StorageRootId { get; set; }
    public StorageRoot? StorageRoot { get; set; }
    public long RoleId { get; set; }
    public Role? Role { get; set; }

    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public bool CanUpload { get; set; }
    public bool CanDownload { get; set; } = true;
    public bool CanShare { get; set; }
}

// ---------------------------------------------------------------- Explorer-Zustand

public class Bookmark : EntityBase
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;
    public long StorageRootId { get; set; }
    public StorageRoot? StorageRoot { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? DisplayIcon { get; set; }
    public int SortOrder { get; set; }
}

public class ExplorerTab : EntityBase
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public string Title { get; set; } = string.Empty;
    public long? StorageRootId { get; set; }
    public StorageRoot? StorageRoot { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    /// <summary>Angeheftete Tabs werden nach dem Neuladen wiederhergestellt.</summary>
    public bool IsPersistent { get; set; }
    public bool IsActive { get; set; }
}

public class UserSetting : EntityBase
{
    public long UserId { get; set; }
    public User? User { get; set; }
    public string SettingKey { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
}

// ---------------------------------------------------------------- Hintergrundjobs & Protokoll

public enum JobType
{
    Copy,
    Move,
    Delete,
    Zip,
    Unzip,
    Upload
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class JobTask : EntityBase
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; }
    public int ProgressPercent { get; set; }
    public string? PayloadJson { get; set; }
    public string? ResultPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? FinishedDate { get; set; }
}

public class ActivityLog : EntityBase
{
    public long? UserId { get; set; }
    public User? User { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public long? StorageRootId { get; set; }
    public string? TargetPath { get; set; }
    public string? DetailsJson { get; set; }
    public string? IpAddress { get; set; }
}
