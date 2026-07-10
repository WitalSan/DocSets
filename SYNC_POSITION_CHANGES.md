# Sync with current position

Added the context-menu command `Синхронизировать с текущей позицией`.

Behavior:
- File: updates path, line and column; clears symbol/project and restarts file tracking.
- Symbol: updates path, line, column, symbol and project; fails without changing the item if no symbol can be resolved.
- Empty: becomes Symbol when Roslyn resolves a symbol, otherwise becomes File.
- NodeType, Name, children and tree position remain unchanged.
