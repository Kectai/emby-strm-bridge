// Isolated external-ASS regression using shipped hls.js + Octopus and synthetic media.
// Cold-open the retained keyframe before playlist position 18; the native renderer
// follows browser time and is wrong. The adapter must follow source time instead.
const { chromium, webkit } = require('playwright');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http'), assert = require('node:assert/strict');
const [fixture, web49, web410, transformed] = process.argv.slice(2).map(p => path.resolve(p));
if (!transformed) throw Error('Usage: subtitle-external-browser-check.cjs <fixture> <web49> <web410> <transformed-directory>');
const adapter = fs.readFileSync('src/Emby.StrmBridge/Subtitles/Player.js');
let root, version, clocks = 0, subtitleReads = 0, coldReads = 0;
// Short colored cues with blank gaps prove which cue libass actually paints.
// Long cues only prove that something was painted, even with seconds of drift.
const cueColor = second => [(second * 37) % 192 + 64, (second * 71) % 192 + 64, (second * 113) % 192 + 64];
const assHeader = fs.readFileSync(path.join(fixture, 'source.ass'), 'utf8').split('Dialogue:')[0];
const preciseAss = assHeader + Array.from({ length: 60 }, (_, second) => {
  const stamp = '0:00:' + String(second).padStart(2, '0');
  const color = cueColor(second).reverse().map(n => n.toString(16).padStart(2, '0')).join('');
  return `Dialogue: 0,${stamp}.00,${stamp}.40,Default,,0,0,0,,{\\1c&H${color}&}Cue ${second}\n`;
}).join('');
const sockets = new Set(), results = [];
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (url.pathname === '/favicon.ico') return res.writeHead(204).end();
  if (url.pathname === '/web/index.html') return res.writeHead(200, { 'Content-Type': 'text/html' }).end('<div style="position:relative;width:640px;height:360px"><video width="640" height="360" muted playsinline></video></div>');
  if (url.pathname.endsWith('/subtitle-player.js')) return res.writeHead(200, { 'Content-Type': 'application/javascript' }).end(adapter);
  if (url.pathname === '/native.js') {
    const s = fs.readFileSync(path.join(transformed, 'web-' + version + '.js'), 'utf8');
    return res.writeHead(200, { 'Content-Type': 'text/plain' }).end(s.slice(s.indexOf('function renderWithSubtitlesOctopus('), s.indexOf('function renderAssSsa(')));
  }
  if (url.pathname.endsWith('/Clock')) {
    let text = ''; for await (const chunk of req) text += chunk;
    clocks++; const body = JSON.parse(text); assert.equal(body.PlaySessionId, 'fixture');
    // This fixture's mux delay was produced by FFmpeg; server-contract tests
    // independently verify real command parsing with multiple max_delay values.
    return res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ TimelineOffsetTicks: 14000000 + body.MseTimestampOffsetTicks }));
  }
  if (req.method !== 'GET') return res.writeHead(405).end();
  let base = root, relative = url.pathname.replace(/^\/web\//, '');
  if (url.pathname.startsWith('/media/')) { base = fixture; relative = url.pathname.slice(7); }
  if (relative === 'segment3.ts') { relative = 'cold.ts'; coldReads++; }
  if (relative === 'source.ass') { subtitleReads++; return res.writeHead(200, { 'Content-Type': 'text/x-ssa' }).end(preciseAss); }
  const file = path.resolve(base, relative);
  if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return res.writeHead(404).end();
  res.setHeader('Content-Type', ({ '.js': 'application/javascript', '.wasm': 'application/wasm', '.woff2': 'font/woff2', '.m3u8': 'application/vnd.apple.mpegurl', '.ts': 'video/mp2t', '.ass': 'text/x-ssa' })[path.extname(file)] || 'application/octet-stream');
  res.end(fs.readFileSync(file));
});
server.on('connection', s => { sockets.add(s); s.on('close', () => sockets.delete(s)); });
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    for (const [engine, type] of [['chromium', chromium], ['webkit', webkit]]) {
      const browser = await type.launch({ headless: true, ...(engine === 'chromium' && process.env.CHROME_EXECUTABLE ? { executablePath: process.env.CHROME_EXECUTABLE } : {}) });
      try {
        for ([version, root] of [['4.9.5.0', web49], ['4.10.0.40', web410]]) {
          clocks = subtitleReads = coldReads = 0;
          const page = await browser.newPage(); const errors = [];
          page.on('pageerror', e => errors.push(e.message));
          page.on('console', m => { if (m.type()==='error') console.log('CONSOLE',engine,version,m.text()); });
          await page.goto('http://127.0.0.1:' + server.address().port + '/web/index.html');
          try {
            await page.evaluate(async () => {
              const video = document.querySelector('video');
              window.Emby = { importModule: async url => {
                let module; const exports = {}; const src = await (await fetch(url)).text();
                new Function('define', src)((deps, make) => { module = make(...deps.map(d => d === 'exports' ? exports : undefined)) || exports.default || exports; });
                return module;
              } };
              const Hls = await Emby.importModule('./modules/hlsjs/hls.js');
              window.instance = { _strmBridgeEpoch: 1, boundOnVideoResize: () => {}, _currentPlayOptions: { url: location.origin + '/media/master.m3u8?PlaySessionId=fixture&SegmentContainer=ts' }, _hlsPlayer: new Hls({ startPosition: 18, maxBufferLength: 6, maxMaxBufferLength: 6 }) };
              instance._hlsPlayer.attachMedia(video); instance._hlsPlayer.loadSource(instance._currentPlayOptions.url);
              await video.play();
              const api = { getUrl: p => location.origin + '/' + p, accessToken: () => 'fixture' };
              const scope = {
                Emby, window, document, fetch, TextDecoderStream: window.TextDecoderStream, WritableStream: window.WritableStream,
                console, ResizeObserver, _browser: { default: {} }, _approuter: { default: { baseUrl: () => '/web' } },
                _connectionmanager: { default: { getApiClient: () => api } }, loadWebVTT: async () => {}, fetchSubtitleContent: async url => (await fetch(url)).text(),
                getTextTrackUrl: () => '/media/source.ass', enableChunkedResponse: () => false,
                getFallbackFontUrl: async () => '/web/modules/fonts/GoNotoKurrent.woff2', getAttachmentFonts: () => [], getAvailableFonts: () => ({}),
                resetVideoRendererSize: () => {}, ensureCustomSubtitlesElement: (i, v) => {
                  const parent = document.createElement('div'); parent.style.cssText='position:absolute;inset:0';
                  v.parentNode.appendChild(parent); i.videoSubtitlesElem = parent;
                }
              };
              const s = await (await fetch('/native.js')).text();
              const functions = new Function(...Object.keys(scope), s + ';return { native:renderWithSubtitlesOctopusNative, fixed:renderWithSubtitlesOctopus };')(...Object.values(scope));
              window.start = async fixed => {
                await functions[fixed ? 'fixed' : 'native'](instance, video, { Index: 2, IsExternal: true, Codec: 'ass' }, { Id: 'fixture' }, { Id: 'fixture', Container: 'mkv', Protocol: 'Http' });
                if (fixed) {
                  if(instance.currentSubtitlesOctopus.renderMode !== 'wasm-blend') throw Error('External clock needs ordered synchronous blending.');
                  const worker = instance.currentSubtitlesOctopus.worker, send = worker.postMessage.bind(worker);
                  worker.postMessage = (m, ...rest) => { if(m.target === 'video') window.lastClock = m; return send(m, ...rest); };
                  worker.addEventListener('message', e => { if(e.data.target === 'canvas') window.lastCanvas = {time:e.data.time, lastRender:instance.currentSubtitlesOctopus.lastRenderTime, count:e.data.canvases?.length}; });
                }
              };
              await start(false);
            });
            await page.waitForFunction(() => instance.currentSubtitlesOctopus?.workerActive && instance._hlsPlayer.streamController.fragPlaying);
            await page.waitForTimeout(200);
            const before = await page.evaluate(() => {
              const v=document.querySelector('video'),r=instance.currentSubtitlesOctopus,c=instance._hlsPlayer.streamController;
              const pts=c.initPTS[c.fragPlaying.cc]; return { origin:pts.baseTime/pts.timescale, error:1.4-pts.baseTime/pts.timescale+r.timeOffset };
            });
            assert.ok(Math.abs(before.error) > 1, 'Cold opening must reproduce the native external-ASS mismatch.');
            await page.evaluate(async () => {
              instance.currentSubtitlesOctopus.dispose(); instance.videoSubtitlesElem.remove();
              instance._resizeObserver?.disconnect(); instance._resizeObserver=null; await start(true);
            });
            await page.waitForFunction(() => instance.strmBridgeExternalClock && instance.videoSubtitlesElem.style.visibility !== 'hidden');
            await page.evaluate(() => {
              document.querySelector('video').pause();
              window.canvasMatches = color => {
                const c = instance.videoSubtitlesElem.querySelector('canvas');
                if (!c?.width || instance.videoSubtitlesElem.style.visibility === 'hidden') return false;
                const pixels = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
                let opaque = 0, matching = 0;
                for (let i = 0; i < pixels.length; i += 4) {
                  if (pixels[i + 3] > 200) {
                    opaque++;
                    if (color && color.every((v, j) => Math.abs(pixels[i + j] - v) < 5)) matching++;
                  }
                }
                return color ? matching > 20 : opaque === 0;
              };
            });
            // Seek inside the retained cold-open segment. Mixing that synthetic
            // runner's keyframe segment with an earlier runner's unbuffered
            // segments tests HLS gap recovery rather than subtitle timing.
            const sourceTimes = [22.2, 18.2, 23.2, 17.2, 22.7, 18.7];
            await page.waitForFunction(times => {
              const b = document.querySelector('video').buffered, c = instance._hlsPlayer.streamController;
              const pts = c.initPTS[c.fragPlaying.cc], shift = 1.4 - pts.baseTime / pts.timescale;
              return times.every(t => Array.from({length:b.length}, (_,i) => i).some(i => t+shift>=b.start(i) && t+shift<b.end(i)));
            }, sourceTimes);
            for (const sourceTime of sourceTimes) {
              const time = await page.evaluate(t => {
                const c = instance._hlsPlayer.streamController, pts = c.initPTS[c.fragPlaying.cc];
                // The synthetic media's independently specified TS mux origin.
                return t + 1.4 - pts.baseTime / pts.timescale;
              }, sourceTime);
              await page.evaluate(t => { document.querySelector('video').currentTime = t; }, time);
              await page.waitForFunction(t => {const v=document.querySelector('video');return !v.seeking && Math.abs(v.currentTime-t)<.05 && instance.videoSubtitlesElem.style.visibility !== 'hidden';}, time);
              const error = await page.evaluate(() => {
                const r=instance.currentSubtitlesOctopus,c=instance._hlsPlayer.streamController;
                const pts=c.initPTS[c.fragPlaying.cc]; return 1.4-pts.baseTime/pts.timescale+r.timeOffset;
              });
              assert.ok(Math.abs(error)<.01, 'Numeric source-clock mapping must stay within timestamp precision.');
              const color = sourceTime % 1 < .4 ? cueColor(Math.floor(sourceTime)) : null;
              await page.waitForFunction(color => canvasMatches(color), color);
            }
            // Negative control: a wrong worker clock must paint a cue in a blank gap.
            // The active adapter owns the clock; dispose before intentionally
            // corrupting it so this control cannot be repaired by its next frame.
            await page.evaluate(() => {
              instance.strmBridgeExternalClock.dispose();
              instance.currentSubtitlesOctopus.setIsPaused(true, 20.2);
            });
            await page.waitForFunction(color => canvasMatches(color), cueColor(20));
            assert.equal(await page.evaluate(() => canvasMatches(null)), false, 'An incorrect worker clock must fail the expected blank-gap check.');
            assert.ok(coldReads>0); assert.equal(subtitleReads,2, 'One native reproduction load + one corrected load, no reload on seeks.');
            assert.equal(clocks,1, 'Continuity-preserving seeks must reuse one calibration.');
            assert.deepEqual(errors, []);
            results.push({ engine, version, nativeErrorSeconds: before.error, numericMappingWithin10ms: true, paintedCueChecks: 6, wrongClockControl: true, repeatedSeeks: 6, subtitleReads, clocks });
            console.log('PASS external ASS cold resume and six seeks ' + engine + ' / ' + version + ', native error=' + before.error.toFixed(3) + 's');
          } catch (error) {
            console.log('BROWSER STATE', engine, version, errors, await page.evaluate(() => ({ time:document.querySelector('video').currentTime, ready:document.querySelector('video').readyState,
              buffered:Array.from({length:document.querySelector('video').buffered.length},(_,i)=>[document.querySelector('video').buffered.start(i),document.querySelector('video').buffered.end(i)]),
              renderer:!!instance.currentSubtitlesOctopus, workerActive:instance.currentSubtitlesOctopus?.workerActive, worker:!!instance.currentSubtitlesOctopus?.worker,
              fragment:instance._hlsPlayer.streamController.fragPlaying?.sn, clock:!!instance.strmBridgeExternalClock, visibility:instance.videoSubtitlesElem?.style.visibility, offset:instance.currentSubtitlesOctopus?.timeOffset, lastClock:window.lastClock, lastCanvas:window.lastCanvas,
              pixels: (()=> {const c=instance.videoSubtitlesElem?.querySelector('canvas');if(!c)return [];const a=c.getContext('2d').getImageData(0,0,c.width,c.height).data,m={};for(let i=0;i<a.length;i+=4){if(a[i+3]>200){const k=[a[i],a[i+1],a[i+2]].join(',');m[k]=(m[k]||0)+1;}}return Object.entries(m).sort((a,b)=>b[1]-a[1]).slice(0,5);})() })));
            throw error;
          } finally {
            await page.evaluate(() => { instance.strmBridgeExternalClock?.dispose(); instance.currentSubtitlesOctopus?.dispose(); instance._hlsPlayer?.destroy(); instance._resizeObserver?.disconnect(); }).catch(()=>{});
            await page.close();
          }
        }
      } finally { await browser.close(); }
    }
  } finally { for (const socket of sockets) socket.destroy(); server.close(); fs.writeFileSync(path.join(fixture, 'external-results.json'), JSON.stringify(results, null, 2)); }
})().catch(e => { console.error(e); process.exitCode=1; });
