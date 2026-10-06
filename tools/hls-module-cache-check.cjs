// The host's original AMD module IDs are deliberately preloaded with stale
// objects. Exercise the real transformed base player against the new dependency.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), assert = require('node:assert/strict');
const directory = process.argv[2];
if (!directory) throw Error('Usage: node tools/hls-module-cache-check.cjs <HostCheck-output-directory>');
async function check(version) {
  const oldBase = {}, oldHls = {};
  const cache = new Map([['modules/htmlvideoplayer/basehtmlplayer.js', oldBase], ['modules/hlsjs/hls.js', oldHls]]);
  let dependencies, Base;
  vm.runInNewContext(fs.readFileSync(path.join(directory, 'web-' + version + '.js'), 'utf8'), {
    define: deps => { dependencies = deps; }
  });
  const dependency = dependencies.find(x => x.includes('basehtmlplayer'));
  const baseId = path.posix.normalize('modules/htmlvideoplayer/' + dependency);
  assert.equal(baseId, 'modules/strmbridge/basehtmlplayer-hls-v1.js');
  assert.equal(cache.has(baseId), false);
  const requested = [];
  class Hls {
    static Events = { MANIFEST_PARSED: 'manifest', ERROR: 'error' };
    constructor() { this.handlers = new Map(); }
    on(event, fn) { this.handlers.set(event, fn); }
    loadSource() {}
    attachMedia() {}
  }
  const context = { console: { log() {} }, window: {}, Emby: { importModule: async id => {
    const key = id.replace(/^\.\//, ''); requested.push(key);
    if (cache.has(key)) return cache.get(key);
    assert.equal(key, 'modules/strmbridge/hls-seek-v1.js');
    cache.set(key, Hls); context.Hls = Hls; return Hls;
  } }, define: (deps, make) => {
    const exports = {};
    make(...deps.map(d => d === 'exports' ? exports : { default: {} })); Base = exports.default;
  } };
  const baseSource = fs.readFileSync(path.join(directory, 'base-' + version + '.js'), 'utf8');
  vm.runInNewContext(baseSource, context); cache.set(baseId, Base);
  const instance = new Base();
  const video = { play: () => Promise.resolve() };
  const result = instance.setSrcWithHlsJs(video, { mediaType: 'Video' }, 'fixture.m3u8', new AbortController().signal);
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(instance._hlsPlayer instanceof Hls);
  instance._hlsPlayer.handlers.get('manifest')();
  assert.equal((await result).autoplayed, true);
  assert.deepEqual(requested, ['modules/strmbridge/hls-seek-v1.js']);
  assert.equal(cache.get('modules/htmlvideoplayer/basehtmlplayer.js'), oldBase);
  assert.equal(cache.get('modules/hlsjs/hls.js'), oldHls);
  console.log('PASS ' + version + ': actual player dependency/startup bypasses cached host base and HLS modules');
}
(async () => { for (const version of ['4.9.5.0', '4.10.0.40']) await check(version); })().catch(e => { console.error(e); process.exitCode = 1; });
