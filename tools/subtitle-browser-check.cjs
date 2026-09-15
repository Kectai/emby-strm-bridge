// Isolated, headless local-fixture check. Never connects to a user's Emby server.
const { chromium, webkit, firefox } = require('playwright');
const http = require('node:http'), fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const [fixture, web49, web410] = process.argv.slice(2).map(p => path.resolve(p));
if (!fixture || !web49 || !web410) throw Error('Usage: subtitle-browser-check.cjs <fixture> <web49> <web410>');
const player = fs.readFileSync('src/Emby.StrmBridge/Subtitles/Player.js');
const sockets = new Set(), results = [];
let webRoot, posts = [], streamCount = 0, streamOffsets = [], coldServed = false;
const cold = process.env.SUBTITLE_TEST_COLD === '1';
// Shared subtitles require the observable hls.js clock. Native-only mode tests exclusion.
const forceMse = process.env.SUBTITLE_TEST_NATIVE !== '1';
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (url.pathname === '/favicon.ico') { res.writeHead(204).end(); return; }
  if (req.method === 'POST') {
    let body = ''; for await (const chunk of req) body += chunk;
    posts.push(JSON.parse(body)); res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ SessionId: 'fixture', WindowSeconds: 60 })); return;
  }
  if (req.method === 'DELETE') { res.writeHead(204).end(); return; }
  if (url.pathname.endsWith('/Sessions/fixture/Stream')) {
    streamCount++; streamOffsets.push(url.searchParams.get('MseTimestampOffsetTicks')); res.setHeader('Content-Type', 'text/x-ssa; charset=utf-8');
    let text = fs.readFileSync(path.join(fixture, 'shared.ass'), 'utf8');
    if (cold) {
      // Model SharedSubtitleTimeline: TS mux delay is 1.4 s. A cold output
      // starts with source keyframe 16 s but playlist segment 3 claims 18 s.
      const offset = url.searchParams.get('MseTimestampOffsetTicks');
      assert.notEqual(offset, null, 'Subtitle reads require the current MSE clock.');
      const shift = 1.4 + Number(offset) / 1e7;
      text = text.replace(/(Dialogue:\s*[^,]*,)(\d+:\d+:\d+\.\d+),(\d+:\d+:\d+\.\d+),/g, (_, prefix, start, end) => {
        const stamp = v => { const p=v.split(':').map(Number); const c=Math.max(0, Math.round((p[0]*3600+p[1]*60+p[2]+shift)*100)); return Math.floor(c/360000)+':'+String(Math.floor(c/6000)%60).padStart(2,'0')+':'+String(Math.floor(c/100)%60).padStart(2,'0')+'.'+String(c%100).padStart(2,'0'); };
        return prefix+stamp(start)+','+stamp(end)+',';
      });
    }
    res.write(text); return; // Keep EOF pending.
  }
  if (url.pathname === '/web/index.html') {
    res.setHeader('Content-Type', 'text/html');
    res.end('<html><body style="margin:0;background:black"><div style="position:relative;width:640px;height:360px"><video style="width:640px;height:360px" width="640" height="360" muted playsinline></video></div></body></html>'); return;
  }
  if (url.pathname === '/adapter.js') { res.setHeader('Content-Type', 'application/javascript'); res.end(player); return; }
  let base, relative;
  if (url.pathname.startsWith('/media/')) { base = fixture; relative = url.pathname.slice(7); }
  else if (url.pathname === '/hls.js') { base = webRoot; relative = 'modules/hlsjs/hls.js'; }
  else { base = webRoot; relative = url.pathname.replace(/^\/web\//, ''); }
  if (cold && relative === 'segment3.ts') { relative = 'cold.ts'; coldServed = true; }
  const file = path.resolve(base, relative);
  if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404).end(); return; }
  res.setHeader('Content-Type', ({ '.js': 'application/javascript', '.wasm': 'application/wasm', '.woff2': 'font/woff2', '.m3u8': 'application/vnd.apple.mpegurl', '.ts': 'video/mp2t' })[path.extname(file)] || 'application/octet-stream');
  if (process.env.SUBTITLE_CLOCK_TRACE && file.endsWith('subtitles-octopus-worker.js')) {
    res.end(fs.readFileSync(file, 'utf8') + ';self.addEventListener("message",function(e){if(e.data.target==="video"&&self.getCurrentTime)postMessage({target:"clock-proof",value:self.getCurrentTime(),paused:self._isPaused,rate:self.rate,at:Date.now()});});'); return;
  }
  res.end(fs.readFileSync(file));
});
server.on('connection', s => { sockets.add(s); s.on('close', () => sockets.delete(s)); });
async function visible(page) {
  await page.waitForFunction(() => {
    const canvas = document.querySelector('canvas');
    if (!canvas?.width || !canvas.height) return false;
    const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let pixels = 0; for (let i = 3; i < data.length; i += 4) if (data[i]) pixels++;
    return pixels > 100;
  }, null, { timeout: 15000 });
}
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  try {
    for (const [engine, type] of [['chromium', chromium], ['webkit', webkit], ['firefox', firefox]]) {
      if (cold && engine === 'webkit' && !forceMse) continue;
      if (!forceMse && engine !== 'webkit') continue;
      if (process.env.SUBTITLE_TEST_ENGINE && process.env.SUBTITLE_TEST_ENGINE !== engine) continue;
      const browser = await type.launch({ headless: true, ...(engine === 'chromium' && process.env.CHROME_EXECUTABLE ? { executablePath: process.env.CHROME_EXECUTABLE } : {}) });
      try {
        for (const [version, root] of [['4.9', web49], ['4.10', web410]]) {
          webRoot = root; posts = []; streamCount = 0; streamOffsets = []; coldServed = false;
          const page = await browser.newPage({ viewport: { width: 640, height: 360 } });
          const errors = []; page.on('pageerror', e => errors.push(e.message));
          page.on('console', m => { if (m.type() === 'error' || m.text().startsWith('STRM_BRIDGE')) console.log(engine, version, m.text()); });
          try {
            await page.goto(base + '/web/index.html');
            await Promise.race([page.evaluate(async native => {
              window.workerEvidence=[];
              const NativeWorker=window.Worker;
              window.Worker=class extends NativeWorker { constructor(...args){super(...args);this.proof={frames:0};window.workerEvidence.push(this.proof);this.addEventListener('message',e=>{if(e.data.target==='clock-proof'){e.stopImmediatePropagation();this.proof.workerClocks??=[];this.proof.workerClocks.push({videoTime:document.querySelector('video').currentTime,...e.data});return;}this.proof.received=e.data.target;if(e.data.target==='canvas'){this.proof.frames++;this.proof.operation=e.data.op;this.proof.canvases=e.data.canvases?.length;this.proof.renders??=[];this.proof.renders.push({videoTime:document.querySelector('video').currentTime,time:e.data.time,spentTime:e.data.spentTime,blendTime:e.data.blendTime,bitmaps:e.data.bitmaps?.length,canvases:e.data.canvases?.length});}});} postMessage(msg,...rest){if(msg.target==='video'){this.proof.clock=msg;this.proof.clocks??=[];if(this.proof.clocks.length<400)this.proof.clocks.push({currentTime:msg.currentTime,isPaused:msg.isPaused,at:performance.now()});}if(msg.target==='set-track')this.proof.events=(msg.content.match(/Dialogue:/g)||[]).length;return super.postMessage(msg,...rest);} };
              window.Emby = { importModule: async url => {
                const src = await (await fetch(url)).text(); let module;
                const define = (deps, make) => { const exports = {}; module = make(...deps.map(d => d === 'exports' ? exports : undefined)) || exports.default || exports; };
                new Function('define', src)(define); return module;
              } };
              const video = document.querySelector('video'), url = location.origin + '/media/master.m3u8?PlaySessionId=fixture-video';
              window.mediaEvidence=[]; ['seeking','seeked','playing','waiting'].forEach(name=>video.addEventListener(name,()=>mediaEvidence.push({name,time:video.currentTime,at:performance.now()})));
              window.instance = { _strmBridgeEpoch: 1, _currentPlayOptions: { url } };
              if (native) {
                if (!video.canPlayType('application/vnd.apple.mpegurl')) throw Error('native HLS unavailable');
                video.src = url;
              } else {
                const Hls = await Emby.importModule('/hls.js');
                instance._hlsPlayer = new Hls({ maxBufferLength: 6, maxMaxBufferLength: 6 }); instance._hlsPlayer.attachMedia(video); instance._hlsPlayer.loadSource(url);
              }
              await video.play();
              const adapter = await Emby.importModule('/adapter.js');
              window.subtitleOptions = { instance, video, epoch: 1, item: { Id: 'fixture' }, source: { Id: 'fixture', MediaStreams: [] },
                track: { Index: 2 }, api: { getUrl: p => location.origin + '/' + p, accessToken: () => 'fixture-only' }, fallback: () => { window.nativeFallbacks = (window.nativeFallbacks || 0) + 1; } };
              window.adapter = adapter;
              await adapter.render(subtitleOptions);
            }, engine === 'webkit' && !forceMse), new Promise((_, reject) => { const timer = setTimeout(() => reject(Error('video startup timeout')), 20000); timer.unref(); })]);
            if (!forceMse) {
              assert.equal(await page.evaluate(() => window.nativeFallbacks), 1);
              assert.equal(posts.length, 0); assert.equal(streamCount, 0);
              assert.equal(await page.locator('canvas').count(), 0);
              assert.ok(await page.evaluate(() => !document.querySelector('video').paused));
              assert.deepEqual(errors, []);
              results.push({ engine, version, nativeExcluded: true, videoUnchanged: true });
              console.log('PASS native HLS exclusion / Emby ' + version + ': video continues, no subtitle session or guessed clock');
              continue;
            }
            await visible(page);
            assert.equal(await page.evaluate(() => window.nativeFallbacks || 0), 0);
            assert.equal(posts[0].PlaySessionId, 'fixture-video');
            assert.equal(posts[0].NativeHlsClock, engine === 'webkit' && !forceMse);
            assert.ok(streamCount > 0);
            const initial = await page.evaluate(() => ({ time: document.querySelector('video').currentTime, status: !!document.querySelector('[role=status]') }));
            assert.equal(initial.status, false);
            await page.evaluate(() => { const v = document.querySelector('video'); v.currentTime = 22.1; });
            await page.waitForFunction(() => document.querySelector('video').currentTime > 22 && !document.querySelector('video').seeking);
            await visible(page);
            if (cold) assert.ok(await page.evaluate(() => document.querySelector('video').currentTime) < 22.7, 'Cold-seek cue must match video at 22 s, not appear seconds later.');
            // Repeat forward/backward seeks in one MSE session, so a new
            // server segment/keyframe cannot masquerade as a new initPTS.
            for (const position of [4.1, 24.1, 3.1, 22.1, 4.1, 24.1, 3.1, 22.1, 4.1, 24.1, 3.1, 22.1]) {
              await page.evaluate(position => { document.querySelector('video').currentTime = position; }, position);
              await page.waitForFunction(position => Math.abs(document.querySelector('video').currentTime-position) < 1 && !document.querySelector('video').seeking, position);
              await visible(page);
            }
            if (engine !== 'webkit' || forceMse) {
              assert.ok(streamOffsets.length >= 3);
              assert.ok(streamOffsets.every(v => v !== null && v === streamOffsets[0]), 'MSE origin must be stable across seeks within one continuity.');
              if (cold) assert.ok(coldServed, 'Must exercise a restarted output retaining an earlier keyframe.');
            }
            // Late selection uses the same player and media input, including after seek.
            await page.evaluate(async () => { const v=document.querySelector('video');const state={time:v.currentTime,ready:v.readyState,seeking:v.seeking,paused:v.paused}; instance.strmBridgeSubtitle.dispose(); await adapter.render(subtitleOptions); return state; });
            await new Promise(r=>setTimeout(r,100));
            await visible(page);
            assert.equal(await page.locator('[role=status]').count(), 0);
            assert.deepEqual(errors, []);
            results.push({ engine, version, firstVisibleTime: initial.time, seekAndReselect: true, silent: true, streamingBeforeEof: true, repeatedSeeks: true, coldKeyframe: cold, forcedMse: forceMse, mseOffsets: streamOffsets });
            console.log('PASS ' + engine + ' / Emby ' + version + ': HLS + real libass/WASM, first cue, seek, late selection, silent');
          } catch (error) {
            console.log('FAILED',engine,version,JSON.stringify(await page.evaluate(() => { const v=document.querySelector('video'),r=instance.currentSubtitlesOctopus,c=document.querySelector('canvas');return {time:v.currentTime,paused:v.paused,ready:v.readyState,canvas:c&&[c.width,c.height],workerActive:r?.workerActive,offset:r?.timeOffset,workers:window.workerEvidence,media:window.mediaEvidence}; })),errors,streamOffsets);
            await page.screenshot({path:path.join(fixture,'failed-'+engine+'-'+version+'.png')}); throw error;
          } finally { await page.evaluate(() => { window.instance?.strmBridgeSubtitle?.dispose(); window.instance?._hlsPlayer?.destroy(); }).catch(() => {}); await page.close(); }
        }
      } finally { await browser.close(); }
    }
  } finally { for (const socket of sockets) socket.destroy(); server.close(); fs.writeFileSync(path.join(fixture, 'browser-results'+(forceMse?'-mse':'-native-excluded')+(cold?'-cold':'')+(process.env.SUBTITLE_TEST_ENGINE?'-'+process.env.SUBTITLE_TEST_ENGINE:'')+'.json'), JSON.stringify(results, null, 2)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
