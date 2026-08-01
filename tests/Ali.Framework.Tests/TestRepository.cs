namespace Ali.Framework.Tests;

internal static class TestRepository
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Ali.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate Ali.sln above the test base directory {AppContext.BaseDirectory}.");
    }
}
