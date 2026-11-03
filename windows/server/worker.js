// Cyclop for Windows — the update and usage-count server.
//
// GET /v1/check?v=0.9.0&arch=x64&os=10.0.22631&id=<uuid>
//   Answers with the newest Windows release: {version, url, sha256, notes}.
//   With an id, also records that this install was alive today. The id is a
//   random number the app made on first launch; no IP or anything else about
//   the user is stored. Without an id (count switched off) nothing is stored.
//
// GET /download?arch=x64|arm64
//   Redirects to the newest exe and counts the download (per day and
//   architecture, nothing else). The website's download button points here, so
//   it never needs editing when a version comes out.
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
  return json(await latest(env, ctx, arch));
}

/// The newest release for one architecture, cached for ten minutes: GitHub
/// allows an unauthenticated client 60 API calls an hour.
async function latest(env, ctx, arch) {
  const cache = caches.default;
  const key = new Request(`https://cache.cyclop/latest/${env.GITHUB_REPO}/${arch}`);
  const hit = await cache.match(key);
  if (hit) return hit.json();

  const headers = { "user-agent": "cyclop-updates", accept: "application/vnd.github+json" };
  if (env.GITHUB_TOKEN) headers.authorization = `Bearer ${env.GITHUB_TOKEN}`;
  const response = await fetch(`https://api.github.com/repos/${env.GITHUB_REPO}/releases?per_page=20`, { headers });
  let answer = { version: null };
  if (response.ok) {
    const releases = await response.json();
    const release = releases.find((r) => !r.draft && !r.prerelease && r.tag_name.startsWith("win-v"));
    if (release) {
      const name = `Cyclop-win-${arch}.exe`;
      const exe = release.assets.find((a) => a.name === name);
      const sum = release.assets.find((a) => a.name === `${name}.sha256`);
      if (exe && sum) {
        const text = await (await fetch(sum.browser_download_url, { headers: { "user-agent": "cyclop-updates" } })).text();
        const sha256 = (text.match(/[0-9a-f]{64}/i) || [""])[0].toLowerCase();
        if (sha256) {
          answer = {
            version: release.tag_name.slice("win-v".length),
            url: exe.browser_download_url,
            sha256,
            notes: (release.body || "").slice(0, 1000),
          };
        }
      }
    }
  }
  ctx.waitUntil(cache.put(key, new Response(JSON.stringify(answer), {
    headers: { "content-type": "application/json", "cache-control": `max-age=${CACHE_SECONDS}` },
  })));
  return answer;
}

// MARK: - Download

async function download(url, env, ctx) {
  const arch = ARCHES.includes(url.searchParams.get("arch")) ? url.searchParams.get("arch") : "x64";
  const release = await latest(env, ctx, arch);
  if (!release.url) {
    return Response.redirect(`https://github.com/${env.GITHUB_REPO}/releases`, 302);
  }
  ctx.waitUntil(
    env.DB.prepare(
      "INSERT INTO downloads (day, arch, n) VALUES (?1, ?2, 1) ON CONFLICT (day, arch) DO UPDATE SET n = n + 1"
    ).bind(today(), arch).run()
  );
  return new Response(null, {
    status: 302,
    headers: { location: release.url, "cache-control": "no-store", "access-control-allow-origin": "*" },
  });
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
  const arches = await all(
    "SELECT arch, COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1 GROUP BY arch ORDER BY n DESC", daysAgo(6));

  return new Response(page({ dau, wau, mau, total, daily, fresh, versions, arches, downloads, downloadsTotal }), {
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
</div>
${bars(s.daily, "Пользователи по дням")}
${bars(s.fresh, "Новые установки по дням")}
${bars(s.downloads, "Скачивания с сайта по дням")}
<div class="grid2" style="margin-top:12px">
${table(s.versions, "version", "Версии за 7 дней")}
${table(s.arches, "arch", "Архитектура за 7 дней")}
</div>
</main></body></html>`;
}
