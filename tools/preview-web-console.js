#!/usr/bin/env node
// tools/preview-web-console.js
// 预览 qchost 的 Web 控制台，不需要重启宿主。
//
// 做法：每次请求读 quickcmdhost/web/index.html（也可用 --file 指向别的文件），
// 注入预览 token / 端口后由本进程提供页面；
// GET /api/state 与 /api/log 代理到正在运行的 qchost。
// 写操作（POST /api/*）不代理：宿主 token 每次启动随机生成，只注入真实页面。
//
// 用法：
//   node tools/preview-web-console.js
//   node tools/preview-web-console.js --port 43999 --upstream 41777
//   node tools/preview-web-console.js --no-upstream     # 只起静态页，API 返回离线
//   node tools/preview-web-console.js --file path/to/index.html
//
// 仅绑定 127.0.0.1。

"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const args = process.argv.slice(2);

function argValue(name, fallback) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
}

if (args.includes("--help") || args.includes("-h")) {
  console.log(`用法: node tools/preview-web-console.js [选项]

  --port <n>        预览端口（默认 43999，被占用时顺延）
  --upstream <n>    正在运行的 qchost Web 端口（默认读 settings.json 的 uiPort）
  --file <path>     页面文件路径（默认 quickcmdhost/web/index.html）
  --no-upstream     不代理 API（页面显示未连接）
`);
  process.exit(0);
}

const sourceFile = path.resolve(ROOT, argValue("--file", path.join("quickcmdhost", "web", "index.html")));
const startPort = Number(argValue("--port", "43999"));
const noUpstream = args.includes("--no-upstream");
function readSettingsPort() {
  try {
    const settingsFile = path.join(process.env.APPDATA || "", "quickcmd", "settings.json");
    const j = JSON.parse(fs.readFileSync(settingsFile, "utf8"));
    return Number(j.uiPort) || 41777;
  } catch (e) {
    return 41777;
  }
}

const upstreamPort = Number(argValue("--upstream", String(readSettingsPort())));
const upstream = "http://127.0.0.1:" + upstreamPort;

function extractHtml(file) {
  return fs.readFileSync(file, "utf8");
}

function contentTypeOf(res) {
  return res.headers.get("content-type") || "application/octet-stream";
}

async function proxyGet(url, res, kind) {
  if (noUpstream) {
    res.writeHead(503, {"Content-Type": "text/plain; charset=utf-8"});
    res.end("preview: --no-upstream 已启用，API 不可用");
    return;
  }
  try {
    const r = await fetch(url, {signal: AbortSignal.timeout(5000)});
    const body = Buffer.from(await r.arrayBuffer());
    res.writeHead(r.status, {"Content-Type": contentTypeOf(r), "Cache-Control": "no-store"});
    res.end(body);
  } catch (e) {
    const hint = "无法连接 qchost（" + upstream + "）：" + (e && e.message || e) +
      "\n先确认宿主在运行：quickcmd host status";
    res.writeHead(502, {"Content-Type": "text/plain; charset=utf-8"});
    res.end(kind === "json" ? JSON.stringify({ok: false, message: hint}) : hint);
  }
}

// 启动时先校验一次；实际请求时再读，改完 WebConsole.cs 刷新页面即可生效
try {
  extractHtml(sourceFile);
} catch (e) {
  console.error(String(e && e.message || e));
  process.exit(1);
}

const PORT = (() => {
  const p = Number(startPort) || 43999;
  return p;
})();

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://127.0.0.1:" + PORT);

  if (req.method === "GET" && (url.pathname === "/" || url.pathname === "/index.html")) {
    const port = server.address().port;
    let page;
    try {
      page = extractHtml(sourceFile)
        .replace("__QC_TOKEN__", "preview-token")
        .replace("__QC_PORT__", String(port));
    } catch (e) {
      res.writeHead(500, {"Content-Type": "text/plain; charset=utf-8"});
      res.end("读取页面失败：" + (e && e.message || e));
      return;
    }
    res.writeHead(200, {"Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store"});
    res.end(page);
    return;
  }

  if (req.method === "GET" && url.pathname === "/api/state") {
    await proxyGet(upstream + "/api/state", res, "json");
    return;
  }

  if (req.method === "GET" && url.pathname === "/api/log") {
    await proxyGet(upstream + "/api/log" + url.search, res, "text");
    return;
  }

  if (req.method === "GET" && url.pathname === "/favicon.ico") {
    res.writeHead(204);
    res.end();
    return;
  }

  if (req.method === "POST" && url.pathname.startsWith("/api/")) {
    res.writeHead(501, {"Content-Type": "application/json; charset=utf-8"});
    res.end(JSON.stringify({
      ok: false,
      message: "预览模式不代理写操作；请到真实控制台 http://127.0.0.1:" + upstreamPort + "/ 执行",
    }));
    return;
  }

  res.writeHead(404, {"Content-Type": "text/plain; charset=utf-8"});
  res.end("not found");
});

let attempt = 0;
server.on("error", err => {
  if (err.code === "EADDRINUSE" && attempt < 20) {
    attempt++;
    server.listen(PORT + attempt, "127.0.0.1");
    return;
  }
  console.error("启动失败：" + err.message);
  process.exit(1);
});
server.on("listening", () => {
  const port = server.address().port;
  console.log("quickcmd web console preview");
  console.log("  source  : " + sourceFile);
  console.log("  listen  : http://127.0.0.1:" + port + "/");
  console.log("  upstream: " + (noUpstream ? "(disabled)" : upstream + "  (GET only)"));
  console.log("  写操作不代理；启动/停止/固定请到真实控制台。");
});
server.listen(PORT, "127.0.0.1");
