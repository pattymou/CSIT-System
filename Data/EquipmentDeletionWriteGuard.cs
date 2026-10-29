using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

internal static class EquipmentDeletionWriteGuard
{
    internal static HashSet<string> AffectedEquipment(AppDbContext db)
    {
        var changed = db.ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        return changed.Where(x => x.Entity is Apparatus &&
                !(x.State == EntityState.Deleted && db.EquipmentDeletionCleanupJobId.HasValue))
            .Select(x => ((Apparatus)x.Entity).Id)
            .Concat(changed.Where(x => x.Entity is ApparatusFile && x.State != EntityState.Deleted)
                .Select(x => ((ApparatusFile)x.Entity).ApparatusId))
            .Concat(changed.Where(x => x.Entity is ReservationItem)
                .Select(x => ((ReservationItem)x.Entity).ApparatusId))
            .Concat(changed.Where(x => x.Entity is EquipmentGroupDevice && x.State != EntityState.Deleted)
                .Select(x => ((EquipmentGroupDevice)x.Entity).ApparatusId))
            .Concat(changed.Where(x => x.Entity is ApparatusResourceCapability && x.State != EntityState.Deleted)
                .Select(x => ((ApparatusResourceCapability)x.Entity).ApparatusId))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
    }

    internal static async Task LockAsync(AppDbContext db, string equipmentId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational()) return;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT \"Id\" FROM apparatus WHERE \"Id\" = @equipmentId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "equipmentId";
        parameter.Value = equipmentId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
