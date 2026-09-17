// Verify the adapter against actual shipped Octopus APIs, without a browser or GPU.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), assert = require('node:assert/strict');
if (process.argv.length < 3) throw new Error('Usage: node tools/subtitle-clock-check.cjs <dashboard-ui-directory> [...]');
const adapterSource = fs.readFileSync('src/Emby.StrmBridge/Subtitles/Player.js', 'utf8');
async function check(root) {
  const source = fs.readFileSync(path.join(root, 'bower_components/javascriptsubtitlesoctopus/dist/subtitles-octopus.js'), 'utf8');
  const workerSource = fs.readFileSync(path.join(root, 'bower_components/javascriptsubtitlesoctopus/dist/subtitles-octopus-worker.js'), 'utf8');
  const workers = [], frames = new Map(), events = new Map(); let nextFrame = 0, adapter, Octopus, body;
  const ctx = { clearRect() {}, drawImage() {}, putImageData() {}, getImageData: () => ({ data: new Uint8ClampedArray(4) }) };
  function element() { return { style: {}, textContent: '', children: [], appendChild(v) { v.parentNode = this; this.children.push(v); },
    setAttribute() {}, getContext: () => ctx, getBoundingClientRect: () => ({ top: 0, left: 0 }), remove() {} }; }
  const video = Object.assign(element(), { parentNode: element(), currentTime: 12, playbackRate: 1.5, paused: false, seeking: false, readyState: 4,
    videoWidth: 1920, videoHeight: 1080, offsetWidth: 960, offsetHeight: 540,
    addEventListener(name, fn) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(fn); },
    removeEventListener(name, fn) { events.get(name)?.delete(fn); },
    requestVideoFrameCallback(fn) { const id = ++nextFrame; frames.set(id, fn); return id; },
    cancelVideoFrameCallback(id) { frames.delete(id); } });
  class Worker {
    constructor() { this.messages = []; this.handlers = new Map(); workers.push(this); }
    postMessage(v) { this.messages.push(v); }
    addEventListener(name, fn) { this.handlers.set(name, fn); }
    removeEventListener(name) { this.handlers.delete(name); }
    terminate() { this.terminated = true; }
    ready() { this.handlers.get('message')({ data: { target: 'ready' } }); }
  }
  class ImageData { constructor() { this.data = new Uint8ClampedArray(4); } }
  const quiet = { log() {}, debug() {}, info() {}, error() {} };
  const window = { Worker, ImageData, devicePixelRatio: 1, addEventListener() {}, removeEventListener() {},
    requestAnimationFrame: fn => setTimeout(fn, 0), cancelAnimationFrame: clearTimeout };
  const context = { window, Worker, ImageData, Uint8ClampedArray, localStorage: { getItem: () => 'false' },
    document: { createElement: element, baseURI: 'http://localhost/web/index.html', URL: 'http://localhost/web/index.html',
      addEventListener() {}, removeEventListener() {} }, console: quiet, performance,
    setTimeout, clearTimeout, setInterval, clearInterval, AbortController, TextDecoder, URL, Map, Set,
    define: (deps, make) => { const exports = {}; Octopus = make(exports) || exports.default; } };
  vm.runInNewContext(source, context);
  context.define = (_, make) => { adapter = make(); };
  context.Emby = { importModule: async () => Octopus };
  context.fetch = async (_, options) => {
    if (options.method === 'POST') return { ok: true, status: 200, json: async () => ({ SessionId: 'fixture' }) };
    if (options.method === 'DELETE') return { ok: true, status: 204 };
    const stream = new ReadableStream({ start(c) { body = c; c.enqueue(new TextEncoder().encode('[Script Info]\n[Events]\nDialogue: 0,0:00:12.00,0:00:14.00,Default,,0,0,0,,fixture\n')); } });
    return { ok: true, status: 200, body: stream };
  };
  vm.runInNewContext(adapterSource, context);
  const instance = { _strmBridgeEpoch: 1, _currentPlayOptions: {}, _hlsPlayer: { streamController: { fragPlaying: { start: 0, duration: 100, cc: 0 }, initPTS: [{ baseTime: 0, timescale: 90000 }] } } };
  try {
    await adapter.render({ instance, epoch: 1, video, item: { Id: 'fixture' }, source: { Id: 'source', MediaStreams: [] }, track: { Index: 1 },
      api: { getUrl: x => 'http://localhost/' + x, accessToken: () => 'fixture' }, fallback: () => { throw new Error('unexpected fallback'); } });
    assert.equal(workers.length, 1);
    const worker = workers[0]; worker.ready();
    assert.ok(worker.messages.some(m => m.target === 'video' && m.isPaused === false && m.currentTime === 12));
    assert.ok(worker.messages.some(m => m.target === 'video' && m.rate === 1.5));
    video.currentTime = 12.25;
    const [id, fn] = frames.entries().next().value; frames.delete(id); fn(0, { mediaTime: 12.125 });
    assert.equal(worker.messages.filter(m => m.target === 'video').at(-1).currentTime, 12.25);
    instance.currentSubtitlesOctopus.onTimeUpdate(3);
    for (const fn of events.get('timeupdate') || []) fn();
    assert.equal(worker.messages.filter(m => m.target === 'video').at(-1).currentTime, 12.25);
    await new Promise(resolve => setTimeout(resolve, 30));
    video.currentTime = 42; video.seeking = true;
    for (const fn of events.get('seeking') || []) fn();
    instance.currentSubtitlesOctopus.onTimeUpdate(12.125);
    assert.equal(worker.messages.filter(m => m.target === 'video').at(-1).currentTime, 42);
    assert.equal(worker.messages.filter(m => m.target === 'video').at(-1).isPaused, true);
    video.seeking = false;
    for (const fn of events.get('seeked') || []) fn();
    assert.equal(worker.messages.filter(m => m.target === 'video').at(-1).currentTime, 42);
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.ok(worker.messages.some(m => (m.target === 'set-track' && m.content.includes('fixture')) || (m.target === 'add-to-track' && m.chunk.includes('fixture'))));
    assert.ok(!video.parentNode.children.some(e => e.textContent.includes('加载中')));
    // Execute the real worker's clock getter: paused initialization does not advance.
    const begin = workerSource.indexOf('self.getCurrentTime=function()'), end = workerSource.indexOf(',self.setCurrentTime=', begin);
    assert.ok(begin >= 0 && end > begin);
    const clock = { lastCurrentTime: 12.125, lastCurrentTimeReceivedAt: 1000, _isPaused: false, rate: 1.5, setIsPaused() {} };
    vm.runInNewContext(workerSource.slice(begin, end), { self: clock, Date: { now: () => 1200 }, console: quiet });
    assert.equal(clock.getCurrentTime(), 12.425); clock._isPaused = true; assert.equal(clock.getCurrentTime(), 12.125);
    instance.customTrackIndex = 1;
    assert.throws(() => worker.handlers.get('error')({ message: 'synthetic-worker-failure' }), /Worker error/);
    assert.equal(worker.terminated, true);
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(instance.strmBridgeSubtitle, null);
    assert.equal(instance.customTrackIndex, -1);
    assert.equal(instance.currentSubtitlesOctopus, null);
    assert.equal(worker.messages.filter(m => m.target === 'destroy').length, 1);
    console.log('PASS actual Octopus late-start presentation clock, stale callback isolation, immediate cue delivery, single error disposal and silent load: ' + path.basename(root));
  } finally {
    instance.strmBridgeSubtitle?.dispose(); body?.close();
    assert.equal(frames.size, 0); assert.ok(workers.every(w => w.terminated));
  }

  // Use the actual shipped renderer API for live fallback, including a playing
  // event swallowed during calibration. No future video event may be required.
  let finishClock;
  context.fetch = () => new Promise(resolve => { finishClock = resolve; });
  video.currentTime = 101; video.seeking = false; video.paused = false;
  const externalCanvas = element(), externalParent = element(); externalParent.appendChild(externalCanvas);
  const external = new Octopus({ video, canvas: externalCanvas, canvasParent: externalParent, subContent: '', workerUrl: 'fixture-worker.js' });
  const externalWorker = workers.at(-1); externalWorker.ready();
  const externalInstance = { _strmBridgeEpoch: 2, currentSubtitlesOctopus: external, videoSubtitlesElem: element(),
    _currentSubtitleOffset: 500, _currentPlayOptions: { url: 'http://localhost/master.m3u8?PlaySessionId=external&SegmentContainer=ts' },
    _hlsPlayer: { streamController: { fragPlaying: { start: 0, duration: 200, cc: 0 }, initPTS: [{ baseTime: 0, timescale: 90000 }] } } };
  const handle = adapter.bindExternalClock({ instance: externalInstance, epoch: 2, video, item: { Id: 'fixture' }, source: { Id: 'source' },
    track: { Index: 2, IsExternal: true }, api: { getUrl: x => 'http://localhost/' + x, accessToken: () => 'fixture' } });
  try {
    for (const fn of events.get('playing') || []) fn();
    assert.equal(externalWorker.messages.filter(m => m.target === 'video' && m.isPaused !== undefined).at(-1).isPaused, true);
    finishClock({ ok: false, status: 422 });
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(externalInstance.strmBridgeExternalClock, null);
    const state = externalWorker.messages.filter(m => m.target === 'video' && m.isPaused !== undefined).at(-1);
    assert.equal(state.isPaused, false); assert.equal(state.currentTime, 100.5);
    assert.equal(externalWorker.messages.filter(m => m.target === 'video' && m.rate !== undefined).at(-1).rate, 1.5);
    const begin = workerSource.indexOf('self.getCurrentTime=function()'), end = workerSource.indexOf(',self.setCurrentTime=', begin);
    const clock = { lastCurrentTime: state.currentTime, lastCurrentTimeReceivedAt: 1000, _isPaused: state.isPaused, rate: 1.5, setIsPaused() {} };
    vm.runInNewContext(workerSource.slice(begin, end), { self: clock, Date: { now: () => 1200 }, console: quiet });
    assert.equal(clock.getCurrentTime(), 100.8, 'Worker must continue advancing after live fallback.');
    console.log('PASS actual Octopus external fallback restores advancing worker clock and rate: ' + path.basename(root));
  } finally { handle.dispose(); external.dispose(); }

}
(async () => { for (const root of process.argv.slice(2)) await check(root); })().catch(e => { console.error(e); process.exitCode = 1; });
