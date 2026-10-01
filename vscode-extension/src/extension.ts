import { randomUUID } from "node:crypto";
import * as path from "node:path";
import * as vscode from "vscode";
import { HostProcess, promptForProject } from "./hostProcess";

/** EmptyEngine エディタを VSCode 内で利用するためのコマンドと画面を登録する。 */
export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel("EmptyEngine");
  const log = (line: string) => output.appendLine(line);

  const host = new HostProcess(context, log);
  const surfaces = new Surfaces({ host, log });
  const follow = new ExplorerFollow(host);

  context.subscriptions.push(
    output,
    host,
    surfaces,
    follow,
    host.onDidRestart(() => surfaces.refresh()),

    ...follow.listen(),

    vscode.commands.registerCommand("emptyEngine.inspectFile", async (uri?: vscode.Uri) => {
      const target = uri?.scheme === "file" ? uri.fsPath : activeFile();
      if (!target) return;

      const result = await host.inspect(target);
      if (result === "shown") {
        await vscode.commands.executeCommand("emptyEngine.inspector.focus");
        return;
      }
      void vscode.window.showInformationMessage(
        result === "offline"
          ? "The EmptyEngine editor is not running yet. Open the «EmptyEngine» view."
          : `${path.basename(target)} is not an imported asset (it must be under Assets and supported by an importer).`
      );
    }),

    ...PANES.map((pane) =>
      vscode.window.registerWebviewViewProvider(
        viewId(pane),
        {
          resolveWebviewView: (view) => surfaces.attach(view.webview, `${pane.title} view`, pane.route, view.onDidDispose)
        }
      )
    ),

    vscode.commands.registerCommand("emptyEngine.openInEditor", () =>
      openInEditor(context, surfaces, WORKBENCH)
    ),
    vscode.commands.registerCommand("emptyEngine.openPaneInEditor", async () => {
      const picked = await vscode.window.showQuickPick(
        PANES.map((pane) => ({ label: pane.title, pane })),
        { title: "Which pane do you want to open in the editor area?", placeHolder: "As a tab, it can be detached with «Move into New Window»" }
      );
      if (picked) openInEditor(context, surfaces, picked.pane);
    }),
    vscode.commands.registerCommand("emptyEngine.showHostConsole", () => host.show()),
    vscode.commands.registerCommand("emptyEngine.showLogs", () => output.show(true)),
    vscode.commands.registerCommand("emptyEngine.restartHost", () => {
      log("[host] Restarting…");
      host.restart();
    }),
    vscode.commands.registerCommand("emptyEngine.openInBrowser", async () => {
      try {
        const port = await host.waitForEditor();
        await vscode.env.openExternal(vscode.Uri.parse(`http://127.0.0.1:${port}/`));
      } catch (err) {
        void vscode.window.showErrorMessage(describe(err));
      }
    }),
    vscode.commands.registerCommand("emptyEngine.selectProject", async () => {
      const picked = await promptForProject(context);
      if (!picked) return;

      log(`[host] Project: ${picked}. Restarting the Host…`);
      host.restart();
    })
  );
}

/** エクスプローラの選択に合わせてエディタのアセット表示を更新する。 */
class ExplorerFollow {
  private last = "";
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(private readonly host: HostProcess) {}

  listen(): vscode.Disposable[] {
    const bump = () => this.schedule();
    return [
      vscode.window.tabGroups.onDidChangeTabs(bump),
      vscode.window.tabGroups.onDidChangeTabGroups(bump)
    ];
  }

  dispose(): void {
    if (this.timer) clearTimeout(this.timer);
  }

  /** 落ち着くまで待ってからの 1 回だけの送信 */
  private schedule(): void {
    if (!vscode.workspace.getConfiguration("emptyEngine").get<boolean>("followExplorerSelection", true)) {
      this.last = "";
      return;
    }

    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = undefined;
      const file = activeFile();
      if (!file || file === this.last) return;
      this.last = file;
      void this.host.inspect(file);
    }, 120);
  }
}

/** アセットのソースファイルのエクスプローラでの選択 */
async function reveal(fsPath: string): Promise<void> {
  const uri = vscode.Uri.file(fsPath);
  const command = vscode.workspace.getWorkspaceFolder(uri) ? "revealInExplorer" : "revealFileInOS";
  await vscode.commands.executeCommand(command, uri);
}

/** ソースファイルを指定された 1-origin 行で開く。 */
async function openSource(fsPath: string, line: number): Promise<void> {
  const document = await vscode.workspace.openTextDocument(vscode.Uri.file(fsPath));
  const position = new vscode.Position(Math.max(0, Math.trunc(line) - 1), 0);
  await vscode.window.showTextDocument(document, {
    preserveFocus: false,
    selection: new vscode.Range(position, position)
  });
}

/** 前面のタブが指しているファイル */
function activeFile(): string | undefined {
  const input = vscode.window.tabGroups.activeTabGroup.activeTab?.input as { uri?: vscode.Uri } | undefined;
  const uri = input?.uri;
  return uri?.scheme === "file" ? uri.fsPath : undefined;
}

/** 出せる画面の 1 つ */
interface Surface {
  readonly id: string;
  readonly title: string;
  readonly route: string;
}

const PANES: readonly Surface[] = [
  { id: "hierarchy", title: "Hierarchy", route: "pane/hierarchy" },
  { id: "inspector", title: "Inspector", route: "pane/inspector" },
  { id: "build", title: "Build", route: "pane/build" }
];

/** エディタ全体を表示するタブの定義。 */
const WORKBENCH: Surface = { id: "workbench", title: "EmptyEngine", route: "" };

function viewId(pane: Surface): string {
  return `emptyEngine.${pane.id}`;
}

/** webview の表示とエディタ操作に必要な依存先。 */
interface Wiring {
  readonly host: HostProcess;
  readonly log: (line: string) => void;
}

/** 出している webview 1 枚分 */
interface Attached {
  readonly webview: vscode.Webview;
  readonly where: string;
  readonly route: string;
  revision: number;
}

/** 出している webview（ビュー・タブ）の集まり */
class Surfaces {
  private readonly open = new Set<Attached>();

  constructor(private readonly wiring: Wiring) {}

  attach(webview: vscode.Webview, where: string, route: string, onDidDispose: vscode.Event<void>): void {
    const entry: Attached = { webview, where, route, revision: 0 };
    this.open.add(entry);

    webview.options = { enableScripts: true };

    const listener = webview.onDidReceiveMessage((message) => {
      if (message?.type === "alive") {
        this.wiring.log(`[webview] ${where} started rendering`);
        return;
      }
      if (message?.type === "showConsole") {
        this.wiring.host.show();
        return;
      }
      if (message?.type === "reveal" && typeof message.path === "string") {
        void reveal(message.path);
        return;
      }
      if (message?.type === "openSource"
          && typeof message.path === "string"
          && typeof message.line === "number") {
        void openSource(message.path, message.line);
      }
    });

    onDidDispose(() => {
      listener.dispose();
      this.open.delete(entry);
    });

    void fill(entry, this.wiring);
  }

  refresh(): void {
    for (const entry of this.open) {
      void fill(entry, this.wiring);
    }
  }

  dispose(): void {
    this.open.clear();
  }
}

/** 同じ画面のエディタ領域のタブとしての表示 */
const panels = new Map<string, vscode.WebviewPanel>();

function openInEditor(context: vscode.ExtensionContext, surfaces: Surfaces, surface: Surface): void {
  const existing = panels.get(surface.id);
  if (existing) {
    existing.reveal(undefined, false);
    return;
  }

  const panel = vscode.window.createWebviewPanel(`emptyEngine.${surface.id}Tab`, surface.title, vscode.ViewColumn.Active, {
    enableScripts: true
  });
  panel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "emptyengine.svg");
  panel.onDidDispose(() => {
    if (panels.get(surface.id) === panel) panels.delete(surface.id);
  });
  panels.set(surface.id, panel);

  surfaces.attach(panel.webview, `${surface.title} tab`, surface.route, panel.onDidDispose);
}

/** エディタの起動を待ち、webview に画面を表示する。 */
async function fill(entry: Attached, wiring: Wiring): Promise<void> {
  const { host, log } = wiring;
  const revision = ++entry.revision;

  entry.webview.html = waitingHtml();

  let port: number;
  try {
    port = await host.waitForEditor();
  } catch (err) {
    if (entry.revision === revision) {
      entry.webview.html = troubleHtml(describe(err));
    }
    return;
  }

  let origin: string;
  try {
    origin = await editorOrigin(port);
  } catch (err) {
    if (entry.revision === revision) {
      entry.webview.html = troubleHtml(describe(err));
    }
    return;
  }

  if (entry.revision !== revision) return;
  entry.webview.html = shellHtml(origin, entry.route);
  log(`[webview] Embedded ${origin}/${entry.route} in ${entry.where}`);
}

/** webview から届くエディタのオリジン。リモート開発ではポートを転送した先になる */
async function editorOrigin(port: number): Promise<string> {
  const external = await vscode.env.asExternalUri(vscode.Uri.parse(`http://127.0.0.1:${port}/`));
  return new URL(external.toString(true)).origin;
}

/** エディタを埋め込んで表示する HTML を返す。 */
function shellHtml(origin: string, route: string): string {
  const nonce = randomUUID();

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; frame-src ${origin}; script-src 'nonce-${nonce}'; style-src 'unsafe-inline';">
<style>
html, body { height: 100%; margin: 0; padding: 0; overflow: hidden; background: #1e2126; }
iframe { display: block; width: 100%; height: 100%; border: 0; background: #1e2126; }
</style>
</head>
<body>
<iframe id="ui" src="${origin}/${route}"></iframe>
<script nonce="${nonce}">
(function () {
  "use strict";
  var api = acquireVsCodeApi();
  api.postMessage({ type: "alive" });
  window.addEventListener("message", function (event) {
    if (event.origin !== "${origin}") return;
    var data = event.data;
    if (data && data.type === "emptyengine:reveal" && typeof data.path === "string") {
      api.postMessage({ type: "reveal", path: data.path });
      return;
    }
    if (data && data.type === "emptyengine:openSource"
        && typeof data.path === "string"
        && typeof data.line === "number") {
      api.postMessage({ type: "openSource", path: data.path, line: data.line });
    }
  });
})();
</script>
</body>
</html>`;
}

/** エディタが立つまでのつなぎ画面 */
function waitingHtml(): string {
  const nonce = randomUUID();

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; script-src 'nonce-${nonce}'; style-src 'unsafe-inline';">
<style>
body {
  font-family: var(--vscode-font-family);
  font-size: var(--vscode-font-size);
  color: var(--vscode-foreground);
  background: #1e2126;
  padding: 12px;
  margin: 0;
}
.wrap {
  min-height: calc(100vh - 24px);
  display: grid;
  place-items: center;
}
.card {
  display: grid;
  gap: 10px;
  justify-items: center;
  text-align: center;
  padding: 16px 18px;
}
.spinner {
  width: 22px;
  height: 22px;
  border-radius: 50%;
  border: 2px solid color-mix(in srgb, var(--vscode-foreground) 25%, transparent);
  border-top-color: var(--vscode-foreground);
  animation: spin .85s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
.hint { opacity: .75; font-size: .9em; }
button {
  font: inherit;
  color: var(--vscode-button-foreground);
  background: var(--vscode-button-background);
  border: 0;
  border-radius: 2px;
  padding: 4px 12px;
  cursor: pointer;
}
button:hover { background: var(--vscode-button-hoverBackground); }
</style>
</head>
<body>
<div class="wrap">
  <div class="card">
    <div class="spinner" aria-hidden="true"></div>
    <div>Starting EmptyEngine.Host…</div>
    <div class="hint">The first launch takes a while until the editor is built. Progress is shown in the «EmptyEngine Host» terminal.</div>
    <button id="console" type="button">Open Terminal</button>
  </div>
</div>
<script nonce="${nonce}">
(function () {
  "use strict";
  var api = acquireVsCodeApi();
  document.getElementById("console").addEventListener("click", function () {
    api.postMessage({ type: "showConsole" });
  });
})();
</script>
</body>
</html>`;
}

/** Host を起こせなかったときの画面 */
function troubleHtml(message: string): string {
  const escaped = message.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" })[c] ?? c);

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
<style>
body {
  font-family: var(--vscode-font-family);
  font-size: var(--vscode-font-size);
  color: var(--vscode-foreground);
  background: #1e2126;
  padding: 12px;
  margin: 0;
}
.wrap { min-height: calc(100vh - 24px); display: grid; place-items: center; }
.card { display: grid; gap: 10px; justify-items: center; text-align: center; padding: 16px 18px; max-width: 34em; }
.dot { font-size: 20px; opacity: .55; }
.message { color: var(--vscode-errorForeground); line-height: 1.6; }
.hint { opacity: .75; font-size: .9em; line-height: 1.6; }
</style>
</head>
<body>
<div class="wrap">
  <div class="card">
    <div class="dot" aria-hidden="true">○</div>
    <div class="message">${escaped}</div>
    <div class="hint">After fixing it, run «EmptyEngine: Restart Host».</div>
  </div>
</div>
</body>
</html>`;
}

function describe(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
