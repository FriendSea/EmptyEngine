// Host を同梱した全プラットフォーム共通の VSIX を dist/ に生成する。

import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, rmSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const extensionDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoDir = resolve(extensionDir, "..");
const hostProject = join(repoDir, "Core", "EmptyEngine.Host", "EmptyEngine.Host.csproj");

/** 拡張に同梱する Host の出力ディレクトリ。 */
const hostDir = join(extensionDir, "host");
const outDir = join(extensionDir, "dist");

/** VSIX の作成に使う vsce の実行ファイル。 */
const vsce = join(extensionDir, "node_modules", "@vscode", "vsce", "vsce");

copyFileSync(join(repoDir, "LICENSE"), join(extensionDir, "LICENSE"));

publishHost();
pack();


/** Host を host/ へ発行する。中身は毎回作り直す。 */
function publishHost() {
  rmSync(hostDir, { recursive: true, force: true });
  mkdirSync(hostDir, { recursive: true });

  console.log("\n[package] Publishing Host");
  run("dotnet", [
    "publish",
    hostProject,
    "--configuration",
    "Release",
    "-p:UseAppHost=false",
    "--output",
    hostDir,
    "--nologo"
  ]);
}

/** VSIX を作成する。 */
function pack() {
  mkdirSync(outDir, { recursive: true });

  const version = JSON.parse(readFileSync(join(extensionDir, "package.json"), "utf8")).version;
  const name = `emptyengine-host-${version}.vsix`;

  console.log(`[package] Creating ${name}`);
  run(process.execPath, [vsce, "package", "--out", join(outDir, name)]);
}

/** 指定した子プロセスを実行し、失敗した場合は同じ終了コードで終了する。 */
function run(command, args) {
  const result = spawnSync(command, args, { cwd: extensionDir, stdio: "inherit" });

  if (result.error) {
    fail(`Failed to start ${command}: ${result.error.message}`);
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

function fail(message) {
  console.error(`[package] ${message}`);
  process.exit(1);
}
