const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/Emby.StrmBridge/Subtitles/Player.js', 'utf8');
const header = '[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n';
const cue = 'Dialogue: 0,0:00:02.00,0:00:04.00,Default,,0,0,0,,中文字幕';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function harness(progressive = true, outside = false, exclusive = false, frameClock = false, moduleDelay = 0) {
  let adapter, renderer, stream;
  let activeReads = 0;
  const statuses = [], sessionStatuses = [], diagnostics = [];
  let partialWindow = false;
  const calls = [], paints = [], appended = [], aborted = [], listeners = new Map(), frameCallbacks = new Map();
  let frameId = 0;
  function element() { return { style: {}, hidden: false, children: [], textContent: '', appendChild(child) { this.children.push(child); }, remove() {}, setAttribute() {} }; }
  const video = { parentNode: element(), currentTime: 0, paused: false, seeking: false, playbackRate: 1,
    addEventListener(name, fn) { listeners.set(name, fn); }, removeEventListener(name) { listeners.delete(name); } };
  if (frameClock) {
    video.requestVideoFrameCallback = fn => { const id = ++frameId; frameCallbacks.set(id, fn); return id; };
    video.cancelVideoFrameCallback = id => frameCallbacks.delete(id);
  }
  class Octopus {
    constructor(options) { renderer = this; this.options = options; this.paused = true; this.rate = 1; this.times = []; if (progressive) this.addToTrack = text => appended.push(text); }
    resize() { this.resizes = (this.resizes || 0) + 1; }
    setTrack(text) { paints.push(text); }
    setCurrentTime(time) { this.times.push(time); }
    setIsPaused(paused, time) { this.paused = paused; this.setCurrentTime(time); }
    setRate(rate) { this.rate = rate; }
    dispose() { this.disposed = true; this.worker = null; this.disposeCount = (this.disposeCount || 0) + 1; }
  }
  const fetch = async (url, options) => {
    calls.push({ url, options });
    if (options.method === 'POST') {
      const failure = sessionStatuses.shift();
      if (failure) return { ok: false, status: failure.status, json: async () => ({ ReasonCode: failure.reason }) };
      return { ok: !outside, status: outside ? 422 : 200, json: async () => outside ? { ReasonCode: 'outside-scope' } : { SessionId: 'session', WindowSeconds: 60 } };
    }
    if (options.method === 'DELETE') return { ok: true, status: 204 };
    if (exclusive && activeReads) return { ok: false, status: 503 };
    const status = statuses.shift();
    if (status && typeof status === 'object') { await status.ready; return { ok: false, status: status.status }; }
    if (status) return { ok: false, status };
    const response = new ReadableStream({ start(controller) {
      stream = controller;
      activeReads++;
      controller.enqueue(new TextEncoder().encode(header + cue + '\n'));
      options.signal.addEventListener('abort', () => { activeReads--; aborted.push(url); try { controller.error(new Error('aborted')); } catch {} });
    } });
    return { ok: true, status: 200, headers: { get: () => partialWindow ? 'partial' : null }, body: response };
  };
  let resizeCallback;
  class ResizeObserver { constructor(callback) { resizeCallback = callback; } observe() { resizeCallback(); } disconnect() {} }
  const context = { ResizeObserver, define: (_, make) => { adapter = make(); }, document: { createElement: element, baseURI: 'http://localhost/web/index.html' },
    Emby: { importModule: async () => { if (moduleDelay) await sleep(moduleDelay); return Octopus; } }, window: { ResizeObserver }, fetch, AbortController, TextDecoder, URL,
    setTimeout, clearTimeout, setInterval, clearInterval, Map, Set, console: { debug: (...args) => diagnostics.push(args.join(' ')) } };
  vm.runInNewContext(source, context);
  const instance = { _strmBridgeEpoch: 1, _currentPlayOptions: {}, _hlsPlayer: { streamController: { fragPlaying: { start: 0, duration: 100000, cc: 0 }, initPTS: [{ baseTime: 0, timescale: 90000 }] } } };
  let fallback = 0;
  const options = { instance, video, epoch: 1, track: { Index: 3 }, item: { Id: 'item' }, source: { Id: 'exact', MediaStreams: [] },
    api: { getUrl: x => 'http://localhost/' + x, accessToken: () => 'fixture-only' }, fallback: () => { fallback++; } };
  return { adapter, options, calls, statuses, sessionStatuses, diagnostics, paints, appended, aborted, listeners, frameCallbacks, video,
    partial: () => { partialWindow = true; }, resize: () => resizeCallback?.(), renderer: () => renderer, stream: () => stream, fallback: () => fallback };
}

test('ASS overlap retains legitimate duplicate events and styles', () => {
  const h = harness();
  const a = h.adapter.parse(header + cue + '\n' + cue + '\n');
  const b = h.adapter.parse(header + cue + '\n');
  const merged = h.adapter.merge([a, b]);
  assert.equal(merged.events.length, 2);
  assert.match(merged.text, /ScriptType: v4.00\+/);
});

for (const progressive of [false, true]) test(`automatic first cue before EOF, seek cancels, stop drains (${progressive ? '4.10' : '4.9'})`, async () => {
  const h = harness(progressive);
  try {
    await h.adapter.render(h.options);
    await sleep(350);
    assert.equal(h.calls[0].options.method, 'POST');
    assert.deepEqual(JSON.parse(h.calls[0].options.body), { Id: 'item', MediaSourceId: 'exact', Index: 3, PlaySessionId: '', VideoStartTicks: 0, NativeHlsClock: false });
    assert.ok(h.paints.some(text => text.includes('中文字幕')), 'Must paint while the HTTP stream is still open.');
    const second = cue.replace('中文字幕', '下一句');
    h.stream().enqueue(new TextEncoder().encode(second + '\n'));
    await sleep(350);
    assert.ok((progressive ? h.appended : h.paints).some(text => text.includes('下一句')));
    h.video.currentTime = 125;
    h.listeners.get('seeking')();
    await sleep(650);
    assert.ok(h.aborted.length >= 1);
    assert.ok(h.calls.some(call => call.url.includes('StartPositionTicks=1250000000')));
    h.options.instance.strmBridgeSubtitle.dispose();
    await sleep(30);
    assert.ok(h.renderer().disposed);
    assert.ok(h.calls.some(call => call.options.method === 'DELETE'));
    assert.equal(h.listeners.size, 0);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('outside-scope media returns to native renderer without a second subtitle reader', async () => {
  const h = harness(true, true);
  h.options.instance.customTrackIndex = 3;
  await h.adapter.render(h.options);
  assert.equal(h.fallback(), 1);
  assert.equal(h.options.instance.customTrackIndex, 3, 'Native fallback retains the host selection.');
  assert.equal(h.calls.filter(call => call.options.method === 'GET').length, 0);
});

test('pause stops reads and resumes an incomplete interval; offset uses media time', async () => {
  const h = harness();
  try {
    h.options.instance._currentPlayOptions.transcodingOffsetTicks = 1200000000;
    await h.adapter.render(h.options);
    await sleep(350);
    assert.ok(h.calls.some(c => c.url.includes('StartPositionTicks=1200000000')));
    h.video.paused = true; h.listeners.get('pause')();
    const reads = h.calls.filter(c => c.options.method === 'GET').length;
    await sleep(650);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, reads);
    assert.ok(h.aborted.length);
    h.video.paused = false; h.listeners.get('playing')();
    await sleep(50);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, reads + 1);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('cold seek clears old renderer contents before the new interval arrives', async () => {
  const h = harness();
  try {
    await h.adapter.render(h.options); await sleep(350);
    h.options.instance._hlsPlayer.streamController.initPTS[0].baseTime = 90000;
    h.video.currentTime = 125; h.listeners.get('seeking')();
    assert.ok(!h.paints.at(-1).includes('Dialogue:'), 'Old cues must be removed synchronously.');
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('an expired idle session is renewed automatically once', async () => {
  const h = harness(); h.statuses.push(404);
  try {
    await h.adapter.render(h.options); await sleep(350);
    assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 2);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, 2);
    assert.ok(h.paints.some(text => text.includes('中文字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('long-event retention uses original interval without restarting ASS effects', () => {
  const h = harness();
  const event = 'Dialogue: 0,0:00:01.00,0:03:00.00,Default,,0,0,0,,{\\fad(500,500)}long';
  assert.equal(h.adapter.activeEvent(event, 121), true);
  assert.equal(h.adapter.activeEvent(event, 0), false);
  assert.equal(h.adapter.activeEvent(event, 180), false);
});

for (const progressive of [false, true]) test(`natural minute crossing cancels stale extraction before requesting next window (${progressive ? '4.10' : '4.9'})`, async () => {
  const h = harness(progressive, false, true);
  try {
    h.video.currentTime = 170;
    await h.adapter.render(h.options); await sleep(350);
    const old = h.calls.find(c => c.url.includes('StartPositionTicks=1700000000'));
    assert.ok(old && !old.options.signal.aborted);
    h.video.currentTime = 181;
    // No seeking event: ordinary playback crossed the fixed window boundary.
    h.listeners.get('playing')();
    await sleep(850);
    assert.ok(old.options.signal.aborted, 'An unfinished obsolete window must release the sole reader.');
    const next = h.calls.filter(c => c.url.includes('StartPositionTicks=1810000000'));
    assert.equal(next.length, 1, 'The current window must not exhaust busy retries against its own predecessor.');
    assert.ok(!next[0].options.signal.aborted);
    const actual = cue.replace('0:00:02.00,0:00:04.00', '0:03:02.00,0:03:04.00').replace('中文字幕', '跨窗口字幕');
    h.stream().enqueue(new TextEncoder().encode(actual + '\n')); await sleep(350);
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('跨窗口字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('a late selection starts at current media time and a backward seek reloads uncovered time', async () => {
  const h = harness();
  try {
    h.video.currentTime = 170;
    await h.adapter.render(h.options); await sleep(350);
    assert.ok(h.calls.some(c => c.url.includes('StartPositionTicks=1700000000') && c.url.includes('EndPositionTicks=1800000000')));
    h.stream().close(); await sleep(50);
    h.video.currentTime = 150; h.listeners.get('seeking')(); await sleep(650);
    assert.ok(h.calls.some(c => c.url.includes('StartPositionTicks=1500000000')), 'A completed partial window must not cover earlier time.');
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('subtitle session binds to current video job without copying URL credentials', async () => {
  const h = harness();
  h.options.instance._currentPlayOptions.url = 'http://localhost/videos/item/master.m3u8?PlaySessionId=play-a&StartTimeTicks=1682151423&api_key=not-subtitle-data';
  try {
    await h.adapter.render(h.options);
    const data = JSON.parse(h.calls.find(c => c.options.method === 'POST').options.body);
    assert.equal(data.PlaySessionId, 'play-a');
    assert.equal(data.VideoStartTicks, 1682151423);
    assert.ok(!JSON.stringify(data).includes('not-subtitle-data'));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

for (const status of [422, 503]) test(`missing shared video job does not fall back to independent extraction (${status})`, async () => {
  const h = harness();
  for (let i = 0; i < 3; i++) h.sessionStatuses.push({ status, reason: 'video-input-unavailable' });
  try {
    await h.adapter.render(h.options);
    assert.equal(h.fallback(), 0);
    assert.equal(h.calls.filter(c => c.options.method === 'POST').length, status === 503 ? 3 : 1);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, 0);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('subtitle session waits briefly for a starting HLS video job then renders', async () => {
  const h = harness();
  h.sessionStatuses.push({ status: 503, reason: 'video-input-unavailable' });
  try {
    await h.adapter.render(h.options);
    await sleep(300);
    assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 2);
    assert.equal(h.fallback(), 0);
    assert.ok(h.paints.some(text => text.includes('中文字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('disposing while waiting for a video job cancels session retries', async () => {
  const h = harness();
  h.sessionStatuses.push({ status: 503, reason: 'video-input-unavailable' });
  const started = h.adapter.render(h.options);
  await sleep(20);
  h.options.instance.strmBridgeSubtitle.dispose();
  await started;
  await sleep(600);
  assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 1);
  assert.equal(h.fallback(), 0);
});

test('seek in a completed window reloads cues for the new HLS timeline', async () => {
  const h = harness();
  try {
    await h.adapter.render(h.options); await sleep(350);
    h.stream().close(); await sleep(30);
    const before = h.calls.filter(c => c.options.method === 'GET').length;
    h.options.instance._hlsPlayer.streamController.initPTS[0].baseTime = 90000;
    h.video.currentTime = 15; h.listeners.get('seeking')();
    assert.ok(!h.paints.at(-1).includes('Dialogue:'), 'A changed MSE origin must not leave stale cues visible.');
    await sleep(650);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, before + 1);
    assert.ok(h.calls.some(c => c.url.includes('StartPositionTicks=150000000')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('late subtitle creation synchronizes the running worker clock and playback rate', async () => {
  const h = harness();
  try {
    h.video.currentTime = 2.5; h.video.playbackRate = 1.5;
    await h.adapter.render(h.options);
    assert.equal(h.renderer().paused, false, 'The video playing event happened before renderer construction.');
    assert.equal(h.renderer().rate, 1.5);
    assert.equal(h.renderer().times.at(-1), 2.5);
    h.video.paused = true; h.listeners.get('pause')();
    assert.equal(h.renderer().paused, true);
    h.video.paused = false; h.listeners.get('playing')();
    assert.equal(h.renderer().paused, false);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('loading stays silent and complete cues are submitted without a 250 ms hold', async () => {
  const h = harness();
  try {
    const render = h.adapter.render(h.options);
    assert.equal(h.video.parentNode.children.some(e => /字幕加载中/.test(e.textContent)), false);
    await render; await sleep(60);
    assert.ok(h.paints.concat(h.appended).some(text => text.includes('中文字幕')), 'Ready data should be submitted immediately.');
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('one presentation clock drives the renderer despite stale frame PTS after seek', async () => {
  const h = harness(true, false, false, true);
  try {
    h.video.currentTime = 3.9;
    h.options.instance._currentPlayOptions.transcodingOffsetTicks = 1200000000;
    h.options.instance._currentSubtitleOffset = 200;
    await h.adapter.render(h.options);
    assert.equal(h.frameCallbacks.size, 1);
    const [id, callback] = h.frameCallbacks.entries().next().value;
    h.frameCallbacks.delete(id); callback(0, { mediaTime: 3.5 });
    assert.equal(h.renderer().times.at(-1), 123.7, 'Use the presentation clock, with each offset applied once.');
    h.renderer().setCurrentTime(1); h.renderer().setIsPaused(false, 2);
    assert.equal(h.renderer().times.at(-1), 123.7, 'Foreign clock writers cannot replace the authoritative clock.');
    h.listeners.get('waiting')(); assert.equal(h.renderer().paused, true);
    h.listeners.get('playing')(); assert.equal(h.renderer().paused, false);
    h.video.playbackRate = 2; h.listeners.get('ratechange')(); assert.equal(h.renderer().rate, 2);
    h.options.instance.strmBridgeSubtitle.dispose(); assert.equal(h.frameCallbacks.size, 0);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

for (const mse of [false, true]) test(`session selects the ${mse ? 'hls.js' : 'native HLS'} clock`, async () => {
  const h = harness();
  try {
    if (!mse) delete h.options.instance._hlsPlayer;
    await h.adapter.render(h.options);
    if (mse) {
      const data = JSON.parse(h.calls.find(c => c.options.method === 'POST').options.body);
      assert.equal(data.NativeHlsClock, false); assert.equal(h.fallback(), 0);
    } else {
      assert.equal(h.calls.length, 0); assert.equal(h.fallback(), 1);
      assert.equal(h.renderer(), undefined);
    }
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('renderer failure stops frame callbacks without interrupting video', async () => {
  const h = harness(true, false, false, true);
  try {
    await h.adapter.render(h.options);
    h.renderer().options.onError();
    assert.equal(h.frameCallbacks.size, 0);
    assert.equal(h.video.paused, false);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

for (const failure of ['session', 'stream', 'renderer']) test(`all subtitle failure stages stay silent (${failure})`, async () => {
  const h = harness(true, false, false, true);
  if (failure === 'session') h.sessionStatuses.push({ status: 500, reason: 'fixture' });
  if (failure === 'stream') h.statuses.push(500);
  try {
    await h.adapter.render(h.options); await sleep(50);
    if (failure === 'renderer') h.renderer().options.onError(new Error('private://token/subtitle-text'));
    assert.equal(h.video.parentNode.children.some(e => e.textContent), false, 'Failures must not add text to the player.');
    assert.equal(h.video.paused, false);
    assert.equal(h.diagnostics.length, 1);
    assert.ok(!JSON.stringify(h.diagnostics).includes('private'));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('confirmed direct MKV input does not retry a missing server video job', async () => {
  const h = harness();
  h.options.instance._currentPlayOptions.url = '/emby/StrmBridge/Playback/v3/private-ticket/stream.mkv?api_key=private-key';
  for (let i = 0; i < 3; i++) h.sessionStatuses.push({ status: 503, reason: 'video-input-unavailable' });
  try {
    await h.adapter.render(h.options);
    assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 1);
    assert.equal(h.fallback(), 0, 'Do not start a second remote reader after a supported source has no shared input.');
    assert.equal(h.renderer(), undefined);
    assert.equal(h.video.parentNode.children.length, 0);
    assert.match(h.diagnostics.join(), /direct-play-unavailable/);
    assert.ok(!h.diagnostics.join().includes('private'));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('an out-of-scope direct MKV still uses native handling', async () => {
  const h = harness(true, true);
  h.options.instance._currentPlayOptions.url = '/emby/StrmBridge/Playback/v3/fixture/stream.mkv';
  await h.adapter.render(h.options);
  assert.equal(h.fallback(), 1);
  assert.equal(h.diagnostics.length, 0);
});

test('initial and unchanged ResizeObserver notifications do not erase an active cue', async () => {
 const h=harness();h.video.offsetWidth=640;h.video.offsetHeight=360;
 try {
  await h.adapter.render(h.options);await sleep(50);
  assert.equal(h.renderer().resizes||0,0);
  h.resize();assert.equal(h.renderer().resizes||0,0);
  h.video.offsetWidth=1280;h.resize();assert.equal(h.renderer().resizes,1);
  h.resize();assert.equal(h.renderer().resizes,1);
 } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('same-millisecond worker frames replace blank frames while older frames stay rejected', () => {
 const h=harness(),listeners=new Set(),accepted=[];
 const renderer={lastRenderTime:0,worker:{addEventListener:(_,fn)=>listeners.add(fn),removeEventListener:(_,fn)=>listeners.delete(fn)}};
 renderer.onWorkerMessage=e=>{if(renderer.lastRenderTime<e.data.time){renderer.lastRenderTime=e.data.time;accepted.push(e.data.content);}};
 listeners.add(renderer.onWorkerMessage);h.adapter.preserveFrameOrder(renderer);
 for(const [time,content] of [[100,'blank'],[100,'cue'],[99,'stale'],[101,'next']])
  [...listeners].forEach(fn=>fn({data:{target:'canvas',op:'renderCanvas',time,content}}));
 assert.deepEqual(accepted,['blank','cue','next']);assert.equal(listeners.size,1);
 renderer.worker.removeEventListener('message',renderer.onWorkerMessage);assert.equal(listeners.size,0);
});

test('MSE uses retained initPTS across runner restarts and resolves the target continuity on seek', () => {
  const h = harness();
  const first = { start: 1062, duration: 18, cc: 0 };
  const target = { start: 234, duration: 18, cc: 0 };
  const discontinuity = { start: 300, duration: 18, cc: 1 };
  const player = h.options.instance;
  player._hlsPlayer = { streamController: { fragPlaying: first, initPTS: [{ baseTime: 9.1 * 90000, timescale: 90000 }] }, latestLevelDetails: { fragments: [first, target, discontinuity] } };
  assert.equal(h.adapter.mseClock(player, 1067), -91000000);
  assert.equal(h.adapter.mseClock(player, 245), -91000000, 'A new server keyframe does not reset MSE initPTS.');
  assert.equal(h.adapter.mseClock(player, 305), undefined, 'Do not reuse the old continuity while the new one is unknown.');
  player._hlsPlayer.streamController.initPTS[1] = { baseTime: 7.06 * 90000, timescale: 90000 };
  assert.equal(h.adapter.mseClock(player, 305), -70600000);
  player._currentPlayOptions.transcodingOffsetTicks = 50000000;
  assert.equal(h.adapter.mseClock(player, 305), -20600000);
});

test('MSE windows wait for the actual clock and send it on every seek request', async () => {
  const h = harness();
  const controller = { fragPlaying: { start: 0, duration: 60, cc: 0 }, initPTS: [] };
  h.options.instance._hlsPlayer = { streamController: controller };
  try {
    await h.adapter.render(h.options); await sleep(40);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, 0);
    controller.initPTS[0] = { baseTime: 9.1 * 90000, timescale: 90000 };
    h.listeners.get('playing')(); await sleep(40);
    assert.match(h.calls.filter(c => c.options.method === 'GET').at(-1).url, /MseTimestampOffsetTicks=-91000000/);
    const painted = h.paints.length;
    h.video.currentTime = 18; h.listeners.get('seeking')(); await sleep(650);
    assert.equal(h.paints.length, painted, 'Unchanged MSE cues keep the same libass track/font cache across seek.');
    assert.match(h.calls.filter(c => c.options.method === 'GET').at(-1).url, /StartPositionTicks=180000000.*MseTimestampOffsetTicks=-91000000/);
    controller.initPTS[0] = { baseTime: 7.06 * 90000, timescale: 90000 };
    await sleep(1100);
    assert.match(h.calls.filter(c => c.options.method === 'GET').at(-1).url, /MseTimestampOffsetTicks=-70600000/);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});


test('a completed runner is only a partial window and is polled again without manual seek', async () => {
  const h = harness(); h.partial();
  try {
    await h.adapter.render(h.options); await sleep(50);
    h.stream().close(); await sleep(50);
    const initial = h.calls.filter(c => c.options.method === 'GET').length;
    await sleep(1600);
    assert.ok(h.calls.filter(c => c.options.method === 'GET').length > initial, 'Local EOF cannot permanently cache an incomplete minute.');
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

for (const status of [500, 503]) test(`transient subtitle failures recover without a new user action (${status})`, async () => {
  const h = harness(); h.statuses.push(status);
  try {
    await h.adapter.render(h.options); await sleep(1700);
    assert.ok(h.calls.filter(c => c.options.method === 'GET').length >= 2);
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('中文字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('a complete prefetched window is reused before its playback interval starts', async () => {
  const h = harness(); h.video.currentTime = 45;
  try {
    await h.adapter.render(h.options); await sleep(30);
    h.stream().close(); await sleep(30);
    h.listeners.get('playing')(); await sleep(30);
    h.stream().close(); await sleep(30);
    for (const position of [46, 50, 59, 60]) {
      h.video.currentTime = position; h.listeners.get('playing')(); await sleep(30);
    }
    assert.equal(h.calls.filter(c => c.url.includes('StartPositionTicks=600000000')).length, 1);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, 2);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('playback becoming ready during renderer import does not retain a paused clock', async () => {
  const h = harness(true, false, false, false, 80); h.video.readyState = 2;
  try {
    const started = h.adapter.render(h.options);
    await sleep(20); h.video.readyState = 4;
    // The playing event precedes registration of our listeners.
    h.listeners.get('playing')?.();
    await started;
    assert.equal(h.renderer().paused, false);
    assert.equal(h.renderer().times.at(-1), h.video.currentTime);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('a stale forbidden response cannot disable the subtitle after a seek', async () => {
  const h = harness(); let finish;
  h.statuses.push({ status: 403, ready: new Promise(resolve => { finish = resolve; }) });
  try {
    await h.adapter.render(h.options); await sleep(30);
    h.video.currentTime = 125; h.listeners.get('seeking')(); finish();
    await sleep(350);
    assert.ok(h.calls.some(c => c.url.includes('StartPositionTicks=1250000000')));
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('中文字幕')));
    assert.equal(h.diagnostics.length, 0);
  } finally { finish(); h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('startup failure releases the host selection so the same track can be selected again', async () => {
  const h = harness(); h.options.instance.customTrackIndex = 3;
  h.sessionStatuses.push({ status: 500 });
  await h.adapter.render(h.options);
  assert.equal(h.options.instance.customTrackIndex, -1);
  assert.equal(h.options.instance.strmBridgeSubtitle, null);
  try {
    await h.adapter.render(h.options); await sleep(30);
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('中文字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('late disposal of an old adapter cannot clear the current host track selection', async () => {
  const h = harness();
  await h.adapter.render(h.options);
  const old = h.options.instance.strmBridgeSubtitle;
  const replacement = { dispose() {} };
  h.options.instance.strmBridgeSubtitle = replacement;
  h.options.instance.customTrackIndex = 7;
  old.dispose();
  assert.equal(h.options.instance.strmBridgeSubtitle, replacement);
  assert.equal(h.options.instance.customTrackIndex, 7);
});

for (const selfDisposes of [false, true]) test(`renderer failure releases same-track selection without double disposal (self-dispose=${selfDisposes})`, async () => {
  const h = harness(true, false, false, true); h.options.instance.customTrackIndex = 3;
  try {
    await h.adapter.render(h.options);
    const failed = h.renderer();
    failed.options.onError();
    assert.equal(failed.disposed, undefined, 'Octopus must finish its own error handler before adapter disposal.');
    if (selfDisposes) failed.dispose();
    await sleep(30);
    assert.equal(failed.disposeCount, 1);
    assert.equal(h.options.instance.strmBridgeSubtitle, null);
    assert.equal(h.options.instance.customTrackIndex, -1);
    assert.equal(h.options.instance.currentSubtitlesOctopus, null);
    assert.equal(h.listeners.size, 0); assert.equal(h.frameCallbacks.size, 0);
    assert.equal(h.video.paused, false);
    await h.adapter.render(h.options); await sleep(30);
    assert.notEqual(h.renderer(), failed);
    assert.equal(h.renderer().disposed, undefined);
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('中文字幕')));
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('deferred renderer error cleanup does not clear a replacement adapter selection', async () => {
  const h = harness();
  await h.adapter.render(h.options);
  const old = h.options.instance.strmBridgeSubtitle;
  h.renderer().options.onError();
  const replacement = { dispose() {} };
  h.options.instance.strmBridgeSubtitle = replacement;
  h.options.instance.customTrackIndex = 7;
  h.options.instance._strmBridgeEpoch++;
  await sleep(30);
  assert.equal(h.options.instance.strmBridgeSubtitle, replacement);
  assert.equal(h.options.instance.customTrackIndex, 7);
  old.dispose();
});

for (const status of [403, 409, 404]) test(`current terminal stream failure releases selection and allows manual retry (${status})`, async () => {
  const h = harness(); let finish;
  h.options.instance.customTrackIndex = 3;
  h.statuses.push({ status, ready: new Promise(resolve => { finish = resolve; }) });
  if (status === 404) h.statuses.push(404); // Missing even after one session renewal.
  try {
    await h.adapter.render(h.options);
    const failed = h.renderer(); finish(); await sleep(30);
    assert.equal(failed.disposeCount, 1);
    assert.equal(h.options.instance.strmBridgeSubtitle, null);
    assert.equal(h.options.instance.customTrackIndex, -1);
    assert.equal(h.options.instance.currentSubtitlesOctopus, null);
    assert.equal(h.options.instance.videoSubtitlesElem, null);
    assert.equal(h.listeners.size, 0);
    await sleep(600);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, status === 404 ? 2 : 1, 'Terminal errors must not retry automatically.');
    await h.adapter.render(h.options); await sleep(30);
    assert.notEqual(h.renderer(), failed);
    assert.ok(h.paints.concat(h.appended).some(t => t.includes('中文字幕')));
  } finally { finish(); h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('terminal stream failure before module initialization does not construct a stale renderer', async () => {
  const h = harness(true, false, false, false, 80);
  h.options.instance.customTrackIndex = 3; h.statuses.push(403);
  await h.adapter.render(h.options);
  assert.equal(h.renderer(), undefined);
  assert.equal(h.options.instance.strmBridgeSubtitle, null);
  assert.equal(h.options.instance.customTrackIndex, -1);
  assert.equal(h.listeners.size, 0);
  assert.ok(h.calls.some(c => c.options.method === 'DELETE'));
});

test('authorization failure when renewing a session terminates rather than repeating the renewal', async () => {
  const h = harness(); let finish;
  h.statuses.push({ status: 404, ready: new Promise(resolve => { finish = resolve; }) });
  try {
    await h.adapter.render(h.options);
    h.sessionStatuses.push({ status: 403 }); finish(); await sleep(30);
    assert.equal(h.options.instance.strmBridgeSubtitle, null);
    assert.equal(h.renderer().disposeCount, 1);
    assert.ok(h.diagnostics.some(d => d.includes('subtitle-http-403')));
    await sleep(1100);
    assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 2);
  } finally { finish(); h.options.instance.strmBridgeSubtitle?.dispose(); }
});

test('invalid UTF-8 terminates the current subtitle while network read failures remain recoverable', async () => {
  const h = harness();
  try {
    await h.adapter.render(h.options); await sleep(30);
    h.stream().error(new TypeError('synthetic-network-error'));
    await sleep(1600);
    assert.ok(h.calls.filter(c => c.options.method === 'GET').length >= 2);
    assert.ok(h.options.instance.strmBridgeSubtitle, 'A reader transport TypeError is transient.');
    const reads = h.calls.filter(c => c.options.method === 'GET').length;
    h.stream().enqueue(new Uint8Array([0xff])); await sleep(30);
    assert.equal(h.options.instance.strmBridgeSubtitle, null);
    assert.equal(h.options.instance.customTrackIndex, -1);
    assert.equal(h.renderer().disposeCount, 1);
    assert.ok(h.diagnostics.some(d => d.includes('invalid-subtitle')));
    await sleep(1100);
    assert.equal(h.calls.filter(c => c.options.method === 'GET').length, reads);
  } finally { h.options.instance.strmBridgeSubtitle?.dispose(); }
});
