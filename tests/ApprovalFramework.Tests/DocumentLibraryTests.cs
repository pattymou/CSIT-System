using SIT.DepartmentSystem.Web.Services;

namespace ApprovalFramework.Tests;

public sealed class DocumentLibraryTests
{
    [Fact]
    public void FolderLevel_IsLimitedToThree()
    {
        Assert.Equal(1, DocumentLibraryRules.LevelForParent(null));
        Assert.Equal(2, DocumentLibraryRules.LevelForParent(1));
        Assert.Equal(3, DocumentLibraryRules.LevelForParent(2));
        Assert.Throws<InvalidOperationException>(() => DocumentLibraryRules.LevelForParent(3));
    }

    [Fact]
    public void StandardFileName_IsStableSafeAndPreservesExtension()
    {
        var id = Guid.Parse("9f2d1e1f-cbc9-4423-b592-3dd949cdbf21");
        var result = DocumentLibraryRules.CreateStandardFileName(
            "原始 文件.PDF", new DateTime(2026, 10, 21, 2, 30, 45, 123, DateTimeKind.Utc), id);

        Assert.Equal("DOC-20261021023045123-9f2d1e1fcbc94423b5923dd949cdbf21.pdf", result);
    }

    [Fact]
    public void OriginalFileName_RemovesClientPath()
    {
        Assert.Equal("report.txt", DocumentLibraryRules.NormalizeOriginalFileName("C:\\fakepath\\report.txt"));
    }
}
