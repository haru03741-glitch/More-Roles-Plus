// Based on https://github.com/waffle-ful/Aeterna-End-K-not tools/bridge-mcp/server.js (GPL-3.0)
// mrp-bridge-mcp — ゲーム内 TestBridge (src/Bridge/TestBridge.cs) のファイルプロトコルを
// 型付きツールとして提供する stdio MCP サーバー。
// 入出力先は <Desktop>/MRP_Logs/bridge (Epic 版) / bridge-steam (Steam 版)。MRP_BRIDGE_DIR で上書き可。
// どちらの版を操作するかはサーバーごと (= 使う人ごと) に持つので、Epic 版と Steam 版を別々の人が同時に動かせる。

import path from "node:path";
import fs from "node:fs";
import os from "node:os";
import { execFileSync, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const LAUNCH_PS1 = path.join(__dirname, "..", "launch.ps1");
const MAX_TEXT = 12000;
const MAX_WAIT_SEC = 110;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ── 入出力先 ────────────────────────────────────────────────────────────────
const EDITIONS = {
    epic: { root: String.raw`C:\Program Files\Epic Games\AmongUs`, dir: "bridge" },
    steam: { root: String.raw`C:\Program Files (x86)\Steam\steamapps\common\Among Us`, dir: "bridge-steam" },
};
let edition = EDITIONS[process.env.MRP_AU_EDITION] ? process.env.MRP_AU_EDITION : "epic";
let cachedDesktop = null;
function desktopDir() {
    if (cachedDesktop) return cachedDesktop;
    let desktop = null;
    try {
        desktop = execFileSync("powershell.exe",
            ["-NoProfile", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Environment]::GetFolderPath('DesktopDirectory')"],
            { encoding: "utf8", timeout: 15000, windowsHide: true }).trim();
    } catch { }
    return (cachedDesktop = desktop || path.join(os.homedir(), "Desktop"));
}
function bridgeDir() {
    if (process.env.MRP_BRIDGE_DIR) return process.env.MRP_BRIDGE_DIR;
    return path.join(desktopDir(), "MRP_Logs", EDITIONS[edition].dir);
}
const file = {
    cmd: () => path.join(bridgeDir(), "bridge-cmd.txt"),
    out: () => path.join(bridgeDir(), "bridge-out.log"),
    state: () => path.join(bridgeDir(), "bridge-state.json"),
};

function outLen() { try { return fs.statSync(file.out()).size; } catch { return 0; } }

function readOutFrom(pos) {
    let fd;
    try { fd = fs.openSync(file.out(), "r"); } catch { return { text: "", nextPos: pos }; }
    try {
        const size = fs.fstatSync(fd).size;
        const from = pos > size ? 0 : pos;
        if (size <= from) return { text: "", nextPos: from };
        const buf = Buffer.alloc(size - from);
        fs.readSync(fd, buf, 0, buf.length, from);
        return { text: buf.toString("utf8"), nextPos: size };
    } finally { fs.closeSync(fd); }
}

const NOT_CONSUMED = "(cmd 未消費 — ゲーム未起動 / EnableTestBridge=false / 別の mod で起動中。game_launch で起動する)";

// 1 ファイル 20 行まで。消費 (= ファイル削除) を確認してから次を書く
async function send(lines, acceptSec = 25) {
    fs.mkdirSync(bridgeDir(), { recursive: true });
    for (let i = 0; i < lines.length; i += 20) {
        fs.writeFileSync(file.cmd(), lines.slice(i, i + 20).join("\n") + "\n", "utf8");
        const deadline = Date.now() + acceptSec * 1000;
        let ok = false;
        while (Date.now() < deadline) {
            await sleep(300);
            if (!fs.existsSync(file.cmd())) { ok = true; break; }
        }
        if (!ok) return false;
    }
    return true;
}

async function sendAndWait(lines, okRe, errRe, timeoutSec) {
    let pos = outLen();
    if (!(await send(lines))) return { ok: false, line: NOT_CONSUMED, output: "" };
    const deadline = Date.now() + timeoutSec * 1000;
    let all = "";
    while (Date.now() < deadline) {
        await sleep(300);
        const r = readOutFrom(pos);
        pos = r.nextPos;
        if (!r.text) continue;
        all += r.text;
        for (const raw of r.text.split("\n")) {
            const line = raw.trim();
            if (errRe && errRe.test(line)) return { ok: false, line, output: all };
            if (okRe && okRe.test(line)) return { ok: true, line, output: all };
        }
    }
    return { ok: false, line: `(応答 timeout ${timeoutSec}s)`, output: all };
}

function textResult(text, isError = false) {
    let t = String(text ?? "").trim();
    if (t.length > MAX_TEXT) t = "…(先頭省略)…\n" + t.slice(-MAX_TEXT);
    return { content: [{ type: "text", text: t || "(出力なし)" }], isError };
}

function cleanLines(lines) {
    for (const l of lines) if (/[\r\n]/.test(l)) throw new Error(`ディレクティブ行に改行は入れられない: ${JSON.stringify(l)}`);
    return lines;
}

function compileRe(p, label) {
    if (!p) return null;
    try { return new RegExp(p); } catch (e) { throw new Error(`${label} が正規表現として不正: ${e.message}`); }
}

// state の新しさで「いま MRP として動いている AU があるか」を判定する
function bridgeAliveMs() {
    try { return Date.now() - fs.statSync(file.state()).mtimeMs; } catch { return Infinity; }
}

const server = new McpServer(
    { name: "mrp-bridge-mcp", version: "0.1.0" },
    {
        instructions: [
            "More Roles Plus の実機テスト運転ツール。",
            "段取り: game_launch (BepInEx-MRP ツリーで AU を起動) → bridge_wait phase=Menu → bridge_send 'freeplay 0' → bridge_wait phase=InGame → 検証 → game_quit。",
            "他の mod で AU が起動中なら game_launch は何もしない (切り替え事故の防止)。",
            "game_launch の edition (epic|steam) で操作する版を選ぶ (以後の bridge_* と game_quit はその版だけを相手にする)。別の人が片方を使っている時はもう片方を使う。フリープレイは同時に動かせる (2 つ目は空いた番号で待ち受ける)。",
            "コマンド一覧は bridge_send ['help']。機能側の検証コマンドもそこに出る。",
        ].join("\n"),
    }
);

server.registerTool("bridge_send", {
    title: "ディレクティブ送信",
    description: "ブリッジにディレクティブを送り、応答を返す。lines は 1 要素 = 1 行 (20 行超は自動分割)。" +
        "okPattern/errPattern を指定すると一致行を待って判定、省略時は最終行の応答 (OK/ERR) まで待つ。" +
        "lines には 'wait phase=InGame 60' / 'wait marker <正規表現> 30' を混ぜてよい (wait 中は後の行が止まり、済むと進む) ので、" +
        "台本 1 本 (freeplay → wait → tp → hammer → shot) を 1 回の呼び出しで流せる。その時は timeoutSec を wait の合計以上 (上限 110)・1 回 20 行以内・okPattern は最後の行の応答に当てる。",
    inputSchema: {
        lines: z.array(z.string()).min(1).max(100),
        okPattern: z.string().optional(),
        errPattern: z.string().optional(),
        timeoutSec: z.number().int().min(3).max(MAX_WAIT_SEC).optional(),
    },
}, async ({ lines, okPattern, errPattern, timeoutSec }) => {
    const clean = cleanLines(lines);
    const t = timeoutSec ?? 30;
    let okRe = compileRe(okPattern, "okPattern");
    const errRe = compileRe(errPattern, "errPattern");
    if (!okRe) {
        // 最終行のコマンド名に対する OK/ERR を待つ
        const last = [...clean].reverse().find((l) => l.trim() && !l.trim().startsWith("#")) ?? "";
        const name = last.trim().split(/\s+/)[0].replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
        okRe = new RegExp(`\\] (OK|ERR) ${name}\\b`);
    }
    const r = await sendAndWait(clean, okRe, errRe, t);
    const failed = !r.ok || /\] ERR /.test(r.line);
    return textResult(`${failed ? "NG" : "OK"}: ${r.line}\n--- 応答 ---\n${r.output}`, failed);
});

server.registerTool("bridge_state", {
    title: "状態スナップショット",
    description: "bridge-state.json を書き直させて返す (phase / scene / map / local 座標 / players / errorsTotal / fps)。",
    inputSchema: {},
}, async () => {
    const before = Date.now();
    const r = await sendAndWait(["state"], /\] OK state/, /\] ERR state/, 20);
    if (!r.ok) return textResult(r.line, true);
    for (let i = 0; i < 20; i++) {
        try {
            const st = fs.statSync(file.state());
            if (st.mtimeMs >= before - 1000) return textResult(fs.readFileSync(file.state(), "utf8"));
        } catch { }
        await sleep(200);
    }
    return textResult("bridge-state.json の更新を確認できない", true);
});

server.registerTool("bridge_wait", {
    title: "条件待ち",
    description: "ゲーム内で条件を待つ。phase (Boot|Menu|Lobby|InGame|Meeting — Boot はメニュー初期化中) か marker (ログ行の正規表現) のどちらか。",
    inputSchema: {
        phase: z.string().optional(),
        marker: z.string().optional(),
        timeoutSec: z.number().int().min(1).max(MAX_WAIT_SEC).optional(),
    },
}, async ({ phase, marker, timeoutSec }) => {
    const t = timeoutSec ?? 60;
    if (!phase === !marker) return textResult("phase か marker のどちらか一方を指定する", true);
    if (marker && /\s/.test(marker)) return textResult("marker に空白は使えない (\\s を使う)", true);
    const line = phase ? `wait phase=${phase} ${t}` : `wait marker ${marker} ${t}`;
    const r = await sendAndWait([line], /\] OK wait /, /\] ERR wait /, t + 10);
    return textResult(`${r.ok ? "OK" : "NG"}: ${r.line}`, !r.ok);
});

// スクショは長辺 1092px に縮めて返す (原寸が要る時は full=true)。PNG/JPG とも同じ形式で保存する。
function shrinkPng(p, maxEdge = 1092) {
    const m = p.match(/\.(png|jpe?g)$/i);
    if (!m) return p;
    const jpg = m[1].toLowerCase() !== "png";
    const out = p.replace(/\.(png|jpe?g)$/i, `.s${maxEdge}.${jpg ? "jpg" : "png"}`);
    try {
        if (!fs.existsSync(out)) {
            const ps = `Add-Type -AssemblyName System.Drawing; $i=[System.Drawing.Image]::FromFile('${p.replace(/'/g, "''")}'); $m=[Math]::Max($i.Width,$i.Height); if($m -le ${maxEdge}){ $i.Dispose(); exit 3 }; $s=${maxEdge}/$m; $w=[int]($i.Width*$s); $h=[int]($i.Height*$s); $b=New-Object System.Drawing.Bitmap $w,$h; $g=[System.Drawing.Graphics]::FromImage($b); $g.InterpolationMode='HighQualityBicubic'; $g.DrawImage($i,0,0,$w,$h); $b.Save('${out.replace(/'/g, "''")}',[System.Drawing.Imaging.ImageFormat]::${jpg ? "Jpeg" : "Png"}); $g.Dispose(); $b.Dispose(); $i.Dispose()`;
            execFileSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps], { stdio: "ignore", timeout: 20000 });
        }
        return fs.existsSync(out) ? out : p;
    } catch { return p; }
}

server.registerTool("bridge_screenshot", {
    title: "スクリーンショット",
    description: "ゲーム画面を撮って画像で返す (長辺 1092px に縮小。原寸は full=true)。機械判定は bridge_state を優先し、見た目の確認だけに使う。",
    inputSchema: { name: z.string().regex(/^[A-Za-z0-9_-]{0,40}$/).optional(), full: z.boolean().optional() },
}, async ({ name, full }) => {
    const r = await sendAndWait([`shot ${name ?? "shot"}`], /\] OK shot /, /\] ERR shot/, 20);
    if (!r.ok) return textResult(r.line, true);
    const p = r.line.replace(/^.*\] OK shot /, "").trim();
    for (let i = 0; i < 30; i++) {
        await sleep(200);
        try {
            const st = fs.statSync(p);
            if (st.size > 0) {
                await sleep(200);
                const src = full ? p : shrinkPng(p);
                const data = fs.readFileSync(src).toString("base64");
                return { content: [{ type: "text", text: p }, { type: "image", data, mimeType: /\.jpe?g$/i.test(src) ? "image/jpeg" : "image/png" }] };
            }
        } catch { }
    }
    return textResult(`撮影ファイルが現れない: ${p}`, true);
});

server.registerTool("bridge_grep", {
    title: "ログ検索",
    description: "ゲーム内に保持している直近のログ行を正規表現で検索する。",
    inputSchema: { pattern: z.string().regex(/^\S+$/), max: z.number().int().min(1).max(200).optional() },
}, async ({ pattern, max }) => {
    const r = await sendAndWait([`grep ${pattern} ${max ?? 30}`], /\] OK grep /, /\] ERR grep/, 20);
    return textResult(r.output, !r.ok);
});

server.registerTool("bridge_errors", {
    title: "エラー一覧",
    description: "直近のエラーログと累計件数。",
    inputSchema: { max: z.number().int().min(1).max(200).optional() },
}, async ({ max }) => {
    const r = await sendAndWait([`errors ${max ?? 20}`], /\] OK errors /, null, 20);
    return textResult(r.output, !r.ok);
});

// 選んだ版の AU のプロセス番号 (パスで見分ける)
function auPids(ed) {
    try {
        const out = execFileSync("powershell.exe", ["-NoProfile", "-Command",
            `Get-Process 'Among Us' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '${EDITIONS[ed].root}\\*' } | ForEach-Object { $_.Id }`],
            { encoding: "utf8", timeout: 15000, windowsHide: true });
        return out.split(/\s+/).filter(Boolean).map(Number);
    } catch { return []; }
}

server.registerTool("game_launch", {
    title: "AU を MRP で起動",
    description: "Among Us を BepInEx-MRP ツリーで起動し、ブリッジの応答まで待つ。edition (epic 既定 | steam) で版を選び、以後の bridge_* と game_quit はその版を相手にする。その版の AU が既に起動中なら何もしない。",
    inputSchema: {
        edition: z.enum(["epic", "steam"]).optional(),
        timeoutSec: z.number().int().min(30).max(MAX_WAIT_SEC).optional(),
    },
}, async ({ edition: ed, timeoutSec }) => {
    if (ed) edition = ed;
    if (bridgeAliveMs() < 3000) return textResult(`既に MRP で起動中 (${edition}・bridge-state.json が更新されている)`);
    if (auPids(edition).length) return textResult(`${edition} 版の AU が別の用途で起動中なので触らない`, true);
    let out = "";
    if (edition === "steam") {
        const root = EDITIONS.steam.root;
        fs.mkdirSync(bridgeDir(), { recursive: true });
        try {
            const child = spawn(path.join(root, "Among Us.exe"),
                ["--doorstop-target-assembly", String.raw`BepInEx-MRP\core\BepInEx.Unity.IL2CPP.dll`],
                { cwd: root, detached: true, stdio: "ignore", env: { ...process.env, MRP_BRIDGE_DIR: bridgeDir() } });
            child.unref();
            out = `steam 版を起動 (pid ${child.pid})`;
        } catch (e) {
            return textResult(`steam 版の起動失敗: ${e.message}`, true);
        }
    } else {
        try {
            out = execFileSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", LAUNCH_PS1],
                { encoding: "utf8", timeout: 130000, windowsHide: true });
        } catch (e) {
            return textResult(`launch.ps1 失敗:\n${e.stdout ?? ""}${e.stderr ?? ""}`, true);
        }
    }
    const deadline = Date.now() + (timeoutSec ?? 90) * 1000;
    while (Date.now() < deadline) {
        if (bridgeAliveMs() < 3000) return textResult(`${out.trim()}\nブリッジ応答あり`);
        await sleep(1000);
    }
    return textResult(`${out.trim()}\nブリッジの state が更新されない (EnableTestBridge が false の可能性・steam 版は Steam 本体が起動しているか)`, true);
});

server.registerTool("game_quit", {
    title: "AU を終了",
    description: "game_launch で選んだ版 (既定 epic) の MRP の Among Us だけを終了する。ブリッジの state が新しい時 (= MRP で起動中) だけ動く。もう片方の版には触らない。",
    inputSchema: {},
}, async () => {
    if (bridgeAliveMs() > 3000) return textResult(`MRP で起動中の ${edition} 版 AU が見当たらない (他の mod で起動中なら触らない)`, true);
    const pids = auPids(edition);
    if (!pids.length) return textResult(`${edition} 版の AU のプロセスが見つからない`, true);
    try {
        for (const pid of pids) execFileSync("taskkill", ["/PID", String(pid), "/F"], { encoding: "utf8", windowsHide: true });
    } catch (e) {
        return textResult(`taskkill 失敗: ${e.message}`, true);
    }
    return textResult(`OK ${edition} 版の Among Us を終了した (pid ${pids.join(",")})`);
});

await server.connect(new StdioServerTransport());
