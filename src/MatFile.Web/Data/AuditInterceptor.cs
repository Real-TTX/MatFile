using MatFile.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MatFile.Web.Data;

/// <summary>Füllt CreateDate/CreateUserId und UpdateDate/UpdateUserId bei jedem Speichern.</summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _currentUser;

    public AuditInterceptor(ICurrentUser currentUser) => _currentUser = currentUser;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var userId = _currentUser.UserId;

        foreach (EntityEntry<EntityBase> entry in context.ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreateDate = now;
                entry.Entity.CreateUserId ??= userId;
                entry.Entity.UpdateDate = now;
                entry.Entity.UpdateUserId ??= userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdateDate = now;
                entry.Entity.UpdateUserId = userId ?? entry.Entity.UpdateUserId;
                entry.Property(x => x.CreateDate).IsModified = false;
                entry.Property(x => x.CreateUserId).IsModified = false;
            }
        }
    }
}
