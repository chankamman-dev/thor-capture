// Builds an offline demo data root for the Cocoon backend so the native game library can be
// developed and screenshotted without a device: a small cocoon_db with made-up titles and
// abstract generated artwork placed where the backend's art cache expects it.
//   node tools/cocoon-demo.cjs <absolute data root>
// Refuses to touch a data root that already has a library.
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { DatabaseSync } = require('node:sqlite');

const root = process.argv[2];
if (!root || !path.isAbsolute(root)) { console.error('usage: node cocoon-demo.cjs <absolute data root>'); process.exit(2); }
const data = path.join(root, '_data'), cache = path.join(root, '_cache');
if (fs.existsSync(path.join(data, 'cocoon_db'))) { console.error('data root already has a library; not overwriting'); process.exit(3); }
fs.mkdirSync(data, { recursive: true }); fs.mkdirSync(cache, { recursive: true });

// --- minimal PNG writer -------------------------------------------------------------
const crcTable = new Int32Array(256).map((_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c; });
const crc = buf => { let c = -1; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ -1) >>> 0; };
function chunk(type, body) {
  const head = Buffer.alloc(8); head.writeUInt32BE(body.length, 0); head.write(type, 4, 'ascii');
  const tail = Buffer.alloc(4); tail.writeUInt32BE(crc(Buffer.concat([head.subarray(4), body])), 0);
  return Buffer.concat([head, body, tail]);
}
function png(w, h, pixel) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0;
    for (let x = 0; x < w; x++) { const p = pixel(x, y); raw.set(p, y * (w * 4 + 1) + 1 + x * 4); }
  }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
const mix = (a, b, t) => a.map((v, i) => Math.round(v + (b[i] - v) * t));
function hsl(h, s, l) {
  const k = n => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  const f = n => l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1));
  return [f(0), f(8), f(4)].map(v => Math.round(v * 255));
}
// Abstract art: a two-tone gradient with soft circles; logos are a rounded bar with a dot.
function cover(seed, w, h) {
  const a = hsl(seed * 47 % 360, 0.45, 0.55), b = hsl((seed * 47 + 60) % 360, 0.5, 0.28);
  const circles = [0, 1, 2].map(i => ({ x: ((seed * (i + 3) * 37) % 100) / 100 * w, y: ((seed * (i + 5) * 53) % 100) / 100 * h, r: (0.18 + 0.1 * i) * Math.min(w, h) }));
  return png(w, h, (x, y) => {
    let c = mix(a, b, (x / w + y / h) / 2);
    for (const k of circles) { const d = Math.hypot(x - k.x, y - k.y) / k.r; if (d < 1) c = mix(c, [255, 255, 255], 0.14 * (1 - d * d)); }
    return [...c, 255];
  });
}
function logo(seed) {
  const w = 480, h = 160, c = hsl(seed * 47 % 360, 0.35, 0.92);
  return png(w, h, (x, y) => {
    const bar = x > 70 && x < 440 && Math.abs(y - 80) < 26 ? 1 : 0;
    const dot = Math.hypot(x - 40, y - 80) < 30 ? 1 : 0;
    return [...c, (bar || dot) ? 235 : 0];
  });
}

const titles = [
  ['星辰旅人', 'switch'], ['Moss Harbor', 'switch'], ['雾港来信', 'switch'], ['Lantern Keep', 'switch'], ['纸鸢远航', 'switch'],
  ['Tidewalker', 'nds'], ['青瓦小镇', 'nds'], ['Pocket Orbit', 'nds'], ['回声森林', '3ds'], ['Brass Garden', '3ds'],
  ['Quiet Relay', 'gba'], ['像素农场日记', 'gba'], ['Night Ferry', 'gba'], ['北境灯塔', 'psp'], ['Paper Comet', 'psp'],
  ['Copper Wings', 'ps2'], ['长夜列车', 'ps2'], ['Sunday Arcade', 'snes'], ['云端邮差', 'snes'], ['Hollow Bell', 'snes'],
  ['River Atlas', 'switch'], ['折纸骑士', 'nds'], ['Glass Tundra', 'psp'], ['晚风电台', 'gba'],
];
const exts = { switch: 'nsp', nds: 'nds', '3ds': '3ds', gba: 'gba', psp: 'iso', ps2: 'iso', snes: 'sfc' };
const genres = ['RPG', 'Puzzle', 'Adventure', 'Platformer', 'Simulation', 'Action'];
const devs = ['Atlas Studio', 'Moon Works', 'Little Lantern', 'Harbor Nine', 'Paper Crane'];

const db = new DatabaseSync(path.join(data, 'cocoon_db'));
db.exec(`CREATE TABLE games (
  id INTEGER PRIMARY KEY, uri TEXT, fileSize INTEGER, displayName TEXT, title TEXT, fileName TEXT, platformId TEXT, isHidden INTEGER DEFAULT 0,
  boxArtLocal TEXT, logoLocal TEXT, heroLocal TEXT, boxArtSource TEXT, logoSource TEXT, heroSource TEXT,
  videoUrl TEXT, launchboxVideoUrl TEXT, isFavorite INTEGER DEFAULT 0, summary TEXT, developer TEXT, publisher TEXT,
  releaseDate INTEGER, rating REAL, genres TEXT, players TEXT, region TEXT, logoPlacementOverridden INTEGER DEFAULT 0, logoAnchor INTEGER,
  logoScaleX REAL, logoScaleY REAL, sortOrder INTEGER, metadataLocks TEXT,
  hashCrc32 TEXT, hashMd5 TEXT, hashSha1 TEXT, screenScraperId TEXT, retroAchievementsId INTEGER, retroAchievementsHash TEXT,
  steamGridDbId INTEGER, launchboxDatabaseId INTEGER, igdbId INTEGER)`);
const add = db.prepare(`INSERT INTO games (id,uri,fileSize,displayName,title,fileName,platformId,isFavorite,boxArtLocal,logoLocal,heroLocal,
  boxArtSource,logoSource,heroSource,summary,developer,publisher,releaseDate,rating,genres,players,region,metadataLocks)
  VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`);
const art = (devPath, bytes) => fs.writeFileSync(path.join(cache, Buffer.from(devPath).toString('base64url').slice(-100)), bytes);
titles.forEach(([name, platform], i) => {
  const id = i + 1, file = `${name.replace(/\s+/g, '_')}.${exts[platform]}`;
  // A few games lack artwork so the library shows its "missing" states.
  const hasCover = i % 7 !== 6, hasLogo = i % 5 !== 4, hasHero = i % 4 !== 3;
  const base = `/demo/media/${platform}/${id}`;
  if (hasCover) art(`${base}/icon.png`, cover(id, 360, 360));
  if (hasLogo) art(`${base}/logo.png`, logo(id));
  if (hasHero) art(`${base}/hero.png`, cover(id + 11, 960, 540));
  add.run(id, `/demo/roms/${platform}/${file}`, 1048576 * (8 + id), name, name, file, platform, i % 6 === 0 ? 1 : 0,
    hasCover ? `${base}/icon.png` : null, hasLogo ? `${base}/logo.png` : null, hasHero ? `${base}/hero.png` : null,
    hasCover ? 'demo' : null, hasLogo ? 'demo' : null, hasHero ? 'demo' : null,
    `演示条目：一段虚构的简介，用于检查游戏详情的排版。这里描述了${name}的世界、玩法和氛围。`,
    devs[i % devs.length], devs[(i + 2) % devs.length], Date.UTC(2004 + (i % 18), i % 12, 1 + i) / 1000,
    3 + (i % 5) * 0.4, genres[i % genres.length], i % 3 === 0 ? '1-2' : '1', ['JP', 'US', 'EU', 'CN'][i % 4], '{}');
});
db.close();
console.log(`demo library: ${titles.length} games in ${root}`);
