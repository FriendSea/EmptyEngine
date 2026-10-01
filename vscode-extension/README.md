# EmptyEngine Editor

EmptyEngine is an editor-only game engine with no runtime implementation of its own. This extension opens the EmptyEngine editor as panes in VS Code.

## Requirements

- .NET 10 SDK or later
- An EmptyEngine project (`*.emptyengine`) that allows the editor to be embedded

To allow embedding, add the following to the targets file specified by `editorProps` in your project. Projects created with `dotnet new emptyengine` already include it.

```xml
<ItemGroup>
  <EditorFrameAncestor Include="vscode-webview:" />
  <EditorFrameAncestor Include="vscode-file:" />
</ItemGroup>
```

## Usage

Opening a folder that contains a `*.emptyengine` file activates the extension, and you can open the editor panes from «EmptyEngine» in the Activity Bar.

| View | Contents |
|---|---|
| Hierarchy | Scene tree, Play/Edit toggle, Undo/Redo |
| Inspector | Editing of the selected object or asset |
| Build | Scenes included in the build, and distribution builds |

You can drag each view to the Secondary Side Bar on the right or the Panel at the bottom. With «Open Pane in Editor Area…» you can turn it into a tab in the editor area, which can also be moved out into a separate window.

Logs from the editor itself (the Host) appear in the «EmptyEngine Host» terminal.

### Explorer integration

- Selecting a file in the Explorer shows its asset in the Inspector
- **Shift-dragging** a file onto the Hierarchy or Inspector loads a scene additively, places a prefab, or assigns it to an asset reference
- Clicking the name of an asset reference in the Inspector selects the source file in the Explorer

## Commands

| Command | Description |
|---|---|
| EmptyEngine: Open Pane in Editor Area… | Pick one pane and open it as a tab in the editor area |
| EmptyEngine: Open in Editor Area (All Panes) | Open all panes in a single tab |
| EmptyEngine: Open in Browser | Open the same editor in a browser |
| EmptyEngine: Show in Inspector | Show the selected file's asset in the Inspector |
| EmptyEngine: Show Host Console | Show the Host terminal |
| EmptyEngine: Restart Host | Restart the Host. Scenes being edited and the undo history are lost |
| EmptyEngine: Show Extension Logs | Show the extension's logs |
| EmptyEngine: Select Project (*.emptyengine) | Choose a different project to open |

## Settings

| Setting | Default | Description |
|---|---|---|
| `emptyEngine.project` | empty | The `*.emptyengine` to open. If empty, the workspace is searched, and you are asked if there is more than one |
| `emptyEngine.configuration` | `Debug` | Build configuration of the editor |
| `emptyEngine.followExplorerSelection` | `true` | Show the asset of the file selected in the Explorer in the Inspector |

## Limitations

- Only one Host can run at a time. While a Host is running for the browser, the extension cannot open the editor
- On first launch, the panes do not appear until the editor has finished building
- The game screen opens in a separate window outside VS Code
