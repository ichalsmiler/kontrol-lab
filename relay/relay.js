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
const ACTIONS = new Set(['exambro-jalankan', 'exambro-tutup', 'restart', 'matikan', 'batal-matikan']);

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
  return list.map(c => ({ id: c.id, aksi: c.aksi, target: c.targetAsli, ts: c.ts, sig: c.sig }));
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
  const now = Date.now();
  // target: huruf besar untuk pencocokan; targetAsli: persis seperti yang ditandatangani Admin.
  commands.push({ id: b.id, aksi: b.aksi, target, targetAsli: b.target.map(String), ts: b.ts, sig: b.sig, received: now, delivered: new Set() });
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

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://relay');
    const p = url.pathname.replace(/^\/kontrol-lab/, '');
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
server.requestTimeout = 60000;
server.headersTimeout = 20000;
server.listen(PORT, HOST, () => console.log(`Relay Kontrol Lab mendengar di ${HOST}:${PORT}`));
