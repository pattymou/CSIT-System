using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

// One record lock serializes an accepted-case write with the deletion confirmation.
// The guard runs before every tracked write, including background commits and retries.
internal static class VerificationCaseDeletionWriteGuard
{
    internal static async Task<HashSet<Guid>> AffectedRecordsAsync(AppDbContext db,
        CancellationToken cancellationToken)
    {
        var changed = db.ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(x => x.Entity).ToList();
        var records = changed.OfType<ModuleRecord>().Select(x => x.Id)
            .Concat(changed.OfType<ModuleRecordCase>().Select(x => x.RecordId))
            .Concat(changed.OfType<ModuleCaseFile>().Select(x => x.RecordId))
            .Concat(changed.OfType<VerificationApplication>()
                .Where(x => x.ModuleRecordId.HasValue).Select(x => x.ModuleRecordId!.Value))
            .Concat(changed.OfType<KmIntegrationOutbox>()
                .Where(x => x.ProjectId.HasValue).Select(x => x.ProjectId!.Value))
            .Where(x => x != Guid.Empty).ToHashSet();

        var caseIds = changed.OfType<ModuleRecordTask>().Select(x => x.CaseId)
            .Where(x => x != Guid.Empty).Distinct().ToList();
        if (caseIds.Count > 0)
            records.UnionWith(await db.ModuleRecordCases.AsNoTracking()
                .Where(x => caseIds.Contains(x.Id)).Select(x => x.RecordId)
                .ToListAsync(cancellationToken));

        var applicationIds = changed.OfType<VerificationApplicationFile>()
            .Select(x => x.VerificationApplicationId)
            .Where(x => x != Guid.Empty).Distinct().ToList();
        var attachmentIds = changed.OfType<KmIntegrationOutbox>()
            .Where(x => x.ProjectId is null).Select(x => x.SourceEntityId)
            .Where(x => x != Guid.Empty).Distinct().ToList();
        if (attachmentIds.Count > 0)
            applicationIds.AddRange(await db.VerificationApplicationFiles.AsNoTracking()
                .Where(x => attachmentIds.Contains(x.Id))
                .Select(x => x.VerificationApplicationId).ToListAsync(cancellationToken));

        var documentIds = changed.OfType<DocumentLibraryFile>().Select(x => x.Id)
            .Concat(changed.OfType<DocumentProcessingJob>().Select(x => x.DocumentFileId))
            .Where(x => x != Guid.Empty).Distinct().ToList();
        if (documentIds.Count > 0)
            applicationIds.AddRange(await db.VerificationApplicationFiles.AsNoTracking()
                .Where(x => x.DocumentLibraryFileId.HasValue &&
                    documentIds.Contains(x.DocumentLibraryFileId.Value))
                .Select(x => x.VerificationApplicationId).ToListAsync(cancellationToken));

        if (applicationIds.Count > 0)
            records.UnionWith(await db.VerificationApplications.AsNoTracking()
                .Where(x => applicationIds.Contains(x.Id) && x.ModuleRecordId.HasValue)
                .Select(x => x.ModuleRecordId!.Value).ToListAsync(cancellationToken));

        var submissions = changed.OfType<PendingSubmission>()
            .Where(x => x.SourceModuleCode == "verification").ToList();
        foreach (var submission in submissions)
        {
            var idPart = submission.ContextId.Split(':', 2)[0];
            if (!Guid.TryParse(idPart, out var contextId)) continue;
            switch (submission.ContextType)
            {
                case "CaseAttachmentByNo": records.Add(contextId); break;
                case "CaseAttachment":
                case "TaskAttachmentByNo":
                case "TestReportByTaskNo":
                    records.UnionWith(await db.ModuleRecordCases.AsNoTracking()
                        .Where(x => x.Id == contextId).Select(x => x.RecordId)
                        .ToListAsync(cancellationToken));
                    break;
                case "TaskAttachment":
                case "TestReport":
                    records.UnionWith(await db.ModuleRecordTasks.AsNoTracking()
                        .Where(x => x.Id == contextId).Select(x => x.Case.RecordId)
                        .ToListAsync(cancellationToken));
                    break;
                case "TestReportVersion":
                    records.UnionWith(await db.ModuleCaseFiles.AsNoTracking()
                        .Where(x => x.Id == contextId).Select(x => x.RecordId)
                        .ToListAsync(cancellationToken));
                    break;
            }
        }
        return records;
    }

    internal static async Task LockRecordAsync(AppDbContext db, Guid recordId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational()) return;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT id FROM module_records WHERE id = @recordId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "recordId"; parameter.Value = recordId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
