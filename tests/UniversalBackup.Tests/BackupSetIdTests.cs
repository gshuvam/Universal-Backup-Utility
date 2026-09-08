using UniversalBackup.Domain.Models;

namespace UniversalBackup.Tests;

public class BackupSetIdTests
{
    [Fact]
    public void New_ShouldGenerateNonEmptyGuid()
    {
        var id = BackupSetId.New();
        Assert.NotEqual(Guid.Empty, id.Value);
    }

    [Fact]
    public void Parse_ValidGuidString_ShouldReconstructMatchingId()
    {
        var original = BackupSetId.New();
        var str = original.ToString();
        var parsed = BackupSetId.Parse(str);

        Assert.Equal(original, parsed);
        Assert.Equal(original.Value, parsed.Value);
    }
}

