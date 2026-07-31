using System;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Текущий solution или аналогичный корневой контекст внешней среды.
    /// </summary>
    public interface ISolutionContextService
    {
        SolutionContext Current { get; }
        Task<SolutionContext> GetCurrentAsync();
    }

    public sealed class SolutionContext
    {
        public static SolutionContext Unavailable { get; } = new SolutionContext(false, "", "", "");

        public SolutionContext(bool isAvailable, string name, string directory, string filePath)
        {
            IsAvailable = isAvailable;
            Name = name ?? "";
            Directory = directory ?? "";
            FilePath = filePath ?? "";
        }

        public bool IsAvailable { get; }
        public string Name { get; }
        public string Directory { get; }
        public string FilePath { get; }
    }
}
