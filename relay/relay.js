// Relay Kontrol Lab: perantara antara KontrolLab-Admin.exe dan agen di tiap PC lab.
// Admin dan PC lab hanya terhubung KELUAR ke relay ini, jadi keduanya boleh berada di balik NAT.
// Relay tidak bisa membuat perintah sendiri: tiap perintah ditandatangani kunci RSA milik Admin,
// dan agen di PC lab menolak perintah yang tanda tangannya tidak cocok.
// Tanpa dependensi; cukup Node.js 18+. Konfigurasi: config.json di folder yang sama.
'use strict';
const http = require('http');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const config = JSON.parse(fs.readFileSync(process.env.RELAY_CONFIG || path.join(__dirname, 'config.json'), 'utf8'));
const PORT = Number(config.port || 8790);
const HOST = config.host || '0.0.0.0';
const POLL_WAIT_MS = 25000;
const COMMAND_TTL_MS = 120000;   // perintah hanya dikirim ke PC yang menghubungi relay dalam 2 menit
const MAX_PC = 500;
const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9-]{0,39}$/;
const ACTIONS = new Set(['exambro-jalankan', 'exambro-tutup', 'restart', 'matikan', 'batal-matikan', 'kirim-file']);
// File kiriman Admin disimpan per SHA-256. Agen memeriksa hash yang ikut ditandatangani Admin,
// jadi relay tidak bisa mengganti isi file.
const FILE_DIR = path.join(config.dataDir || process.env.STATE_DIRECTORY || path.join(__dirname, 'data'), 'file');
const MAX_FILE_BYTES = Number(config.maxFileMB || 1024) * 1024 * 1024;
const KEEP_FILES = 5;
const SHA_RE = /^[a-f0-9]{64}$/;
fs.mkdirSync(FILE_DIR, { recursive: true });

const pcs = new Map();       // nama (huruf besar) -> data PC
const commands = [];         // perintah terbaru, maksimal 50
const waiting = new Map();   // nama -> fungsi pembangun long-poll yang sedang menunggu

function tokenOk(given, expected) {
  if (typeof given !== 'string' || typeof expected !== 'string' || !expected) return false;
  const a = crypto.createHash('sha256').update(given).digest();
  const b = crypto.createHash('sha256').update(expected).digest();
  return crypto.timingSafeEqual(a, b);
}

function send(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Length': Buffer.byteLength(body) });
  res.end(body);
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    let size = 0; const parts = [];
    req.on('data', c => { size += c.length; if (size > 32768) { reject(new Error('terlalu besar')); req.destroy(); } else parts.push(c); });
    req.on('end', () => { try { resolve(parts.length ? JSON.parse(Buffer.concat(parts).toString('utf8')) : {}); } catch (e) { reject(e); } });
    req.on('error', reject);
  });
}

function clientIp(req) {
  return (req.headers['x-forwarded-for'] || '').split(',')[0].trim() || req.socket.remoteAddress || '';
}

function targets(cmd, name) {
  return cmd.target.includes('*') || cmd.target.includes(name);
}

function pendingFor(name, now) {
  return commands.filter(c => now - c.received < COMMAND_TTL_MS && !c.delivered.has(name) && targets(c, name));
}

function deliver(name, now) {
  const list = pendingFor(name, now);
  for (const c of list) c.delivered.add(name);
  return list.map(c => ({ id: c.id, aksi: c.aksi, target: c.targetAsli, ts: c.ts, data: c.data, sig: c.sig }));
}

async function handleAgent(req, res) {
  if (!tokenOk(req.headers['x-token'], config.clientToken)) return send(res, 401, { error: 'token PC lab salah' });
  const body = await readJson(req);
  const name = String(body.name || '').toUpperCase();
  if (!NAME_RE.test(name)) return send(res, 400, { error: 'nama PC tidak sah' });
  if (!pcs.has(name) && pcs.size >= MAX_PC) return send(res, 429, { error: 'terlalu banyak PC' });
  const now = Date.now();
  const pc = pcs.get(name) || { name, hasil: {} };
  pc.lastSeen = now;
  pc.ip = clientIp(req);
  pc.user = String(body.user || '').slice(0, 60);
  pc.exambro = !!body.exambro;
  pc.versi = String(body.versi || '').slice(0, 20);
  pc.catatan = String(body.catatan || '').slice(0, 200);
  for (const h of Array.isArray(body.hasil) ? body.hasil.slice(0, 20) : []) {
    if (typeof h.id === 'string' && h.id.length <= 64) pc.hasil[h.id] = { ok: !!h.ok, pesan: String(h.pesan || '').slice(0, 300), waktu: now };
  }
  const known = new Set(commands.map(c => c.id));
  for (const id of Object.keys(pc.hasil)) if (!known.has(id)) delete pc.hasil[id];
  pcs.set(name, pc);

  const ready = deliver(name, now);
  if (ready.length || body.tunggu === false) return send(res, 200, { now, perintah: ready });

  // Long-poll: tahan sampai ada perintah atau 25 detik lewat, supaya semua PC menerima perintah hampir bersamaan.
  const previous = waiting.get(name);
  if (previous) previous(true);
  let done = false;
  const finish = (replaced) => {
    if (done) return; done = true;
    clearTimeout(timer);
    if (waiting.get(name) === finish) waiting.delete(name);
    const t = Date.now();
    send(res, 200, { now: t, perintah: replaced ? [] : deliver(name, t) });
  };
  const timer = setTimeout(() => finish(false), POLL_WAIT_MS);
  req.on('close', () => { if (!done) { done = true; clearTimeout(timer); if (waiting.get(name) === finish) waiting.delete(name); } });
  waiting.set(name, finish);
}

async function handleCommand(req, res) {
  if (!tokenOk(req.headers['x-token'], config.adminToken)) return send(res, 401, { error: 'token admin salah' });
  const b = await readJson(req);
  const target = Array.isArray(b.target) ? b.target.map(x => String(x).toUpperCase()) : [];
  if (typeof b.id !== 'string' || !/^[A-Za-z0-9-]{8,64}$/.test(b.id)) return send(res, 400, { error: 'id tidak sah' });
  if (!ACTIONS.has(b.aksi)) return send(res, 400, { error: 'aksi tidak dikenal' });
  if (!target.length || target.length > MAX_PC || !target.every(t => t === '*' || NAME_RE.test(t))) return send(res, 400, { error: 'target tidak sah' });
  if (typeof b.sig !== 'string' || b.sig.length > 1024 || !Number.isFinite(b.ts)) return send(res, 400, { error: 'tanda tangan tidak ada' });
  if (commands.some(c => c.id === b.id)) return send(res, 409, { error: 'id sudah dipakai' });
  const data = typeof b.data === 'string' ? b.data : '';
  if (data.length > 400) return send(res, 400, { error: 'data terlalu panjang' });
  if (b.aksi === 'kirim-file') {
    const sha = data.split('|')[1] || '';
    if (!SHA_RE.test(sha) || !fs.existsSync(path.join(FILE_DIR, sha))) return send(res, 400, { error: 'file belum diunggah ke relay' });
  }
  const now = Date.now();
  // target: huruf besar untuk pencocokan; targetAsli: persis seperti yang ditandatangani Admin.
  commands.push({ id: b.id, aksi: b.aksi, target, targetAsli: b.target.map(String), ts: b.ts, data, sig: b.sig, received: now, delivered: new Set() });
  while (commands.length > 50) commands.shift();
  let woke = 0;
  for (const [name, finish] of [...waiting]) if (target.includes('*') || target.includes(name)) { finish(false); woke++; }
  send(res, 200, { ok: true, langsungTerkirim: woke });
}

function handleStatus(req, res) {
  if (!tokenOk(req.headers['x-token'], config.adminToken)) return send(res, 401, { error: 'token admin salah' });
  const now = Date.now();
  send(res, 200, {
    now,
    pc: [...pcs.values()].sort((a, b) => a.name.localeCompare(b.name, 'id', { numeric: true })).map(p => ({
      name: p.name, ip: p.ip, user: p.user, exambro: p.exambro, versi: p.versi, catatan: p.catatan,
      detikLalu: Math.round((now - p.lastSeen) / 1000), online: waiting.has(p.name) || now - p.lastSeen < 40000, hasil: p.hasil,
    })),
    perintah: commands.slice(-10).map(c => ({ id: c.id, aksi: c.aksi, target: c.target, diterima: c.received, terkirim: [...c.delivered] })),
  });
}

function handleForget(req, res, url) {
  if (!tokenOk(req.headers['x-token'], config.adminToken)) return send(res, 401, { error: 'token admin salah' });
  const name = String(url.searchParams.get('name') || '').toUpperCase();
  send(res, 200, { ok: pcs.delete(name) });
}

// Unggah dari Admin: badan permintaan = isi file mentah. Ditulis ke file sementara sambil dihitung SHA-256-nya.
function handleUpload(req, res) {
  if (!tokenOk(req.headers['x-token'], config.adminToken)) { req.resume(); return send(res, 401, { error: 'token admin salah' }); }
  const declared = Number(req.headers['content-length'] || 0);
  if (declared > MAX_FILE_BYTES) { req.resume(); return send(res, 413, { error: 'file terlalu besar (maks ' + Math.round(MAX_FILE_BYTES / 1048576) + ' MB)' }); }
  const tmp = path.join(FILE_DIR, '.unggah-' + crypto.randomBytes(8).toString('hex'));
  const out = fs.createWriteStream(tmp);
  const hash = crypto.createHash('sha256');
  let size = 0, failed = false;
  const fail = (code, msg) => { if (failed) return; failed = true; out.destroy(); fs.rm(tmp, { force: true }, () => {}); if (!res.headersSent) send(res, code, { error: msg }); };
  req.on('data', chunk => {
    size += chunk.length;
    if (size > MAX_FILE_BYTES) { fail(413, 'file terlalu besar'); req.destroy(); return; }
    hash.update(chunk);
    if (!out.write(chunk)) { req.pause(); out.once('drain', () => req.resume()); }
  });
  req.on('aborted', () => fail(400, 'unggahan terputus'));
  req.on('error', () => fail(400, 'unggahan terputus'));
  out.on('error', () => fail(500, 'gagal menyimpan file di relay'));
  req.on('end', () => {
    if (failed) return;
    out.end(() => {
      if (failed) return;
      const sha = hash.digest('hex');
      fs.rename(tmp, path.join(FILE_DIR, sha), err => {
        if (err) return fail(500, 'gagal menyimpan file di relay');
        pruneFiles(sha);
        send(res, 200, { sha256: sha, size });
      });
    });
  });
}

// Simpan hanya beberapa file terbaru supaya disk server tidak penuh.
function pruneFiles(keep) {
  try {
    const files = fs.readdirSync(FILE_DIR).filter(f => SHA_RE.test(f))
      .map(f => ({ f, t: fs.statSync(path.join(FILE_DIR, f)).mtimeMs })).sort((a, b) => b.t - a.t);
    const now = new Date();
    fs.utimesSync(path.join(FILE_DIR, keep), now, now);
    for (const x of files.filter(x => x.f !== keep).slice(KEEP_FILES - 1)) fs.rmSync(path.join(FILE_DIR, x.f), { force: true });
  } catch (e) { /* pembersihan tidak boleh menggagalkan unggahan */ }
}

function handleDownload(req, res, sha) {
  if (!tokenOk(req.headers['x-token'], config.clientToken)) return send(res, 401, { error: 'token PC lab salah' });
  const file = path.join(FILE_DIR, sha);
  fs.stat(file, (err, st) => {
    if (err) return send(res, 404, { error: 'file sudah tidak ada di relay' });
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': st.size, 'Cache-Control': 'no-store' });
    fs.createReadStream(file).pipe(res);
  });
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://relay');
    const p = url.pathname.replace(/^\/kontrol-lab/, '');
    if (req.method === 'POST' && p === '/api/admin/file') return handleUpload(req, res);
    const dl = /^\/api\/agen\/file\/([a-f0-9]{64})$/.exec(p);
    if (req.method === 'GET' && dl) return handleDownload(req, res, dl[1]);
    if (req.method === 'POST' && p === '/api/agen') return await handleAgent(req, res);
    if (req.method === 'POST' && p === '/api/admin/perintah') return await handleCommand(req, res);
    if (req.method === 'GET' && p === '/api/admin/status') return handleStatus(req, res);
    if (req.method === 'POST' && p === '/api/admin/lupakan') return handleForget(req, res, url);
    if (req.method === 'GET' && (p === '/' || p === '/api/ping')) return send(res, 200, { relay: 'kontrol-lab', ok: true });
    send(res, 404, { error: 'tidak ada' });
  } catch (e) {
    if (!res.headersSent) send(res, 400, { error: 'permintaan rusak' });
  }
});
server.requestTimeout = 60 * 60 * 1000;   // unggahan file besar lewat internet bisa lama
server.headersTimeout = 20000;
server.listen(PORT, HOST, () => console.log(`Relay Kontrol Lab mendengar di ${HOST}:${PORT}`));
