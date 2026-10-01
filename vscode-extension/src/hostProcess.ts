import * as fs from "node:fs/promises";
import * as http from "node:http";
import * as net from "node:net";
import * as path from "node:path";
import * as vscode from "vscode";

/** エディタへの声かけの間隔 */
const POLL_INTERVAL_MS = 250;

/**
 * Host プロセスの起動・再起動とエディタへの接続を管理する。
 * Host の端末操作を提供し、エディタが応答するまで起動完了を待つ。
 */
export class HostProcess {
  private terminal: vscode.Terminal | undefined;
  private uiPort: number | undefined;
  private ready: Promise<number> | undefined;

  /** 起動待機が属するセッションを識別する世代番号。 */
  private generation = 0;

  private readonly changed = new vscode.EventEmitter<void>();

  /** webview の組み直しを求める合図 */
  readonly onDidRestart = this.changed.event;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly log: (line: string) => void
  ) {}

  /** エディタが応答するまで待ち、接続ポートを返す。重複した呼び出しでは同じ起動結果を共有する。 */
  waitForEditor(): Promise<number> {
    return (this.ready ??= this.launch());
  }

  /** Host のターミナルの前面表示 */
  show(): void {
    this.terminal?.show();
  }

  /** 指定したソースファイルのアセットをエディタに表示する。 */
  async inspect(file: string): Promise<"shown" | "unknown" | "offline"> {
    const port = this.uiPort;
    if (!port) return "offline";

    try {
      const status = await askEditor(port, `/api/editor/select-asset?path=${encodeURIComponent(file)}`, "POST");
      return status === 200 ? "shown" : "unknown";
    } catch {
      return "offline";
    }
  }

  /** 管理する Host とエディタを再起動する。 */
  restart(): void {
    this.stop();

    void this.waitForEditor().catch(() => undefined);
    this.changed.fire();
  }

  dispose(): void {
    this.stop();
    this.changed.dispose();
  }

  private stop(): void {
    this.generation++;
    this.terminal?.dispose();
    this.terminal = undefined;
    this.uiPort = undefined;
    this.ready = undefined;
  }

  private async launch(): Promise<number> {
    const config = vscode.workspace.getConfiguration("emptyEngine");
    const hostPath = await resolveHost(this.context, this.log);

    const manifest = await resolveManifest(this.context);
    if (!manifest) {
      throw new Error(
        "No *.emptyengine project is selected. " +
          "Pick one with «EmptyEngine: Select Project (*.emptyengine)», or set its path in the emptyEngine.project setting."
      );
    }

    const generation = this.generation;

    const port = await freePort();
    this.uiPort = port;

    const args = [manifest, "--no-browser", "--ui-port", String(port)];

    const configuration = config.get<string>("configuration")?.trim();
    if (configuration) {
      args.push("--configuration", configuration);
    }

    this.log(`[host] Launching: dotnet ${hostPath} ${args.join(" ")}`);

    this.terminal = vscode.window.createTerminal({
      name: "EmptyEngine Host",
      shellPath: "dotnet",
      shellArgs: [hostPath, ...args],
      cwd: workingDirectory(manifest),
      env: { DOTNET_CLI_FORCE_UTF8_ENCODING: "true" },
      // 窓を開き直したときに VSCode が勝手に生き返らせると Host が二重になる。
      isTransient: true
    });

    await this.waitForEditorReady(port, generation);
    this.log(`[host] Editor responded: http://127.0.0.1:${port}/`);
    return port;
  }

  /** エディタが応答するまで待つ。再起動によって対象セッションが変わると中断する。 */
  private async waitForEditorReady(port: number, generation: number): Promise<void> {
    for (;;) {
      if (this.generation !== generation) {
        throw new Error("Stopped waiting for the previous editor because the Host was restarted.");
      }

      try {
        if ((await askEditor(port, "/healthz", "GET")) === 200) return;
      } catch {
        // まだ立っていないだけ。次の呼びかけまで待つ。
      }

      await new Promise((done) => setTimeout(done, POLL_INTERVAL_MS));
    }
  }
}

/** 利用可能なポート番号を返す。返したポートの予約は保証しない。 */
function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = net.createServer();
    probe.on("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const address = probe.address();
      const port = typeof address === "object" && address ? address.port : 0;
      probe.close(() => (port ? resolve(port) : reject(new Error("Could not find a free port."))));
    });
  });
}

/** エディタへの本文なしリクエストとステータスコードの取得 */
function askEditor(port: number, route: string, method: "GET" | "POST"): Promise<number> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: "127.0.0.1", port, path: route, method, headers: { "content-length": "0" } },
      (res) => {
        res.resume();
        res.on("end", () => resolve(res.statusCode ?? 0));
      }
    );
    req.on("error", reject);
    req.setTimeout(2000, () => req.destroy(new Error("timeout")));
    req.end();
  });
}

/** 発行された Host のアセンブリ名 */
const HOST_ASSEMBLY = "EmptyEngine.Host.dll";

/** 拡張に同梱した Host のディレクトリ名。 */
const BUNDLED_HOST = "host";

/** 起こす Host のアセンブリの決定 */
async function resolveHost(context: vscode.ExtensionContext, log: (line: string) => void): Promise<string> {
  const bundled = path.join(context.extensionUri.fsPath, BUNDLED_HOST, HOST_ASSEMBLY);
  if (!(await isFile(bundled))) {
    throw new Error(
      `EmptyEngine.Host is not bundled: ${bundled}. ` +
        "Reinstall the .vsix built with npm run package in vscode-extension."
    );
  }

  log(`[host] Assembly: ${bundled}`);
  return bundled;
}

async function isFile(target: string): Promise<boolean> {
  try {
    return (await fs.stat(target)).isFile();
  } catch {
    return false;
  }
}

/** Host の作業ディレクトリを返す。所属するワークスペースがなければマニフェストのディレクトリを使う。 */
function workingDirectory(manifest: string): string {
  const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(manifest));
  return folder?.uri.fsPath ?? path.dirname(manifest);
}

/** 設定されたパスをワークスペース基準の絶対パスに変換する。 */
function absolute(target: string): string {
  if (path.isAbsolute(target)) {
    return target;
  }
  const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  return root ? path.resolve(root, target) : path.resolve(target);
}

const REMEMBERED_PROJECT = "emptyEngine.project";

/** 開くマニフェスト（*.emptyengine）の決定 */
async function resolveManifest(context: vscode.ExtensionContext): Promise<string | undefined> {
  const configured = vscode.workspace.getConfiguration("emptyEngine").get<string>("project")?.trim();
  if (configured) {
    return absolute(configured);
  }

  const remembered = context.workspaceState.get<string>(REMEMBERED_PROJECT);
  if (remembered && await isFile(remembered)) {
    return remembered;
  }

  const found = await findManifests();
  if (found.length === 0) {
    return undefined;
  }
  if (found.length === 1) {
    await context.workspaceState.update(REMEMBERED_PROJECT, found[0]);
    return found[0];
  }

  const picked = await vscode.window.showQuickPick(
    found.map((file) => ({ label: path.basename(file), description: file, file })),
    { title: "EmptyEngine Project to Open", placeHolder: "Select a *.emptyengine file", ignoreFocusOut: true }
  );
  if (!picked) {
    return undefined;
  }

  await context.workspaceState.update(REMEMBERED_PROJECT, picked.file);
  return picked.file;
}

async function findManifests(): Promise<string[]> {
  const hits = await vscode.workspace.findFiles(
    "**/*.emptyengine",
    "**/{bin,obj,.artifacts,node_modules,target}/**"
  );
  return hits
    .filter((uri) => uri.scheme === "file")
    .map((uri) => uri.fsPath)
    .sort((a, b) => a.localeCompare(b));
}

/** プロジェクトの選び直し */
export async function promptForProject(context: vscode.ExtensionContext): Promise<string | undefined> {
  const configured = vscode.workspace.getConfiguration("emptyEngine").get<string>("project")?.trim();
  if (configured) {
    void vscode.window.showWarningMessage(
      `The emptyEngine.project setting points to ${configured}, so selecting another project has no effect. Clear the setting first.`
    );
    return undefined;
  }

  const found = await findManifests();
  if (found.length === 0) {
    void vscode.window.showWarningMessage(
      "No *.emptyengine file in this workspace. Open a folder that contains a project, or set its path in the emptyEngine.project setting."
    );
    return undefined;
  }

  const current = context.workspaceState.get<string>(REMEMBERED_PROJECT);
  const picked = await vscode.window.showQuickPick(
    found.map((file) => ({
      label: path.basename(file),
      description: file === current ? `${file}  ← currently open` : file,
      file
    })),
    { title: "EmptyEngine Project to Open", placeHolder: "Select a *.emptyengine file", ignoreFocusOut: true }
  );
  if (!picked) {
    return undefined;
  }

  await context.workspaceState.update(REMEMBERED_PROJECT, picked.file);
  return picked.file;
}
