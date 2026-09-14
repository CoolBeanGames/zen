using Zen;

namespace ZenOperator;

public static class ProjectLocator
{
    public static string FindRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ProjectStore.DataFileName)))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            $"No {ProjectStore.DataFileName} found in '{startDirectory}' or any parent directory.");
    }
}
