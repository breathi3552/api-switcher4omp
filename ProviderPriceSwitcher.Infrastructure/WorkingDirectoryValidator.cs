namespace ProviderPriceSwitcher.Infrastructure;

public static class WorkingDirectoryValidator
{
    public static string Validate(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
        }

        var fullPath = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: '{fullPath}'.");
        }

        return fullPath;
    }
}
