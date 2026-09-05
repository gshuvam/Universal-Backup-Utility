namespace UniversalBackup.Application.Common.Interfaces;

public interface IResticBinaryResolver
{
    string ResolveBinaryPath();
    bool IsBinaryAvailable();
}

