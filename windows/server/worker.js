// Cyclop for Windows — the update and usage-count server.
//
// GET /v1/check?v=0.9.0&arch=x64&os=10.0.22631&id=<uuid>
//   Answers with the newest Windows release: {version, url, sha256, notes}.
//   With an id, also records that this install was alive today. The id is a
//   random number the app made on first launch; no IP or anything else about
//   the user is stored. Without an id (count switched off) nothing is stored.
//
// GET /download?arch=x64|arm64|mac
//   Sends the newest build as a file download from this address — the browser
//   saves it straight away instead of sending the visitor to GitHub — and
//   counts it (per day and platform, nothing else). x64 and arm64 are this
//   project's Windows releases; mac is the original Cyclop for macOS, by Adil
//   Kalikbergenov (MIT), from its own GitHub releases. The website's buttons
//   point here, so they never need editing when a version comes out.
//
// POST /v1/feedback   {kind, text, contact?, version?, os?, arch?, source, log?}
//   A bug report or a suggestion, from the app's form or the website's.
//   Stored for the author; no IP is kept (a hashed one is used for a few
//   minutes to stop floods, in the cache, never in the database).
//
// GET /feedback, POST /feedback
//   The author's inbox: new and resolved messages, mark resolved, delete.
//   Same password as /stats.
//
// GET /stats
//   Users per day, week and month, versions, new installs. Behind a password
//   (the STATS_PASSWORD secret); the browser asks for it — any user name.
//
// Releases are read from GitHub: the newest release whose tag starts with
// "win-v", with assets Cyclop-win-<arch>.exe and Cyclop-win-<arch>.exe.sha256,
// exactly as .github/workflows/windows-release.yml publishes them.

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const VERSION = /^\d+\.\d+\.\d+$/;
const OS = /^[\d.]{1,32}$/;
const ARCHES = ["x64", "arm64"];
const CACHE_SECONDS = 600;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    try {
      if (url.pathname === "/v1/check") return await check(url, env, ctx);
      if (url.pathname === "/download") return await download(url, env, ctx);
      if (url.pathname === "/stats") return await stats(request, env);
      if (url.pathname === "/v1/feedback") return await receiveFeedback(request, env);
      if (url.pathname === "/feedback") return await inbox(request, env, url);
      return new Response("Cyclop", { status: 404 });
    } catch (e) {
      return json({ error: String(e && e.message || e) }, 500);
    }
  },
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    // Readable from the website, which shows the current version.
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", "access-control-allow-origin": "*" },
  });
}

function today() {
  return new Date().toISOString().slice(0, 10);
}

function daysAgo(n) {
  return new Date(Date.now() - n * 86400000).toISOString().slice(0, 10);
}

// MARK: - Check

async function check(url, env, ctx) {
  const arch = ARCHES.includes(url.searchParams.get("arch")) ? url.searchParams.get("arch") : "x64";
  const version = url.searchParams.get("v") || "";
  const os = url.searchParams.get("os") || "";
  const id = url.searchParams.get("id") || "";

  if (UUID.test(id) && VERSION.test(version)) {
    // Recorded after the answer is sent, so a slow write never slows the app.
    ctx.waitUntil(
      env.DB.prepare(
        "INSERT INTO pings (id, day, version, arch, os) VALUES (?1, ?2, ?3, ?4, ?5) " +
        "ON CONFLICT (id, day) DO UPDATE SET version = excluded.version, os = excluded.os"
      ).bind(id.toLowerCase(), today(), version, arch, OS.test(os) ? os : "").run()
    );
  }
  const { source, ...answer } = await latest(env, ctx, arch);
  return json(answer);
}

/// The newest release for one architecture, cached for ten minutes.
///
/// Read from github.com's own "latest release" redirect and download links,
/// not the API: the API allows 60 unauthenticated calls an hour per address,
/// and Cloudflare's outgoing addresses are shared with countless other sites —
/// the limit is usually spent before this worker asks. The API stays as the
/// fallback for when the newest release is not a Windows one.
async function latest(env, ctx, arch) {
  const cache = caches.default;
  const key = new Request(`https://cache.cyclop/v3/latest/${env.GITHUB_REPO}/${arch}`);
  const hit = await cache.match(key);
  if (hit) return hit.json();

  let answer = { version: null };
  const name = `Cyclop-win-${arch}.exe`;
  try {
    const tag = await latestTag(env.GITHUB_REPO);
    answer = tag && tag.startsWith("win-v") ? await fromTag(env.GITHUB_REPO, tag, name) : { version: null };
    if (!answer.url) answer = await fromApi(env, name);
  } catch (e) {
    console.log(`lookup ${arch} failed: ${e}`);
  }
  answer = await settle(env, ctx, `win-${arch}`, answer);
  remember(ctx, key, answer);
  return answer;
}

/// GitHub does not always answer every Cloudflare location the same — one
/// can be throttled while the next is fine. The last answer that worked is
/// kept in the database, and used whenever a fresh lookup comes back empty,
/// so a button never sends anyone to a GitHub page instead of a file.
async function settle(env, ctx, name, answer) {
  try {
    if (answer.url) {
      ctx.waitUntil(env.DB.prepare(
        "INSERT INTO known (name, value) VALUES (?1, ?2) ON CONFLICT (name) DO UPDATE SET value = excluded.value"
      ).bind(name, JSON.stringify(answer)).run());
      return { ...answer, source: "github" };
    }
    const row = await env.DB.prepare("SELECT value FROM known WHERE name = ?1").bind(name).first();
    if (row) return { ...JSON.parse(row.value), source: "known" };
  } catch (e) {
    console.log(`known ${name}: ${e}`);
  }
  return answer;
}

async function latestTag(repo) {
  const response = await fetch(`https://github.com/${repo}/releases/latest`, {
    redirect: "manual", headers: { "user-agent": "cyclop-updates" },
  });
  const match = (response.headers.get("location") || "").match(/\/releases\/tag\/([^/?#]+)$/);
  return match ? decodeURIComponent(match[1]) : null;
}

async function fromTag(repo, tag, name) {
  const url = `https://github.com/${repo}/releases/download/${tag}/${name}`;
  const sum = await fetch(`${url}.sha256`, { headers: { "user-agent": "cyclop-updates" } });
  if (!sum.ok) return { version: null };
  const sha256 = ((await sum.text()).match(/[0-9a-f]{64}/i) || [""])[0].toLowerCase();
  return sha256 ? { version: tag.slice("win-v".length), url, sha256, notes: null } : { version: null };
}

async function fromApi(env, name) {
  const headers = { "user-agent": "cyclop-updates", accept: "application/vnd.github+json" };
  if (env.GITHUB_TOKEN) headers.authorization = `Bearer ${env.GITHUB_TOKEN}`;
  const response = await fetch(`https://api.github.com/repos/${env.GITHUB_REPO}/releases?per_page=20`, { headers });
  if (!response.ok) return { version: null };
  const release = (await response.json()).find((r) => !r.draft && !r.prerelease && r.tag_name.startsWith("win-v"));
  return release ? fromTag(env.GITHUB_REPO, release.tag_name, name) : { version: null };
}

/// An answer is kept ten minutes; "nothing found" only one, so a hiccup at
/// GitHub does not hide a release for long.
function remember(ctx, key, answer) {
  const seconds = answer.url ? CACHE_SECONDS : 60;
  ctx.waitUntil(caches.default.put(key, new Response(JSON.stringify(answer), {
    headers: { "content-type": "application/json", "cache-control": `max-age=${seconds}` },
  })));
}

// MARK: - Download

const MAC_REPO = "akalikbergenov/cyclop";

async function download(url, env, ctx) {
  const wanted = url.searchParams.get("arch");
  const platform = wanted === "mac" ? "mac" : ARCHES.includes(wanted) ? wanted : "x64";
  const target = platform === "mac" ? await latestMac(env, ctx) : await latest(env, ctx, platform);
  if (!target.url) {
    // Nothing has ever been published for this platform.
    return Response.redirect(`https://github.com/${env.GITHUB_REPO}/releases`, 302);
  }
  let upstream = null;
  try {
    upstream = await fetch(target.url, { headers: { "user-agent": "cyclop-updates" } });
  } catch (e) {
    console.log(`download ${platform} failed: ${e}`);
  }
  // Could not pass the file through: the browser fetches it from GitHub
  // itself — still a download, not a page.
  if (!upstream || !upstream.ok || !upstream.body) {
    console.log(`download ${platform}: upstream ${upstream && upstream.status}, redirecting to the file`);
    return Response.redirect(target.url, 302);
  }
  ctx.waitUntil(
    env.DB.prepare(
      "INSERT INTO downloads (day, arch, n) VALUES (?1, ?2, 1) ON CONFLICT (day, arch) DO UPDATE SET n = n + 1"
    ).bind(today(), platform).run()
  );
  const name = platform === "mac" ? `Cyclop-${target.version}.dmg` : "Cyclop.exe";
  const headers = {
    "content-type": platform === "mac" ? "application/x-apple-diskimage" : "application/vnd.microsoft.portable-executable",
    "content-disposition": `attachment; filename="${name}"`,
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
    "x-cyclop-source": target.source || "cache",
  };
  const length = upstream.headers.get("content-length");
  if (length) headers["content-length"] = length;
  // The body is streamed through as it arrives, never held in memory.
  return new Response(upstream.body, { status: 200, headers });
}

/// The newest .dmg of the original macOS app, found the same way: its tag
/// from the "latest" redirect, the file named after the version. No probe of
/// the file first — the download itself is the check, and GitHub's storage
/// does not answer a bare HEAD the same way everywhere.
const MAC_FALLBACK = {
  version: "0.8.3",
  url: "https://github.com/akalikbergenov/cyclop/releases/download/v0.8.3/Cyclop-0.8.3.dmg",
};

async function latestMac(env, ctx) {
  const key = new Request(`https://cache.cyclop/v3/latest/${MAC_REPO}/mac`);
  const hit = await caches.default.match(key);
  if (hit) return hit.json();
  let answer = { version: null };
  try {
    const tag = await latestTag(MAC_REPO);
    if (tag) {
      const version = tag.replace(/^v/, "");
      answer = { version, url: `https://github.com/${MAC_REPO}/releases/download/${tag}/Cyclop-${version}.dmg` };
    } else {
      const headers = { "user-agent": "cyclop-updates", accept: "application/vnd.github+json" };
      const response = await fetch(`https://api.github.com/repos/${MAC_REPO}/releases/latest`, { headers });
      if (response.ok) {
        const release = await response.json();
        const dmg = (release.assets || []).find((a) => a.name.endsWith(".dmg"));
        if (dmg) answer = { version: release.tag_name.replace(/^v/, ""), url: dmg.browser_download_url };
      }
    }
  } catch (e) {
    console.log(`lookup mac failed: ${e}`);
  }
  answer = await settle(env, ctx, "mac", answer);
  if (!answer.url) answer = { ...MAC_FALLBACK, source: "built-in" };
  remember(ctx, key, answer);
  return answer;
}

// MARK: - Stats

function authorized(request, env) {
  if (!env.STATS_PASSWORD) return false;
  const header = request.headers.get("authorization") || "";
  if (!header.startsWith("Basic ")) return false;
  let decoded = "";
  try { decoded = atob(header.slice(6)); } catch { return false; }
  const password = decoded.slice(decoded.indexOf(":") + 1);
  // Length-independent comparison, so timing says nothing about the password.
  const a = new TextEncoder().encode(password);
  const b = new TextEncoder().encode(env.STATS_PASSWORD);
  let diff = a.length ^ b.length;
  for (let i = 0; i < Math.max(a.length, b.length); i++) diff |= (a[i] || 0) ^ (b[i] || 0);
  return diff === 0;
}

async function stats(request, env) {
  if (!authorized(request, env)) {
    return new Response("Password required", {
      status: 401,
      headers: { "www-authenticate": 'Basic realm="Cyclop stats", charset="UTF-8"' },
    });
  }
  const db = env.DB;
  const one = async (sql, ...args) => (await db.prepare(sql).bind(...args).first()) || {};
  const all = async (sql, ...args) => (await db.prepare(sql).bind(...args).all()).results || [];

  const dau = (await one("SELECT COUNT(*) AS n FROM pings WHERE day = ?1", today())).n || 0;
  const wau = (await one("SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1", daysAgo(6))).n || 0;
  const mau = (await one("SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1", daysAgo(29))).n || 0;
  const total = (await one("SELECT COUNT(DISTINCT id) AS n FROM pings")).n || 0;
  const daily = await all("SELECT day, COUNT(*) AS n FROM pings WHERE day >= ?1 GROUP BY day ORDER BY day", daysAgo(29));
  const fresh = await all(
    "SELECT first AS day, COUNT(*) AS n FROM (SELECT id, MIN(day) AS first FROM pings GROUP BY id) " +
    "WHERE first >= ?1 GROUP BY first ORDER BY first", daysAgo(29));
  // Each install counted once, at the version it reported last this week.
  const versions = await all(
    "SELECT version, COUNT(*) AS n FROM (SELECT version, ROW_NUMBER() OVER (PARTITION BY id ORDER BY day DESC) AS rn " +
    "FROM pings WHERE day >= ?1) WHERE rn = 1 GROUP BY version ORDER BY n DESC", daysAgo(6));
  const downloads = await all(
    "SELECT day, SUM(n) AS n FROM downloads WHERE day >= ?1 GROUP BY day ORDER BY day", daysAgo(29));
  const downloadsTotal = (await one("SELECT SUM(n) AS n FROM downloads")).n || 0;
  const newFeedback = (await one("SELECT COUNT(*) AS n FROM feedback WHERE status = 'new'")).n || 0;
  const platforms = await all(
    "SELECT CASE arch WHEN 'mac' THEN 'Mac' WHEN 'arm64' THEN 'Windows ARM' ELSE 'Windows x64' END AS arch, " +
    "SUM(n) AS n FROM downloads GROUP BY arch ORDER BY n DESC");
  const arches = await all(
    "SELECT arch, COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1 GROUP BY arch ORDER BY n DESC", daysAgo(6));

  return new Response(page({ dau, wau, mau, total, daily, fresh, versions, arches, downloads, downloadsTotal, platforms, newFeedback }), {
    headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" },
  });
}

function escape(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function bars(rows, title) {
  const byDay = new Map(rows.map((r) => [r.day, r.n]));
  const days = Array.from({ length: 30 }, (_, i) => daysAgo(29 - i));
  const max = Math.max(1, ...days.map((d) => byDay.get(d) || 0));
  const cols = days.map((d) => {
    const n = byDay.get(d) || 0;
    return `<div class="col" title="${d}: ${n}"><div class="bar" style="height:${Math.round((n / max) * 100)}%"></div></div>`;
  }).join("");
  return `<section><h2>${title}</h2><div class="chart">${cols}</div>
    <div class="axis"><span>${days[0]}</span><span>${days[29]}</span></div></section>`;
}

function table(rows, key, title) {
  const sum = rows.reduce((s, r) => s + r.n, 0) || 1;
  const body = rows.map((r) =>
    `<tr><td>${escape(r[key] || "—")}</td><td>${r.n}</td><td><div class="share" style="width:${Math.round((r.n / sum) * 100)}%"></div></td></tr>`
  ).join("") || `<tr><td colspan="3" class="muted">—</td></tr>`;
  return `<section><h2>${title}</h2><table>${body}</table></section>`;
}

function page(s) {
  return `<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Cyclop — статистика</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; background: #0b0b0c; color: #f2f2f2; font: 15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
  main { max-width: 860px; margin: 0 auto; padding: 32px 16px 48px; }
  h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em; color: #8a8a8f; margin: 0 0 12px; font-weight: 600; }
  .muted { color: #8a8a8f; }
  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin: 24px 0; }
  .tile { background: #161618; border: 1px solid #242428; border-radius: 14px; padding: 16px; }
  .tile b { display: block; font-size: 30px; font-variant-numeric: tabular-nums; }
  section { background: #161618; border: 1px solid #242428; border-radius: 14px; padding: 16px; margin-top: 12px; }
  .chart { display: flex; align-items: flex-end; gap: 3px; height: 140px; }
  .col { flex: 1; height: 100%; display: flex; align-items: flex-end; }
  .bar { width: 100%; min-height: 2px; background: #0a84ff; border-radius: 3px 3px 0 0; }
  .axis { display: flex; justify-content: space-between; color: #8a8a8f; font-size: 12px; margin-top: 6px; }
  table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
  td { padding: 6px 0; border-top: 1px solid #242428; } td:nth-child(2) { width: 70px; text-align: right; padding-right: 12px; }
  td:last-child { width: 45%; } .share { height: 8px; background: #30d158; border-radius: 4px; }
  .grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; }
  .grid2 section { margin-top: 0; }
</style></head><body><main>
<h1>Cyclop для Windows</h1><div class="muted">Уникальные установки, по UTC. Обновлено: ${escape(new Date().toISOString().slice(0, 16).replace("T", " "))}</div>
<div class="tiles">
  <div class="tile"><span class="muted">Сегодня</span><b>${s.dau}</b></div>
  <div class="tile"><span class="muted">За 7 дней</span><b>${s.wau}</b></div>
  <div class="tile"><span class="muted">За 30 дней</span><b>${s.mau}</b></div>
  <div class="tile"><span class="muted">Всего</span><b>${s.total}</b></div>
  <div class="tile"><span class="muted">Скачиваний с сайта</span><b>${s.downloadsTotal}</b></div>
  <a class="tile" href="/feedback" style="text-decoration:none;color:inherit;${s.newFeedback ? "border-color:#0a84ff" : ""}"><span class="muted">Новые отзывы →</span><b>${s.newFeedback}</b></a>
</div>
${bars(s.daily, "Пользователи по дням")}
${bars(s.fresh, "Новые установки по дням")}
${bars(s.downloads, "Скачивания с сайта по дням")}
<div class="grid2" style="margin-top:12px">
${table(s.versions, "version", "Версии за 7 дней")}
${table(s.arches, "arch", "Архитектура за 7 дней")}
</div>
<div class="grid2" style="margin-top:12px">
${table(s.platforms, "arch", "Скачивания с сайта по платформам, всего")}
</div>
</main></body></html>`;
}

// MARK: - Feedback

const CORS = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "POST, OPTIONS",
  "access-control-allow-headers": "content-type",
};

async function receiveFeedback(request, env) {
  if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });
  if (request.method !== "POST") return new Response("POST only", { status: 405, headers: CORS });
  const reply = (body, status = 200) => new Response(JSON.stringify(body), {
    status, headers: { ...CORS, "content-type": "application/json; charset=utf-8" },
  });

  let body;
  try { body = await request.json(); } catch { return reply({ ok: false, error: "bad json" }, 400); }
  // A field humans never see; a bot that fills it is told "thanks" and ignored.
  if (body.website) return reply({ ok: true });

  const text = String(body.text || "").trim().slice(0, 5000);
  if (text.length < 3) return reply({ ok: false, error: "empty" }, 400);
  if (await flooded(request)) return reply({ ok: false, error: "too many" }, 429);

  const kind = body.kind === "bug" ? "bug" : "idea";
  const source = body.source === "app" ? "app" : "site";
  const contact = String(body.contact || "").trim().slice(0, 200);
  const version = VERSION.test(String(body.version || "")) ? String(body.version) : "";
  const os = OS.test(String(body.os || "")) ? String(body.os) : "";
  const arch = ARCHES.includes(body.arch) ? body.arch : "";
  const log = source === "app" ? String(body.log || "").slice(-65536) : "";

  await env.DB.prepare(
    "INSERT INTO feedback (created, kind, text, contact, version, os, arch, source, log, status) " +
    "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, 'new')"
  ).bind(new Date().toISOString(), kind, text, contact, version, os, arch, source, log).run();
  return reply({ ok: true });
}

/// At most six messages per ten minutes from one address. The address is
/// hashed and lives only in this location's cache for those ten minutes.
async function flooded(request) {
  const ip = request.headers.get("cf-connecting-ip") || "";
  const window = Math.floor(Date.now() / 600000);
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`${ip}|${window}|cyclop`));
  const hash = [...new Uint8Array(digest)].slice(0, 12).map((b) => b.toString(16).padStart(2, "0")).join("");
  const key = new Request(`https://ratelimit.cyclop/${hash}`);
  const hit = await caches.default.match(key);
  const count = hit ? parseInt(await hit.text(), 10) || 0 : 0;
  if (count >= 6) return true;
  await caches.default.put(key, new Response(String(count + 1), { headers: { "cache-control": "max-age=600" } }));
  return false;
}

async function inbox(request, env, url) {
  if (!authorized(request, env)) {
    return new Response("Password required", {
      status: 401,
      headers: { "www-authenticate": 'Basic realm="Cyclop stats", charset="UTF-8"' },
    });
  }
  if (request.method === "POST") {
    // Only this page may change things: a form on another site would ride on
    // the browser's saved password otherwise.
    const origin = request.headers.get("origin") || request.headers.get("referer") || "";
    if (!origin.startsWith(url.origin)) return new Response("Forbidden", { status: 403 });
    const form = await request.formData();
    const id = parseInt(form.get("id"), 10);
    const action = form.get("action");
    if (id > 0 && action === "done") await env.DB.prepare("UPDATE feedback SET status = 'done' WHERE id = ?1").bind(id).run();
    if (id > 0 && action === "reopen") await env.DB.prepare("UPDATE feedback SET status = 'new' WHERE id = ?1").bind(id).run();
    if (id > 0 && action === "delete") await env.DB.prepare("DELETE FROM feedback WHERE id = ?1").bind(id).run();
    return Response.redirect(`${url.origin}/feedback?show=${encodeURIComponent(form.get("show") || "new")}`, 303);
  }

  const show = ["new", "done", "all"].includes(url.searchParams.get("show")) ? url.searchParams.get("show") : "new";
  const where = show === "all" ? "" : "WHERE status = ?1";
  const stmt = env.DB.prepare(`SELECT * FROM feedback ${where} ORDER BY id DESC LIMIT 200`);
  const rows = (await (show === "all" ? stmt : stmt.bind(show)).all()).results || [];
  const counts = (await env.DB.prepare("SELECT status, COUNT(*) AS n FROM feedback GROUP BY status").all()).results || [];
  const count = (s) => (counts.find((c) => c.status === s) || { n: 0 }).n;

  const items = rows.map((r) => {
    const contact = r.contact ? contactLink(r.contact) : '<span class="muted">без контакта</span>';
    const meta = [r.source === "app" ? "из программы" : "с сайта", r.version && `v${r.version}`, r.arch, r.os && `Windows ${r.os}`]
      .filter(Boolean).map(escape).join(" · ");
    const toggle = r.status === "new"
      ? `<button name="action" value="done">✓ Решено</button>`
      : `<button name="action" value="reopen">Вернуть в новые</button>`;
    return `<article class="${r.status}">
      <header><span class="kind ${r.kind}">${r.kind === "bug" ? "Ошибка" : "Идея"}</span>
        <span class="muted">${escape(r.created.slice(0, 16).replace("T", " "))} UTC · ${meta}</span></header>
      <p class="text">${escape(r.text)}</p>
      <div class="contact">${contact}</div>
      ${r.log ? `<details><summary>Журнал программы</summary><pre>${escape(r.log)}</pre></details>` : ""}
      <form method="post"><input type="hidden" name="id" value="${r.id}"><input type="hidden" name="show" value="${show}">
        ${toggle}<button name="action" value="delete" class="danger" onclick="return confirm('Удалить сообщение?')">Удалить</button></form>
    </article>`;
  }).join("") || '<p class="muted empty">Пусто.</p>';

  const tab = (key, label, n) => `<a href="?show=${key}" class="${show === key ? "on" : ""}">${label}${n === null ? "" : ` <b>${n}</b>`}</a>`;
  const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Cyclop — отзывы</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; background: #0b0b0c; color: #f2f2f2; font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
  main { max-width: 860px; margin: 0 auto; padding: 32px 16px 48px; }
  a { color: #6ab4ff; } h1 { font-size: 22px; margin: 0 0 4px; } .muted { color: #8a8a8f; }
  nav { display: flex; gap: 8px; margin: 20px 0; flex-wrap: wrap; }
  nav a { text-decoration: none; color: #ccc; border: 1px solid #242428; border-radius: 999px; padding: 6px 14px; }
  nav a.on { background: #1d1d22; color: #fff; border-color: #3a3a40; }
  nav a b { color: #0a84ff; }
  article { background: #161618; border: 1px solid #242428; border-radius: 14px; padding: 16px; margin-bottom: 12px; }
  article.done { opacity: .6; }
  header { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; font-size: 13px; }
  .kind { font-weight: 700; font-size: 12px; padding: 2px 9px; border-radius: 999px; }
  .kind.bug { background: rgba(255,69,58,.18); color: #ff6b61; } .kind.idea { background: rgba(10,132,255,.18); color: #4da3ff; }
  .text { white-space: pre-wrap; word-break: break-word; margin: 10px 0; font-size: 15.5px; }
  .contact { font-size: 14px; margin-bottom: 10px; }
  details { margin-bottom: 10px; } summary { cursor: pointer; color: #8a8a8f; font-size: 13px; }
  pre { max-height: 320px; overflow: auto; background: #0b0b0c; border: 1px solid #242428; border-radius: 8px; padding: 10px; font-size: 12px; white-space: pre-wrap; }
  form { display: flex; gap: 8px; }
  button { font: inherit; font-size: 13px; background: #24242a; color: #fff; border: 1px solid #34343a; border-radius: 8px; padding: 6px 12px; cursor: pointer; }
  button:hover { background: #2e2e35; } button.danger { color: #ff6b61; }
  .empty { padding: 40px 0; text-align: center; }
</style></head><body><main>
<h1>Отзывы</h1><div class="muted">Ошибки и предложения от пользователей · <a href="/stats">статистика</a></div>
<nav>${tab("new", "Новые", count("new"))}${tab("done", "Решённые", count("done"))}${tab("all", "Все", null)}</nav>
${items}
</main></body></html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
}

/// An address becomes a mail link, an @name a Telegram link; anything else
/// is shown as written.
function contactLink(contact) {
  const c = contact.trim();
  if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(c)) return `✉️ <a href="mailto:${escape(c)}">${escape(c)}</a>`;
  if (/^@[A-Za-z0-9_]{4,32}$/.test(c)) return `✈️ <a href="https://t.me/${escape(c.slice(1))}" target="_blank" rel="noopener">${escape(c)}</a>`;
  return `☎️ ${escape(c)}`;
}
