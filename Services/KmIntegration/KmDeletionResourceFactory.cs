using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Services.KmIntegration;

public static class KmDeletionResourceFactory
{
    public static KmDeletionResource ForTestReport(VerificationApplication application,
        ModuleCaseFile file)
    {
        if (application.Status != VerificationApplicationStatus.Accepted ||
            application.ModuleRecordId is null || application.ModuleRecordId != file.RecordId ||
            file.FileKind != ModuleCaseFileKinds.TestReport || file.DocumentId == Guid.Empty ||
            file.VersionNo < 1 || file.Id == Guid.Empty)
            throw new InvalidOperationException("TestReport does not belong to the accepted case.");
        return new("TestReport", null, file.DocumentId, file.VersionNo, file.Id,
            application.Id, file.RecordId);
    }

    public static KmDeletionResource ForArchivedAttachment(VerificationApplication application,
        VerificationApplicationFile attachment)
    {
        if (application.Status != VerificationApplicationStatus.Accepted ||
            application.ModuleRecordId is null ||
            attachment.VerificationApplicationId != application.Id ||
            attachment.DocumentLibraryFileId is not { } documentId || documentId == Guid.Empty ||
            attachment.DocumentLibraryFile?.Id != documentId ||
            attachment.DocumentLibraryFile.StorageArea != DocumentStorageAreas.Library)
            throw new InvalidOperationException("Attachment is not formally archived for this case.");
        return new("VerificationApplicationAttachment", null, documentId, 1,
            attachment.Id, application.Id, application.ModuleRecordId);
    }

    public static KmDeletionResource ForDocumentLibraryFile(DocumentLibraryFile file)
    {
        if (file.Id == Guid.Empty) throw new InvalidOperationException("Document file ID is missing.");
        return new("DocumentLibraryFile", file.Id, null, null, null, null, null);
    }
}
