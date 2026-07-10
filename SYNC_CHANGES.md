# Workspace synchronization

- Local changes continue to be saved immediately.
- The workspace file is checked every 1.5 seconds by the tool-window host.
- A change in LastWriteTimeUtc or file length triggers a full reload.
- The currently selected set and selected node position are restored where possible.
- Saving uses a temporary file and atomic replacement when supported.
- Local saves are serialized; external reload is skipped while a local save is active.
