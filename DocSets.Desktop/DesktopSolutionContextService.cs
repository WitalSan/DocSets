namespace DocSets.Desktop;

/// <summary>
/// Корневой контекст самостоятельного приложения.
/// Пока корнем считается родительский каталог открытого DocSet.
/// </summary>
internal sealed class DesktopSolutionContextService : ISolutionContextService
{
    private SolutionContext _current = SolutionContext.Unavailable;

    public SolutionContext Current => _current;

    public Task<SolutionContext> GetCurrentAsync()
    {
        return Task.FromResult(_current);
    }

    public void SetForDocSet(string docSetDirectory)
    {
        if (string.IsNullOrWhiteSpace(docSetDirectory))
        {
            _current = SolutionContext.Unavailable;
            return;
        }

        var fullPath = Path.GetFullPath(docSetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootDirectory = Path.GetDirectoryName(fullPath) ?? fullPath;
        var name = Path.GetFileName(rootDirectory);
        _current = new SolutionContext(
            true,
            string.IsNullOrWhiteSpace(name) ? "Desktop" : name,
            rootDirectory,
            Path.Combine(rootDirectory, ".docsets-desktop.workspace"));
    }
}
