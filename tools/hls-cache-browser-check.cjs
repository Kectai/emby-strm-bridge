// Actual AMD loader + video playback with stale host module IDs already cached.
// All scripts/media are local fixtures; this never connects to a running Emby.
const fs = require('node:fs'), path = require('node:path'), http = require('node:http'), assert = require('node:assert/strict');
const { webkit } = require('playwright');
const [exportsDir, fixtureDir, webRoot] = process.argv.slice(2).map(p => path.resolve(p));
if (!webRoot) throw Error('Usage: <HostCheck-output> <synthetic-HLS-fixture> <dashboard-ui>');
let version; const requests = [], sockets = new Set();
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost'); requests.push(url.pathname);
  if (url.pathname === '/web/index.html') { res.setHeader('Content-Type', 'text/html'); return res.end('<video muted playsinline></video><script src="/loader.js"></script>'); }
  let file;
  if (url.pathname === '/loader.js') file = path.join(webRoot, 'modules/alameda/alameda.js');
  else if (url.pathname === '/web/modules/strmbridge/basehtmlplayer-hls-v1.js') file = path.join(exportsDir, 'base-' + version + '.js');
  else if (url.pathname === '/web/modules/strmbridge/hls-seek-v1.js') file = path.join(exportsDir, 'hls-' + version + '.js');
  else if (/^\/media\/[a-zA-Z0-9_.-]+$/.test(url.pathname)) file = path.join(fixtureDir, path.basename(url.pathname));
  if (!file || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
  res.setHeader('Content-Type', ({ '.js': 'application/javascript', '.m3u8': 'application/vnd.apple.mpegurl', '.ts': 'video/mp2t' })[path.extname(file)] || 'application/octet-stream');
  res.end(fs.readFileSync(file));
});
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await webkit.launch({ headless: true });
    for (version of ['4.9.5.0', '4.10.0.40']) {
      const page = await browser.newPage(); requests.length = 0;
      try {
        await page.goto('http://127.0.0.1:' + server.address().port + '/web/index.html');
        await page.evaluate(async () => {
          require.config({ baseUrl: '/web' });
          define('modules/htmlvideoplayer/basehtmlplayer.js', [], () => ({ stale: true }));
          define('modules/hlsjs/hls.js', [], () => ({ stale: true }));
          for (const id of ['emby-apiclient/connectionmanager', 'browser', 'common/appsettings', 'emby-apiclient/events', 'htmlvideoplayer/htmlmediahelper', 'approuter'])
            define('modules/' + id + '.js', [], () => ({ default: { trigger() {} } }));
          const load = id => new Promise((resolve, reject) => require([id], result => resolve(result.default || result), reject));
          window.Emby = { importModule: id => load(id.replace(/^\.\//, '')) };
          if (!(await load('modules/hlsjs/hls.js')).stale || !(await load('modules/htmlvideoplayer/basehtmlplayer.js')).stale) throw Error('stale cache not populated');
          const Base = await load('modules/strmbridge/basehtmlplayer-hls-v1.js');
          window.instance = new Base();
          await instance.setSrcWithHlsJs(document.querySelector('video'), { mediaType: 'Video' }, '/media/master.m3u8', new AbortController().signal);
        });
        await page.waitForFunction(() => document.querySelector('video').currentTime > 1, null, { timeout: 15000 });
        assert.equal(await page.evaluate(() => instance._hlsPlayer.config.preferManagedMediaSource), false);
        await page.evaluate(() => { document.querySelector('video').currentTime = 22; });
        await page.waitForFunction(() => { const v = document.querySelector('video'); return !v.seeking && v.currentTime > 22.3; }, null, { timeout: 15000 });
        assert.ok(requests.includes('/web/modules/strmbridge/basehtmlplayer-hls-v1.js'));
        assert.ok(requests.includes('/web/modules/strmbridge/hls-seek-v1.js'));
        assert.equal(requests.some(x => x === '/web/modules/hlsjs/hls.js'), false);
        console.log('PASS ' + version + ': actual AMD cache bypass, patched HLS startup and WebKit video seek');
      } finally { await page.close(); }
    }
  } finally { await browser?.close(); for (const socket of sockets) socket.destroy(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
