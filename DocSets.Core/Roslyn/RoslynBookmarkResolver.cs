using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DocSets
{
    internal sealed class RoslynBookmarkResolver
    {
        private readonly AsyncPackage package;

        private static readonly SymbolDisplayFormat StoredSymbolFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
            parameterOptions: SymbolDisplayParameterOptions.None,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private static readonly SymbolDisplayFormat LegacyStoredSymbolFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        private static readonly SymbolDisplayFormat BookmarkNameFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
            parameterOptions: SymbolDisplayParameterOptions.None,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        public RoslynBookmarkResolver(AsyncPackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public async Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync(string solutionDirectory, Func<string, string> toRelativePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE;
            var activeDocument = dte?.ActiveDocument;
            if (activeDocument == null || string.IsNullOrWhiteSpace(activeDocument.FullName))
            {
                return null;
            }

            var selection = activeDocument.Selection as TextSelection;
            var line = Math.Max(1, selection?.ActivePoint.Line ?? 1);
            var column = Math.Max(1, selection?.ActivePoint.LineCharOffset ?? 1);
            var path = activeDocument.FullName;

            var item = new DocumentItem
            {
                Name = GetFallbackName(activeDocument, selection, line),
                Path = toRelativePath?.Invoke(path) ?? path,
                Line = line,
                Column = column
            };

            try
            {
                var workspace = await GetWorkspaceAsync();
                var document = FindDocument(workspace, path);
                if (document == null)
                {
                    return item;
                }

                var text = await document.GetTextAsync();
                var position = ToTextPosition(text, line, column);
                var semanticModel = await document.GetSemanticModelAsync();
                var root = await document.GetSyntaxRootAsync();
                if (semanticModel == null || root == null)
                {
                    return item;
                }

                var symbol = GetNearestDeclaredSymbol(root, semanticModel, position);
                if (symbol == null)
                {
                    return item;
                }

                item.Name = CreateDisplayName(symbol);
                item.Symbol = symbol.ToDisplayString(StoredSymbolFormat);
                item.Project = document.Project.Name ?? "";
            }
            catch
            {
                // Roslyn is a best-effort enhancement. Keep the plain file/line bookmark if it is unavailable.
            }

            return item;
        }


        public async Task<ActiveDocumentContext> GetActiveDocumentContextAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE;
            var activeDocument = dte?.ActiveDocument;
            if (activeDocument == null || string.IsNullOrWhiteSpace(activeDocument.FullName))
            {
                return null;
            }

            var context = new ActiveDocumentContext
            {
                FileName = Path.GetFileNameWithoutExtension(activeDocument.FullName)
            };

            try
            {
                var workspace = await GetWorkspaceAsync();
                var document = FindDocument(workspace, activeDocument.FullName);
                if (!string.IsNullOrWhiteSpace(document?.Project?.Name))
                {
                    context.ProjectName = document.Project.Name;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(context.ProjectName))
            {
                try
                {
                    context.ProjectName = activeDocument.ProjectItem?.ContainingProject?.Name ?? "";
                }
                catch
                {
                }
            }

            return context;
        }

        public async Task<bool> TryOpenBookmarkBySymbolAsync(DocumentItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Symbol))
            {
                return false;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var workspace = await GetWorkspaceAsync();
                if (workspace == null)
                {
                    return false;
                }

                var projects = workspace.CurrentSolution.Projects;
                if (!string.IsNullOrWhiteSpace(item.Project))
                {
                    projects = projects.Where(p => string.Equals(p.Name, item.Project, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var project in projects)
                {
                    foreach (var document in project.Documents.Where(d => d.SupportsSyntaxTree))
                    {
                        var semanticModel = await document.GetSemanticModelAsync();
                        var root = await document.GetSyntaxRootAsync();
                        if (semanticModel == null || root == null)
                        {
                            continue;
                        }

                        var match = root.DescendantNodesAndSelf()
                            .Select(node => GetDeclaredSymbolForNode(node, semanticModel))
                            .Where(symbol => symbol != null)
                            .FirstOrDefault(symbol => IsSameStoredSymbol(symbol, item.Symbol));

                        var location = match?.Locations.FirstOrDefault(x => x.IsInSource);
                        var sourcePath = location?.SourceTree?.FilePath;
                        if (location == null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                        {
                            continue;
                        }

                        var lineSpan = location.GetLineSpan();
                        await OpenFileAtAsync(sourcePath, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public async Task OpenFileAtAsync(string fullPath, int line, int column)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE;
            dte.ItemOperations.OpenFile(fullPath);
            var selection = dte.ActiveDocument?.Selection as TextSelection;
            selection?.MoveToLineAndOffset(Math.Max(1, line), Math.Max(1, column), false);
        }

        private async Task<VisualStudioWorkspace> GetWorkspaceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<VisualStudioWorkspace>();
        }

        private static Microsoft.CodeAnalysis.Document FindDocument(VisualStudioWorkspace workspace, string fullPath)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            return workspace.CurrentSolution.Projects
                .SelectMany(project => project.Documents)
                .FirstOrDefault(document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        private static int ToTextPosition(SourceText text, int oneBasedLine, int oneBasedColumn)
        {
            if (text == null || text.Lines.Count == 0)
            {
                return 0;
            }

            var lineIndex = Math.Max(0, Math.Min(text.Lines.Count - 1, oneBasedLine - 1));
            var line = text.Lines[lineIndex];
            var columnIndex = Math.Max(0, Math.Min(Math.Max(0, line.Span.Length), oneBasedColumn - 1));
            return line.Start + columnIndex;
        }

        private static ISymbol GetNearestDeclaredSymbol(SyntaxNode root, SemanticModel semanticModel, int position)
        {
            var token = root.FindToken(position);
            var node = token.Parent;
            if (node == null)
            {
                return null;
            }

            foreach (var current in node.AncestorsAndSelf())
            {
                var symbol = GetDeclaredSymbolForNode(current, semanticModel);
                if (symbol != null)
                {
                    return symbol;
                }
            }

            return null;
        }

        private static ISymbol GetDeclaredSymbolForNode(SyntaxNode node, SemanticModel semanticModel)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return semanticModel.GetDeclaredSymbol(method);
                case ConstructorDeclarationSyntax constructor:
                    return semanticModel.GetDeclaredSymbol(constructor);
                case PropertyDeclarationSyntax property:
                    return semanticModel.GetDeclaredSymbol(property);
                case IndexerDeclarationSyntax indexer:
                    return semanticModel.GetDeclaredSymbol(indexer);
                case EventDeclarationSyntax eventDeclaration:
                    return semanticModel.GetDeclaredSymbol(eventDeclaration);
                case VariableDeclaratorSyntax variable when variable.Parent?.Parent is FieldDeclarationSyntax:
                    return semanticModel.GetDeclaredSymbol(variable);
                case ClassDeclarationSyntax classDeclaration:
                    return semanticModel.GetDeclaredSymbol(classDeclaration);
                case StructDeclarationSyntax structDeclaration:
                    return semanticModel.GetDeclaredSymbol(structDeclaration);
                case InterfaceDeclarationSyntax interfaceDeclaration:
                    return semanticModel.GetDeclaredSymbol(interfaceDeclaration);
                case EnumDeclarationSyntax enumDeclaration:
                    return semanticModel.GetDeclaredSymbol(enumDeclaration);
                case DelegateDeclarationSyntax delegateDeclaration:
                    return semanticModel.GetDeclaredSymbol(delegateDeclaration);
                default:
                    return null;
            }
        }

        private static bool IsSameStoredSymbol(ISymbol symbol, string storedSymbol)
        {
            if (symbol == null || string.IsNullOrWhiteSpace(storedSymbol))
            {
                return false;
            }

            return string.Equals(symbol.ToDisplayString(StoredSymbolFormat), storedSymbol, StringComparison.Ordinal)
                || string.Equals(symbol.ToDisplayString(LegacyStoredSymbolFormat), storedSymbol, StringComparison.Ordinal);
        }

        private static string CreateDisplayName(ISymbol symbol)
        {
            if (symbol == null)
            {
                return "Bookmark";
            }

            if (symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor)
            {
                var typeName = method.ContainingType?.ToDisplayString(BookmarkNameFormat);
                return string.IsNullOrWhiteSpace(typeName) ? method.Name : $"{typeName}.{method.ContainingType.Name}";
            }

            return symbol.ToDisplayString(BookmarkNameFormat);
        }

        private static string GetFallbackName(EnvDTE.Document document, TextSelection selection, int line)
        {
            try
            {
                if (selection != null && !selection.IsEmpty && !string.IsNullOrWhiteSpace(selection.Text))
                {
                    return selection.Text.Trim().Replace("\r", " ").Replace("\n", " ");
                }

                var editPoint = selection.ActivePoint.CreateEditPoint();
                editPoint.StartOfLine();
                var end = editPoint.CreateEditPoint();
                end.EndOfLine();
                var text = editPoint.GetText(end).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Length > 80 ? text.Substring(0, 80) : text;
                }
            }
            catch
            {
            }

            return $"{Path.GetFileName(document.FullName)}:{line}";
        }
    }
}
