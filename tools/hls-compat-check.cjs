// Exercise the complete host library after the C# transform, without a network
// or real browser. The missing MMS startstreaming event is injected deliberately.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
if (process.argv.length < 3) throw Error('Usage: node tools/hls-compat-check.cjs <transformed-hls.js> [...]');
class Target {
  constructor() { this.listeners = new Map(); }
  addEventListener(name, fn) { if (!this.listeners.has(name)) this.listeners.set(name, new Set()); this.listeners.get(name).add(fn); }
  removeEventListener(name, fn) { this.listeners.get(name)?.delete(fn); }
  emit(name) { for (const fn of [...(this.listeners.get(name) || [])]) fn({ type: name, target: this }); }
}
class MediaSource extends Target { constructor() { super(); this.readyState = 'open'; } static isTypeSupported() { return true; } }
class ManagedMediaSource extends MediaSource {}
function video() {
  return Object.assign(new Target(), { src: '', seeking: false, childNodes: [],
    appendChild(child) { this.childNodes.push(child); }, removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); },
    removeAttribute(name) { if (name === 'src') this.src = ''; }, load() {}, getAttribute(name) { return this[name]; },
    querySelectorAll() { return []; } });
}
for (const file of process.argv.slice(2)) {
  let Hls;
  const context = { console, performance, setTimeout, clearTimeout, setInterval, clearInterval,
    URL: { createObjectURL: () => 'blob:fixture', revokeObjectURL() {} },
    navigator: { userAgent: 'fixture' }, location: { href: 'http://fixture.invalid/' },
    document: { createElement: () => ({}) }, MediaSource, ManagedMediaSource,
    define: (_, make) => { const exports = {}; Hls = make(exports) || exports.default || exports; } };
  context.self = context; context.window = context;
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), context);
  assert.equal(Hls.DefaultConfig.preferManagedMediaSource, false);
  // getMediaSource defaults independently in this older library. The actual
  // controller must honor the patched config, including MMS-only platforms.
  for (const [ordinaryAvailable, preferManaged] of [[true, false], [true, true], [false, false]]) {
    context.MediaSource = ordinaryAvailable ? MediaSource : undefined;
    const hls = new Hls({ autoStartLoad: false, enableWorker: false, preferManagedMediaSource: preferManaged });
    const controller = hls.bufferController, media = video();
    try {
      controller.onMediaAttaching(null, { media });
      const source = controller.mediaSource, managed = preferManaged || !ordinaryAvailable;
      assert.equal(source instanceof ManagedMediaSource, managed);
      if (managed) {
        source.emit('endstreaming'); assert.equal(hls.bufferingEnabled, false);
        media.seeking = true;
        media.emit('seeking'); assert.equal(hls.bufferingEnabled, true, 'seek resumes without startstreaming');
        source.emit('endstreaming'); assert.equal(hls.bufferingEnabled, true, 'endstreaming during seek cannot pause loading');
        media.seeking = false;
        source.emit('endstreaming'); assert.equal(hls.bufferingEnabled, false, 'normal MMS power management remains active');
        source.emit('startstreaming'); assert.equal(hls.bufferingEnabled, true);
      } else {
        assert.equal(media.listeners.get('seeking')?.size || 0, 0, 'ordinary MSE has no MMS workaround listener');
      }
      controller.onMediaDetaching(null, {});
      assert.equal(media.listeners.get('seeking')?.size || 0, 0, 'detach removes the workaround');
      hls.pauseBuffering(); media.emit('seeking'); assert.equal(hls.bufferingEnabled, false, 'old video cannot restart detached playback');
      const replacement = video(); controller.onMediaAttaching(null, { media: replacement });
      if (managed) { replacement.seeking = true; replacement.emit('seeking'); assert.equal(hls.bufferingEnabled, true); }
      controller.onMediaDetaching(null, {});
    } finally { hls.destroy(); }
  }
  console.log('PASS full HLS library: MSE preference, MMS-only fallback, seek recovery, endstreaming race and detach/rebind');
}
