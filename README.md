# Zen

A native C# project manager for humans and AI agents. The first slice is a horizontally scrolling Kanban workspace where columns can represent Git branches or personal categories.

## Run

```powershell
dotnet run
```

## Current behavior

- Permanent `main`, `Uncategorized`, and `Archived` columns
- Create identical columns for branches or personal categories
- Choose any folder to create or open a Zen project
- Persist the board atomically in `zen.tasks.json`
- Store tasks beneath their owning entry in the canonical `branches[] -> tasks[]` hierarchy
- Give every normal board column a unique, Git-safe branch name; system collections use `branch: null`
- Copy the embedded agent guide to `prompt.txt` and store attachments under `files/`
- Automatically reload external JSON changes and normalize completed cards into `Archived`
- Add task cards with prompts, multiple tags, files, requirements, and Git action flags
- Drag cards between columns with a raised, pointer-following card preview
- Single-click a card to toggle its compact tag-and-title view
- Double-click a card to edit its title, task prompt, tags, attachments, requirements, and action flags inline
- Right-click a card to lock or unlock it; locked cards cannot be dragged
- Every card has a stable GUID and a unique board index such as `#001`
- Archive custom columns and retain their cards in `Archived`
- Horizontal mouse-wheel navigation and explicit left/right controls
- Hold the middle mouse button and drag anywhere on the board to pan in both axes
- All columns share one vertical scroll position
- Drag a card near any scrollable edge to automatically pan the board
- Slim scrollbars that brighten on hover or while dragging

## Project files

```text
project-folder/
├── prompt.txt       # Instructions for AI agents
├── zen.tasks.json   # Canonical board and task data
└── files/           # Card attachments
```

Card flags describe requested Git operations; execution of commits, builds, releases, and merges will be connected to agents in a later layer.

When directed to process a branch, `prompt.txt` tells an agent to create or track that branch, execute its eligible tasks in order, and merge it into `main` only when a verified task with `flags.merge: true` is reached.
