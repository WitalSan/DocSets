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
using System.Threading;
using System.Threading.Tasks;

namespace DocSets
{
    internal sealed class RoslynBookmarkResolver
    {
        private readonly AsyncPackage package;
        private readonly EditorStateService editorState;

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
            editorState = new EditorStateService(package);
        }

        public Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync(string solutionDirectory, Func<string, string> toRelativePath)
        {
            return CreateBookmarkFromActiveDocumentAsync(solutionDirectory, toRelativePath, containingTypeOnly: false);
        }

        public Task<DocumentItem> CreateClassBookmarkFromActiveDocumentAsync(string solutionDirectory, Func<string, string> toRelativePath)
        {
            return CreateBookmarkFromActiveDocumentAsync(solutionDirectory, toRelativePath, containingTypeOnly: true);
        }

        private async Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync(
            string solutionDirectory,
            Func<string, string> toRelativePath,
            bool containingTypeOnly)
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

            item.EditorState = await editorState.CaptureAsync(line);

            try
            {
                var workspace = await GetWorkspaceAsync();
                var document = FindDocument(workspace, path);
                if (document == null)
                {
                    return containingTypeOnly ? null : item;
                }

                var text = await document.GetTextAsync();
                var position = ToTextPosition(text, line, column);
                var semanticModel = await document.GetSemanticModelAsync();
                var root = await document.GetSyntaxRootAsync();
                if (semanticModel == null || root == null)
                {
                    return containingTypeOnly ? null : item;
                }

                var symbol = GetNearestDeclaredSymbol(root, semanticModel, position);
                if (containingTypeOnly)
                {
                    symbol = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                }

                if (symbol == null)
                {
                    return containingTypeOnly ? null : item;
                }

                item.Name = CreateDisplayName(symbol);
                item.Symbol = symbol.ToDisplayString(StoredSymbolFormat);
                item.IsMethodSymbol = symbol.Kind == SymbolKind.Method;
                item.Project = document.Project.Name ?? "";
                var symbolLocation = symbol.Locations.FirstOrDefault(x => x.IsInSource);
                var symbolAnchorLine = symbolLocation?.GetLineSpan().StartLinePosition.Line + 1 ?? line;
                var previewStartLine = symbolAnchorLine;
                var previewEndLine = symbolAnchorLine + 5;

                var syntaxReference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxReference != null)
                {
                    var declaration = await syntaxReference.GetSyntaxAsync();
                    var declarationSpan = declaration.GetLocation().GetLineSpan();
                    var declarationStartLine = declarationSpan.StartLinePosition.Line + 1;
                    var declarationEndLine = declarationSpan.EndLinePosition.Line + 1;

                    previewStartLine = GetAttachedCommentStartLine(text, declarationStartLine);
                    previewEndLine = declarationEndLine - declarationStartLine + 1 <= 6
                        ? declarationEndLine
                        : declarationStartLine + 5;
                }

                item.EditorState = await editorState.CaptureAsync(symbolAnchorLine, previewStartLine, previewEndLine);
            }
            catch
            {
                // Roslyn is a best-effort enhancement for ordinary bookmarks.
                // A class folder requires a resolved containing type.
                if (containingTypeOnly)
                {
                    return null;
                }
            }

            return item;
        }



        private static int GetAttachedCommentStartLine(SourceText text, int declarationStartLine)
        {
            if (text == null || text.Lines.Count == 0 || declarationStartLine <= 1)
            {
                return declarationStartLine;
            }

            var lineIndex = Math.Min(text.Lines.Count - 1, declarationStartLine - 2);
            var lineText = text.Lines[lineIndex].ToString().Trim();
            if (lineText.Length == 0)
            {
                return declarationStartLine;
            }

            if (lineText.StartsWith("//", StringComparison.Ordinal))
            {
                var startLineIndex = lineIndex;
                while (startLineIndex > 0)
                {
                    var previous = text.Lines[startLineIndex - 1].ToString().Trim();
                    if (!previous.StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }

                    startLineIndex--;
                }

                return startLineIndex + 1;
            }

            if (lineText.EndsWith("*/", StringComparison.Ordinal))
            {
                var startLineIndex = lineIndex;
                while (startLineIndex >= 0)
                {
                    var current = text.Lines[startLineIndex].ToString();
                    if (current.IndexOf("/*", StringComparison.Ordinal) >= 0)
                    {
                        return startLineIndex + 1;
                    }

                    startLineIndex--;
                }
            }

            return declarationStartLine;
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

            try
            {
                var workspace = await GetWorkspaceAsync();
                var document = FindDocument(workspace, activeDocument.FullName);
                var selection = activeDocument.Selection as TextSelection;
                if (document != null && selection != null)
                {
                    var text = await document.GetTextAsync();
                    var position = ToTextPosition(text, Math.Max(1, selection.ActivePoint.Line), Math.Max(1, selection.ActivePoint.LineCharOffset));
                    var semanticModel = await document.GetSemanticModelAsync();
                    var root = await document.GetSyntaxRootAsync();
                    var symbol = root == null || semanticModel == null ? null : GetNearestDeclaredSymbol(root, semanticModel, position);
                    var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                    if (type != null)
                    {
                        context.ClassName = CreateDisplayName(type);
                    }
                }
            }
            catch
            {
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
                        var anchorLine = lineSpan.StartLinePosition.Line + 1;
                        await OpenFileAtAsync(sourcePath, anchorLine, lineSpan.StartLinePosition.Character + 1);
                        await editorState.RestoreAsync(item.EditorState, anchorLine);
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

        public Task RestoreEditorStateAsync(DocumentItem item, int anchorLine)
        {
            return editorState.RestoreAsync(item?.EditorState, anchorLine);
        }

        public async Task<string> GetLivePreviewAsync(
            string fullPath,
            int line,
            int column,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var workspace = await GetWorkspaceAsync();
                var document = FindDocument(workspace, fullPath);
                if (document != null)
                {
                    var text = await document.GetTextAsync(cancellationToken);
                    var declarationStartLine = Math.Max(1, line);
                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (semanticModel != null && root != null)
                    {
                        var position = ToTextPosition(text, line, column);
                        var symbol = GetNearestDeclaredSymbol(root, semanticModel, position);
                        var syntaxReference = symbol?.DeclaringSyntaxReferences.FirstOrDefault();
                        if (syntaxReference != null)
                        {
                            var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken);
                            declarationStartLine = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        }
                    }

                    var previewStartLine = GetAttachedCommentStartLine(text, declarationStartLine);
                    return GetLineRangeText(text, previewStartLine, declarationStartLine + 9);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            return await ReadFilePreviewAsync(fullPath, line, cancellationToken);
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

        private static string GetLineRangeText(SourceText text, int oneBasedStartLine, int oneBasedEndLine)
        {
            if (text == null || text.Lines.Count == 0)
            {
                return string.Empty;
            }

            var startLine = Math.Max(0, Math.Min(text.Lines.Count - 1, oneBasedStartLine - 1));
            var endLine = Math.Max(startLine, Math.Min(text.Lines.Count - 1, oneBasedEndLine - 1));
            var start = text.Lines[startLine].Start;
            var end = text.Lines[endLine].End;
            return text.ToString(TextSpan.FromBounds(start, end));
        }

        private static Task<string> ReadFilePreviewAsync(
            string fullPath,
            int oneBasedLine,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    return string.Empty;
                }

                var targetIndex = Math.Max(0, oneBasedLine - 1);
                var endIndex = targetIndex + 9;
                var lines = new System.Collections.Generic.List<string>();
                using (var reader = File.OpenText(fullPath))
                {
                    while (!reader.EndOfStream && lines.Count <= endIndex)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lines.Add(reader.ReadLine() ?? string.Empty);
                    }
                }

                if (lines.Count == 0 || targetIndex >= lines.Count)
                {
                    return string.Empty;
                }

                var startIndex = GetAttachedCommentStartIndex(lines, targetIndex);
                var count = Math.Min(lines.Count - startIndex, endIndex - startIndex + 1);
                return string.Join(Environment.NewLine, lines.Skip(startIndex).Take(count));
            }, cancellationToken);
        }

        private static int GetAttachedCommentStartIndex(
            System.Collections.Generic.IReadOnlyList<string> lines,
            int declarationIndex)
        {
            if (lines == null || declarationIndex <= 0 || declarationIndex > lines.Count - 1)
            {
                return declarationIndex;
            }

            var lineIndex = declarationIndex - 1;
            var lineText = lines[lineIndex].Trim();
            if (lineText.StartsWith("//", StringComparison.Ordinal))
            {
                while (lineIndex > 0 && lines[lineIndex - 1].Trim().StartsWith("//", StringComparison.Ordinal))
                {
                    lineIndex--;
                }

                return lineIndex;
            }

            if (lineText.EndsWith("*/", StringComparison.Ordinal))
            {
                while (lineIndex >= 0)
                {
                    if (lines[lineIndex].IndexOf("/*", StringComparison.Ordinal) >= 0)
                    {
                        return lineIndex;
                    }

                    lineIndex--;
                }
            }

            return declarationIndex;
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
