# Editor state restoration

- Bookmarks now persist editor state in `editorState`.
- State is stored relative to the resolved symbol declaration for Symbol bookmarks.
- File bookmarks store state relative to their saved line.
- Saved state includes caret, selection range, and first visible line.
- Creating, updating, and synchronizing a bookmark captures the current editor state.
- Opening a bookmark restores the selection and viewport after navigation.
- Clipboard JSON and cloning preserve editor state.


## Automatic code preview

- When text is selected, the selection is stored as both the restorable selection and the code preview.
- Without a selection, symbol bookmarks store the complete declaration when it fits in six lines; longer declarations store the first six lines.
- File bookmarks store the current line and the following five lines.
- Automatic previews are display-only and are not restored as editor selections.
