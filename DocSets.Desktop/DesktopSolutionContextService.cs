namespace DocSets.Desktop;

/// <summary>
/// Корневой контекст самостоятельного приложения.
/// Пока корнем считается родительский каталог открытого DocSet.
/// </summary>
internal sealed class DesktopSolutionContextService : ISolutionContextService
{
    private readonly SolutionContext _current;

    public DesktopSolutionContextService()
    {
        var storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocSets");
        var sourceDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _current = new SolutionContext(
            true,
            "Desktop",
            sourceDirectory,
            Path.Combine(storageDirectory, "desktop.workspace"));
    }

    public SolutionContext Current => _current;

    public Task<SolutionContext> GetCurrentAsync()
    {
        return Task.FromResult(_current);
    }

}
