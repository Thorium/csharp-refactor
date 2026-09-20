// CSharp.Refactor for VS Code.
//
// VS Code's C# extension hosts Roslyn as a language server that loads
// analyzers from each project. This extension ships no language client of
// its own: it bundles the CSharp.Refactor analyzer assembly and, with the
// user's consent, makes MSBuild add it to every C# project on the machine
// through the user ImportBefore hook that Microsoft.Common.props imports:
//
//   %LOCALAPPDATA%\Microsoft\MSBuild\Current\Imports\Microsoft.Common.props\ImportBefore\CSharp.Refactor.props
//
// Every project the language server or `dotnet build` evaluates then
// carries the analyzer, and Roslyn renders the squiggles and light bulbs.
// Decline and nothing is written; the commands re-offer and undo it.

import * as vscode from "vscode";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as cp from "child_process";

const PROPS_NAME = "CSharp.Refactor.props";
const DECLINED_KEY = "csharpRefactor.wiringDeclined";

/** $(MSBuildUserExtensionsPath) on this platform: LocalApplicationData/Microsoft/MSBuild. */
function userExtensionsPath(): string {
    if (process.platform === "win32") {
        const local = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
        return path.join(local, "Microsoft", "MSBuild");
    }
    const data = process.env.XDG_DATA_HOME ?? path.join(os.homedir(), ".local", "share");
    return path.join(data, "Microsoft", "MSBuild");
}

function propsPath(): string {
    return path.join(userExtensionsPath(), "Current", "Imports", "Microsoft.Common.props", "ImportBefore", PROPS_NAME);
}

function analyzersDir(context: vscode.ExtensionContext): string {
    return path.join(context.extensionPath, "analyzers");
}

/** The props file: an Analyzer item per assembly, only for C# projects, with an opt-out property. */
function propsText(dir: string): string {
    const item = (file: string) => `    <Analyzer Include="${path.join(dir, file)}" />`;
    return [
        `<?xml version="1.0" encoding="utf-8"?>`,
        `<!-- Written by the CSharp.Refactor VS Code extension. It adds the CSharp.Refactor`,
        `     analyzers to every C# project this machine builds or opens; delete this file`,
        `     (or run "CSharp.Refactor: Remove the machine-wide wiring") to undo, or set`,
        `     <CSharpRefactorDisable>true</CSharpRefactorDisable> in a project to opt it out. -->`,
        `<Project>`,
        `  <ItemGroup Condition="'$(Language)' == 'C#' and '$(CSharpRefactorDisable)' != 'true' and Exists('${path.join(dir, "CSharp.Refactor.Analyzers.dll")}')">`,
        item("CSharp.Refactor.Analyzers.dll"),
        item("FSharp.Core.dll"),
        `  </ItemGroup>`,
        `</Project>`,
        ``,
    ].join("\n");
}

function isWired(context: vscode.ExtensionContext): "current" | "stale" | "none" {
    const p = propsPath();
    if (!fs.existsSync(p)) {
        return "none";
    }
    const text = fs.readFileSync(p, "utf8");
    return text.includes(analyzersDir(context)) ? "current" : "stale";
}

function writeProps(context: vscode.ExtensionContext): string {
    const p = propsPath();
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, propsText(analyzersDir(context)), "utf8");
    return p;
}

async function enable(context: vscode.ExtensionContext): Promise<void> {
    const p = writeProps(context);
    await context.globalState.update(DECLINED_KEY, false);
    const choice = await vscode.window.showInformationMessage(
        `CSharp.Refactor analyzers wired for every C# project via ${p}. Restart the C# language server (or reload the window) to see the hints; new builds pick them up immediately.`,
        "Reload Window");
    if (choice === "Reload Window") {
        await vscode.commands.executeCommand("workbench.action.reloadWindow");
    }
}

async function disable(): Promise<void> {
    const p = propsPath();
    if (fs.existsSync(p)) {
        fs.unlinkSync(p);
        vscode.window.showInformationMessage(`Removed ${p}. Reload the window to drop the hints from open projects.`);
    } else {
        vscode.window.showInformationMessage("CSharp.Refactor is not wired machine-wide; nothing to remove.");
    }
}

function status(context: vscode.ExtensionContext): void {
    const pkg = context.extension.packageJSON as {
        version: string;
        csharpRefactorBuild?: { analyzers?: string; built?: string };
    };
    const csharp = vscode.extensions.getExtension("ms-dotnettools.csharp");
    const devkit = vscode.extensions.getExtension("ms-dotnettools.csdevkit");
    const lines = [
        `CSharp.Refactor extension ${pkg.version}`,
        `Bundled analyzers ${pkg.csharpRefactorBuild?.analyzers ?? "?"} (built ${pkg.csharpRefactorBuild?.built || "unknown"})`,
        `Analyzers directory: ${analyzersDir(context)}`,
        `Wiring (${propsPath()}): ${isWired(context)}`,
        `C# extension: ${csharp ? csharp.packageJSON.version : "not installed"}`,
        `C# Dev Kit: ${devkit ? devkit.packageJSON.version : "not installed"}`,
    ];
    const channel = vscode.window.createOutputChannel("CSharp.Refactor");
    channel.clear();
    lines.forEach((l) => channel.appendLine(l));
    channel.show(true);
}


// ---- the csharp-refactor tool, driven from the palette (ported from the F# extension) ----

const TOOL = "csharp-refactor";

function toolVersion(): Promise<string | undefined> {
    return new Promise((resolve) => {
        cp.execFile(TOOL, ["--version"], { timeout: 15000 }, (error, stdout) => {
            resolve(error ? undefined : String(stdout).trim());
        });
    });
}

let terminal: vscode.Terminal | undefined;

function toolTerminal(): vscode.Terminal {
    if (!terminal || terminal.exitStatus) {
        terminal = vscode.window.createTerminal(TOOL);
    }
    return terminal;
}

async function pickTarget(): Promise<vscode.Uri | undefined> {
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length === 0) {
        vscode.window.showWarningMessage("CSharp.Refactor: open a folder or workspace first.");
        return undefined;
    }

    const found = await vscode.workspace.findFiles(
        "**/*.{sln,slnx,csproj}",
        "**/{node_modules,bin,obj,packages,.git}/**",
        100);

    if (found.length === 0) {
        return folders[0].uri;
    }

    const rank = (u: vscode.Uri) => (u.fsPath.endsWith(".csproj") ? 1 : 0);
    const targets = found.sort((a, b) => rank(a) - rank(b) || a.fsPath.localeCompare(b.fsPath));
    if (targets.length === 1) {
        return targets[0];
    }

    const pick = await vscode.window.showQuickPick(
        targets.map((u) => ({ label: vscode.workspace.asRelativePath(u), uri: u })),
        { placeHolder: "Solution or project to run csharp-refactor on" });
    return pick?.uri;
}

/** The tool has to be on PATH before a terminal line is worth sending; offer the install when it is not. */
async function ensureTool(): Promise<boolean> {
    if (await toolVersion()) {
        return true;
    }
    const pick = await vscode.window.showWarningMessage(
        "CSharp.Refactor: the csharp-refactor dotnet tool is not installed.", "Install it");
    if (pick === "Install it") {
        const t = toolTerminal();
        t.show();
        t.sendText("dotnet tool install -g csharp-refactor");
    }
    return false;
}

async function invoke(args: string): Promise<void> {
    const target = await pickTarget();
    if (!target || !(await ensureTool())) {
        return;
    }
    const t = toolTerminal();
    t.show();
    t.sendText(`${TOOL} "${target.fsPath}" ${args}`.trim());
}

async function run(): Promise<void> {
    const mode = await vscode.window.showQuickPick(
        [
            { label: "Report only", description: "--dry-run: list the fixes, change nothing", args: "--dry-run" },
            { label: "Apply fixes", description: "rewrite the files; every pass is verified and rolled back on error", args: "" },
            { label: "Apply fixes with --api-changes", description: "also public signatures, shapes and cross-file rewrites", args: "--api-changes" },
        ],
        { placeHolder: "How to run csharp-refactor" });
    if (mode) {
        await invoke(mode.args);
    }
}

async function runApiChanges(): Promise<void> {
    const pick = await vscode.window.showQuickPick(
        [
            { label: "Report only", description: "--dry-run --api-changes: change nothing", args: "--dry-run --api-changes" },
            { label: "Apply fixes", description: "rewrites public signatures, shapes and call sites solution-wide", args: "--api-changes" },
        ],
        { placeHolder: "csharp-refactor --api-changes: this rewrites your public surface" });
    if (pick) {
        await invoke(pick.args);
    }
}

async function report(): Promise<void> {
    const folder = vscode.workspace.workspaceFolders?.[0];
    const suggested = folder ? vscode.Uri.joinPath(folder.uri, "csharp-refactor.sarif").fsPath : "csharp-refactor.sarif";
    const target = await vscode.window.showInputBox({
        prompt: "Write the report to",
        value: suggested,
        placeHolder: "a .sarif, .csv or .html path",
    });
    if (target) {
        await invoke(`--dry-run --notes --report "${target}"`);
    }
}

type ToolRun = { cancelled?: true; failure?: string; output: string };

/** Run the tool to completion with progress and cancellation: for the commands that open a file afterwards. */
function runToCompletion(args: string[], title: string): Thenable<ToolRun> {
    return vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title, cancellable: true },
        (_progress, token) =>
            new Promise<ToolRun>((resolve) => {
                // no shell: execFile passes argv as is, so a path with a space or a & survives
                const child = cp.execFile(
                    TOOL, args, { timeout: 45 * 60 * 1000, maxBuffer: 32 * 1024 * 1024 },
                    (error, stdout, stderr) => {
                        const output = String(stdout ?? "");
                        if (token.isCancellationRequested) {
                            resolve({ cancelled: true, output });
                        } else if (error) {
                            resolve({ failure: String(stderr || stdout || error.message).trim() || "the tool failed", output });
                        } else {
                            resolve({ output });
                        }
                    });
                token.onCancellationRequested(() => child.kill());
            }));
}

/** Append the csharp-refactor block to the workspace's .editorconfig and open it, or open the one already carrying it. */
async function createConfig(): Promise<void> {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        vscode.window.showWarningMessage("CSharp.Refactor: open a folder or workspace first.");
        return;
    }
    const configPath = vscode.Uri.joinPath(folder.uri, ".editorconfig");
    let hasBlock = false;
    try {
        hasBlock = fs.readFileSync(configPath.fsPath, "utf8").includes("# ---- csharp-refactor ----");
    } catch {
        hasBlock = false;
    }

    if (!hasBlock) {
        if (!(await ensureTool())) {
            return;
        }
        const result = await runToCompletion(["--create-config", folder.uri.fsPath], "CSharp.Refactor: writing the .editorconfig block");
        if (result.cancelled) {
            return;
        }
        if (result.failure) {
            vscode.window.showErrorMessage(`CSharp.Refactor: ${result.failure}`);
            return;
        }
        vscode.window.showInformationMessage(
            "CSharp.Refactor: wrote the block into .editorconfig — every rule at its current default, so it changes nothing until you edit it.");
    }
    await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(configPath));
}

/** The advisory findings as a page, in the browser. */
async function reviewNotes(): Promise<void> {
    const target = await pickTarget();
    if (!target || !(await ensureTool())) {
        return;
    }
    const folder = vscode.workspace.workspaceFolders?.[0];
    const out = folder ? vscode.Uri.joinPath(folder.uri, "csharp-refactor-notes.html").fsPath : "csharp-refactor-notes.html";
    const result = await runToCompletion(
        ["--dry-run", "--notes", "only", "--report", out, target.fsPath],
        "CSharp.Refactor: collecting advisory notes (nothing is written to your code)");
    if (result.cancelled) {
        return;
    }
    if (result.failure) {
        vscode.window.showErrorMessage(`CSharp.Refactor: ${result.failure}`);
        return;
    }
    await vscode.env.openExternal(vscode.Uri.file(out));
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    context.subscriptions.push(
        vscode.commands.registerCommand("csharpRefactor.enable", () => enable(context)),
        vscode.commands.registerCommand("csharpRefactor.disable", () => disable()),
        vscode.commands.registerCommand("csharpRefactor.status", () => status(context)),
        vscode.commands.registerCommand("csharpRefactor.run", () => run()),
        vscode.commands.registerCommand("csharpRefactor.runApiChanges", () => runApiChanges()),
        vscode.commands.registerCommand("csharpRefactor.report", () => report()),
        vscode.commands.registerCommand("csharpRefactor.createConfig", () => createConfig()),
        vscode.commands.registerCommand("csharpRefactor.reviewNotes", () => reviewNotes()));

    switch (isWired(context)) {
        case "current":
            return;
        case "stale":
            // consent was given to an earlier version; keep it pointing at this one
            writeProps(context);
            return;
        case "none":
            if (context.globalState.get<boolean>(DECLINED_KEY)) {
                return;
            }
            const choice = await vscode.window.showInformationMessage(
                "CSharp.Refactor: add the bundled Roslyn analyzers to every C# project on this machine? " +
                "This writes one MSBuild props file under your user profile; it affects the C# language server and dotnet build, and is undone by the Remove command.",
                "Wire it", "Not now", "Never ask again");
            if (choice === "Wire it") {
                await enable(context);
            } else if (choice === "Never ask again") {
                await context.globalState.update(DECLINED_KEY, true);
            }
    }
}

export function deactivate(): void { }
