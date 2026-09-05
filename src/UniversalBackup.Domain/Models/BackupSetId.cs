namespace UniversalBackup.Domain.Models;

public readonly record struct BackupSetId(Guid Value)
{
    public static BackupSetId New() => new(Guid.NewGuid());
    public static BackupSetId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
