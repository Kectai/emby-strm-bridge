define([], function () {
  'use strict';
  var limit = 16 * 1024 * 1024;

  // Max multiplicity preserves intentional identical events while removing overlap
  // introduced by preroll in adjacent windows. No text/time-only event collapsing.
  function merge(windows) {
    var header = '', counts = Object.create(null), lines = [];
    windows.forEach(function (window) {
      if (!header && window.header) header = window.header;
      var seen = Object.create(null);
      window.events.forEach(function (line) {
        var n = seen[line] = (seen[line] || 0) + 1;
        if (n > (counts[line] || 0)) { lines.push(line); counts[line] = n; }
      });
    });
    return { header: header, events: lines, text: header + lines.join('\n') + '\n' };
  }
  function parse(text) {
    var header = [], events = [], inEvents = false;
    text.split(/\r?\n/).forEach(function (line) {
      if (line.indexOf('[Events]') === 0) inEvents = true;
      if (/^Dialogue:\s/.test(line)) events.push(line);
      else if (!inEvents || /^\[Events\]|^Format:/.test(line)) header.push(line);
    });
    return { header: header.join('\n') + '\n', events: events };
  }
  function activeEvent(line, time) {
    var match = /^Dialogue:\s*[^,]*,(\d+):(\d+):(\d+(?:\.\d+)?),(\d+):(\d+):(\d+(?:\.\d+)?),/.exec(line);
    if (!match) return false;
    var start = Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]);
    var end = Number(match[4]) * 3600 + Number(match[5]) * 60 + Number(match[6]);
    return start <= time && end > time;
  }
  function preserveFrameOrder(renderer) {
    // The shipped renderer rejects equal Date.now() timestamps. With synchronous
    // WASM blending, worker messages are ordered: a later same-ms cue frame must
    // replace an earlier blank frame. Keep rejecting strictly older timestamps.
    if (!renderer.worker || typeof renderer.onWorkerMessage !== 'function') return;
    var handler = renderer.onWorkerMessage;
    renderer.worker.removeEventListener('message', handler);
    renderer.onWorkerMessage = function (event) {
      var data = event.data;
      if (data && data.target === 'canvas' && data.op === 'renderCanvas' &&
          Number.isFinite(data.time) && data.time === renderer.lastRenderTime) renderer.lastRenderTime = data.time - 0.5;
      return handler.call(renderer, event);
    };
    renderer.worker.addEventListener('message', renderer.onWorkerMessage);
  }
  function mseClock(instance, time) {
    var hls = instance._hlsPlayer;
    if (!hls) return null;
    var controller = hls.streamController, frag = controller && controller.fragPlaying;
    function contains(value) {
      return value && Number.isFinite(value.start) && Number.isFinite(value.duration) &&
        time >= value.start && time < value.start + value.duration;
    }
    // A seek can leave fragPlaying pointing at the previous picture. Never use
    // that fragment's continuity counter for the new presentation interval.
    if (!contains(frag)) {
      var details = hls.latestLevelDetails;
      frag = details && details.fragments && details.fragments.find(contains);
    }
    var pts = frag && controller && controller.initPTS && controller.initPTS[frag.cc];
    if (!pts || !Number.isFinite(pts.baseTime) || !Number.isFinite(pts.timescale) || pts.timescale <= 0) return undefined;
    var offset = ((instance._currentPlayOptions || {}).transcodingOffsetTicks || 0) - pts.baseTime / pts.timescale * 10000000;
    return Number.isSafeInteger(Math.round(offset)) ? Math.round(offset) : undefined;
  }
  function ownClock(renderer) {
    // Octopus' video listeners and Emby's onTimeUpdate otherwise write a second
    // clock between our updates (including stale events during seeking).
    var writing = false, methods = {};
    ['setCurrentTime', 'setIsPaused', 'setRate'].forEach(function (name) {
      methods[name] = renderer[name];
      renderer[name] = function () { if (writing) return methods[name].apply(renderer, arguments); };
    });
    return function (paused, time, rate) {
      writing = true;
      try {
        if (rate !== null) renderer.setRate(rate);
        renderer.setIsPaused(paused, time);
      } finally { writing = false; }
    };
  }
  // External ASS content and fonts remain owned by the host. Correct only its
  // renderer clock, using this video's observed MSE origin and server mux map.
  function bindExternalClock(options) {
    var instance = options.instance, video = options.video, renderer = instance.currentSubtitlesOctopus;
    if (!instance._hlsPlayer || !renderer || !options.track.IsExternal) return;
    // The verified server mapping is for MPEG-TS. Leave fMP4 and unknown
    // transports on the host clock instead of hiding an unsupported subtitle.
    try {
      var containerType = new URL((instance._currentPlayOptions || {}).url, document.baseURI).searchParams.get('SegmentContainer');
      if (!containerType || containerType.toLowerCase() !== 'ts') return;
    } catch (_) { return; }
    if (instance.strmBridgeExternalClock) instance.strmBridgeExternalClock.dispose();
    var original = {}, closed = false, pending, timeout, animation, timer, key, offset, attempts = 0, retryAt = 0;
    var revision = 0, rate, lastTime = 0, clockWaitAt;
    var container = instance.videoSubtitlesElem, visibility = container && container.style.visibility;
    ['setCurrentTime', 'setIsPaused', 'setRate'].forEach(function (name) { original[name] = renderer[name]; });
    preserveFrameOrder(renderer);
    var write = ownClock(renderer);
    function active() { return !closed && instance._strmBridgeEpoch === options.epoch && instance.currentSubtitlesOctopus === renderer; }
    function dispose(restorePlayback) {
      if (closed) return;
      var resume = restorePlayback === true && active();
      closed = true; revision++;
      if (pending) pending.abort();
      clearTimeout(timeout); clearInterval(timer);
      if (animation !== undefined) window.cancelAnimationFrame(animation);
      ['setCurrentTime', 'setIsPaused', 'setRate'].forEach(function (name) { renderer[name] = original[name]; });
      renderer.timeOffset = -(instance._currentSubtitleOffset || 0) / 1000;
      if (container) container.style.visibility = visibility;
      if (instance.strmBridgeExternalClock === handle) instance.strmBridgeExternalClock = null;
      events.forEach(function (name) { video.removeEventListener(name, tick); });
      // A live fallback must undo our paused worker, even if the video's playing
      // event already fired during calibration. Teardown must never revive it.
      if (resume) {
        renderer.setRate(video.playbackRate || 1);
        renderer.setIsPaused(!!video.paused || video.seeking || video.readyState < 3,
          (video.currentTime || 0) + renderer.timeOffset);
      }
    }
    function request(clock, session) {
      var current = revision, control = new AbortController(); pending = control; attempts++;
      timeout = setTimeout(function () { control.abort(); }, 5000);
      var headers = { 'Content-Type': 'application/json' }, token = options.api.accessToken();
      if (token) headers['X-Emby-Token'] = token;
      fetch(options.api.getUrl('StrmBridge/Subtitles/Clock'), { method: 'POST', credentials: 'same-origin', headers: headers, signal: control.signal,
        body: JSON.stringify({ Id: options.item.Id, MediaSourceId: options.source.Id, Index: options.track.Index,
          PlaySessionId: session, NativeHlsClock: false, MseTimestampOffsetTicks: clock }) })
        .then(async function (response) {
          if (!active() || current !== revision || control.signal.aborted) return;
          if (response.status === 422) { dispose(true); return; }
          if (!response.ok) { if (response.status !== 503) attempts = 3; return; }
          var data = await response.json();
          if (!active() || current !== revision || control.signal.aborted) return;
          if (!Number.isSafeInteger(data.TimelineOffsetTicks) || Math.abs(data.TimelineOffsetTicks) > 1200000000) { attempts = 3; return; }
          offset = data.TimelineOffsetTicks;
          var cache = instance._strmBridgeExternalClockCache;
          cache.offsets.set(clock, offset);
          if (cache.offsets.size > 16) cache.offsets.delete(cache.offsets.keys().next().value);
        }).catch(function () {}).finally(function () {
          if (current !== revision) return;
          clearTimeout(timeout); pending = null; retryAt = Date.now() + attempts * 500;
          if (active()) {
            if (offset === undefined && attempts >= 3) { dispose(true); return; }
            tick();
          }
        });
    }
    function tick() {
      if (!active()) { dispose(); return; }
      var play = instance._currentPlayOptions || {}, session;
      try { session = new URL(play.url, document.baseURI).searchParams.get('PlaySessionId'); } catch (_) {}
      if (!instance._hlsPlayer || !session) { dispose(true); return; }
      var clock = video.seeking ? undefined : mseClock(instance, video.currentTime || 0);
      if (clock === null || clock === undefined) {
        if (!video.seeking && video.readyState >= 3) {
          if (clockWaitAt === undefined) clockWaitAt = Date.now();
          if (Date.now() - clockWaitAt >= 5000) { dispose(true); return; }
        } else clockWaitAt = undefined;
      } else clockWaitAt = undefined;
      if (clock !== null && clock !== undefined) {
        var identity = JSON.stringify([options.item.Id, options.source.Id, session]);
        var cache = instance._strmBridgeExternalClockCache;
        if (!cache || cache.identity !== identity) cache = instance._strmBridgeExternalClockCache = { identity: identity, offsets: new Map() };
        var nextKey = identity + ':' + clock;
        if (key !== nextKey) {
          revision++; if (pending) pending.abort(); clearTimeout(timeout); pending = null;
          key = nextKey; offset = cache.offsets.get(clock); attempts = 0; retryAt = 0;
        }
        if (offset === undefined && !pending && attempts < 3 && Date.now() >= retryAt) request(clock, session);
      }
      var ready = clock !== null && clock !== undefined && offset !== undefined && !video.seeking;
      if (container) container.style.visibility = ready ? visibility : 'hidden';
      var changedRate = rate !== video.playbackRate ? video.playbackRate || 1 : null; rate = video.playbackRate;
      if (ready) {
        renderer.timeOffset = (play.transcodingOffsetTicks || 0) / 10000000 - offset / 10000000 - (instance._currentSubtitleOffset || 0) / 1000;
        lastTime = (video.currentTime || 0) + renderer.timeOffset;
      }
      // Block both the host's timeupdate and Octopus' independent video listeners.
      write(!ready || !!video.paused || video.readyState < 3, lastTime, changedRate);
    }
    function frame() { animation = undefined; if (!active()) { dispose(); return; } tick(); if (!closed) animation = window.requestAnimationFrame(frame); }
    var handle = { dispose: dispose }, events = ['timeupdate', 'seeking', 'seeked', 'playing', 'pause', 'waiting', 'ratechange'];
    instance.strmBridgeExternalClock = handle;
    events.forEach(function (name) { video.addEventListener(name, tick); });
    tick();
    if (!closed) {
      if (typeof window.requestAnimationFrame === 'function' && typeof window.cancelAnimationFrame === 'function') animation = window.requestAnimationFrame(frame);
      else timer = setInterval(tick, 50);
    }
    return handle;
  }
  function render(options) {
    var instance = options.instance, video = options.video, api = options.api;
    if (!instance._hlsPlayer) return Promise.resolve().then(function () {
      if (instance._strmBridgeEpoch === options.epoch) return options.fallback();
    });
    var closed = false, session, renderer, container, timer, resizeObserver;
    var paintTimer = null, seekTimer = null, frameCallback = null, rendererRate = null, writeClock;
    var animationPaint = typeof window.requestAnimationFrame === 'function' && typeof window.cancelAnimationFrame === 'function';
    var waiting = video.readyState !== undefined && video.readyState < 3;
    var windows = new Map(), pending = new Map(), retryAt = new Map(), retryCounts = new Map(), generation = 0;
    var renderingFailed = false, createControl = new AbortController();
    var carry = { header: '', events: [] };
    var lastHeader = '', rendered = [], dirty = false, currentWindow = -1, windowClock;
    var active = function () { return !closed && instance._strmBridgeEpoch === options.epoch; };
    function apiUrl(path) { return api.getUrl('StrmBridge/Subtitles/' + path); }
    function fetchOptions(method, signal, data) {
      return { method: method, credentials: 'same-origin', signal: signal,
        headers: { 'X-Emby-Token': api.accessToken(), 'Content-Type': 'application/json' },
        body: data ? JSON.stringify(data) : undefined };
    }
    function playbackBinding() {
      var play = instance._currentPlayOptions || {};
      try {
        var url = new URL(play.url || video.currentSrc || video.src, document.baseURI), value = { session: '', start: 0, direct: /\/StrmBridge\/Playback\/v3\/[^/]+\/stream\.mkv$/i.test(url.pathname) };
        url.searchParams.forEach(function (v, key) { if (key.toLowerCase() === 'playsessionid') value.session = v; if (key.toLowerCase() === 'starttimeticks') value.start = Number(v) || 0; });
        return value;
      } catch (_) { return { session: '', start: 0, direct: false }; }
    }
    function retryDelay(milliseconds, signal) {
      return new Promise(function (resolve, reject) {
        var timer;
        function abort() { clearTimeout(timer); signal.removeEventListener('abort', abort); reject(new Error('cancelled')); }
        if (signal.aborted) return abort();
        signal.addEventListener('abort', abort);
        timer = setTimeout(function () { signal.removeEventListener('abort', abort); resolve(); }, milliseconds);
      });
    }
    async function createSession(signal) {
      signal = signal || createControl.signal;
      var binding = playbackBinding();
      for (var attempt = 0; attempt < 3; attempt++) {
        var response = await fetch(apiUrl('Sessions'), fetchOptions('POST', signal,
          { Id: options.item.Id, MediaSourceId: options.source.Id, Index: options.track.Index, PlaySessionId: binding.session, VideoStartTicks: binding.start, NativeHlsClock: false }));
        // A gateway file URL has no server video job to wait for. Still ask the
        // authenticated endpoint once so outside-scope sources retain native handling.
        if (response.status !== 503 || binding.direct || attempt === 2) return response;
        await retryDelay(500 * (attempt + 1), signal);
      }
    }
    function destroySession(id) {
      return fetch(apiUrl('Sessions/' + encodeURIComponent(id)), fetchOptions('DELETE')).catch(function () {});
    }
    function mediaTime() {
      var play = instance._currentPlayOptions || {};
      return Math.max(0, (video.currentTime || 0) + (play.transcodingOffsetTicks || 0) / 10000000);
    }
    function updateClock() {
      if (renderer) renderer.timeOffset = ((instance._currentPlayOptions || {}).transcodingOffsetTicks || 0) / 10000000 - (instance._currentSubtitleOffset || 0) / 1000;
    }
    function syncRenderer() {
      if (!active() || !renderer || renderingFailed) return;
      updateClock();
      if (container) container.style.visibility = video.seeking ? 'hidden' : '';
      var rate = video.playbackRate || 1;
      var changedRate = rendererRate !== rate ? rate : null; rendererRate = rate;
      // currentTime is the browser's audio/video presentation clock. Frame
      // metadata is a frame PTS, which can lag it during decode/seek; alternating
      // the two makes subtitles speed up and slow down while video stays normal.
      if (writeClock) writeClock(!!(video.paused || video.seeking || waiting), (video.currentTime || 0) + renderer.timeOffset, changedRate);
    }
    function requestFrame() {
      if (!active() || renderingFailed || frameCallback !== null || typeof video.requestVideoFrameCallback !== 'function') return;
      frameCallback = video.requestVideoFrameCallback(function (_, metadata) {
        frameCallback = null;
        if (!active()) return;
        if (!video.seeking) syncRenderer();
        requestFrame();
      });
    }
    function playing() { waiting = false; syncRenderer(); requestFrame(); tick(); }
    function buffering() { waiting = true; syncRenderer(); }
    function seeked() { waiting = video.readyState !== undefined && video.readyState < 3; syncRenderer(); requestFrame(); tick(); }
    function rateChanged() { syncRenderer(); }
    function diagnostic(stage, error) {
      // Fixed reason codes only: no exception objects, URLs, response text or cues.
      var reason = error && error.message;
      if (!/^(direct-play-unavailable|session-unavailable|stream-unavailable|output-budget|invalid-subtitle|renderer-failed|subtitle-http-[1-5][0-9]{2})$/.test(reason || '')) reason = 'unavailable';
      if (typeof console !== 'undefined' && typeof console.debug === 'function')
        console.debug('STRM_BRIDGE_SUBTITLE_CLIENT stage=' + stage + ' reason=' + reason);
    }
    function cancelPaint() {
      if (paintTimer !== null) {
        if (animationPaint) window.cancelAnimationFrame(paintTimer); else clearTimeout(paintTimer);
        paintTimer = null;
      }
    }
    function dispose(releaseSelection) {
      if (closed) return;
      closed = true; generation++; createControl.abort();
      clearInterval(timer); cancelPaint(); clearTimeout(seekTimer);
      if (frameCallback !== null && typeof video.cancelVideoFrameCallback === 'function') video.cancelVideoFrameCallback(frameCallback);
      frameCallback = null;
      pending.forEach(function (work) { work.abort(); }); pending.clear();
      video.removeEventListener('seeking', seeking);
      video.removeEventListener('pause', pause);
      video.removeEventListener('playing', playing);
      video.removeEventListener('waiting', buffering); video.removeEventListener('seeked', seeked); video.removeEventListener('ratechange', rateChanged);
      video.removeEventListener('ended', dispose);
      if (resizeObserver) resizeObserver.disconnect();
      if (renderer) { if (renderer.worker !== null) renderer.dispose(); if (instance.currentSubtitlesOctopus === renderer) instance.currentSubtitlesOctopus = null; }
      if (container) { container.remove(); if (instance.videoSubtitlesElem === container) instance.videoSubtitlesElem = null; }
      windows.clear(); carry = { header: "", events: [] };
      if (session) destroySession(session);
      if (instance.strmBridgeSubtitle === handle) {
        instance.strmBridgeSubtitle = null;
        if (releaseSelection !== false) instance.customTrackIndex = -1;
      }
    }
    var handle = { dispose: dispose };
    instance.strmBridgeSubtitle = handle;
    function paint(reset) {
      if (!active() || !renderer || renderingFailed) return;
      cancelPaint();
      var selected = Array.from(windows.entries()).filter(function (pair) { return pair[0] >= currentWindow - 1 && pair[0] <= currentWindow + 1; });
      selected.sort(function (a, b) { return a[0] - b[0]; });
      var result = merge([carry].concat(selected.map(function (p) { return p[1]; })));
      if (result.header.indexOf('[Events]') < 0) {
        if (reset && lastHeader) { renderer.setTrack(lastHeader); rendered = []; }
        return;
      }
      var prefix = !reset && result.header === lastHeader && rendered.length <= result.events.length &&
        rendered.every(function (event, i) { return result.events[i] === event; });
      if (prefix && rendered.length === result.events.length) {
        dirty = false; syncRenderer(); return;
      }
      if (prefix && renderer.addToTrack) {
        var added = result.events.slice(rendered.length);
        if (added.length) renderer.addToTrack(added.join('\n') + '\n', false);
      } else if (dirty || reset || lastHeader !== result.header) renderer.setTrack(result.text);
      lastHeader = result.header; rendered = result.events; dirty = false;
      syncRenderer();
    }
    function schedulePaint() {
      dirty = true;
      if (paintTimer === null) {
        var flush = function () { paintTimer = null; if (active()) paint(false); };
        paintTimer = animationPaint ? window.requestAnimationFrame(flush) : setTimeout(flush, 0);
      }
    }
    function prune() {
      var total = 0, position = mediaTime(), retained = [{ header: carry.header, events: carry.events.filter(function (line) { return activeEvent(line, position); }) }];
      retryAt.forEach(function (_, key) { if (Math.abs(key - currentWindow) > 1) { retryAt.delete(key); retryCounts.delete(key); } });
      windows.forEach(function (w, key) {
        if (key < currentWindow - 1 || key > currentWindow + 1) {
          if (key < currentWindow - 1) retained.push({ header: w.header, events: w.events.filter(function (line) { return activeEvent(line, position); }) });
          windows.delete(key);
        } else total += w.bytes;
      });
      carry = merge(retained);
      // Retain already-read long events until their original end, even when their window expires.
      total += carry.text.length * 4;
      if (total > limit) throw new Error('output-budget');
    }
    function deferRetry(index) {
      var count = (retryCounts.get(index) || 0) + 1;
      retryCounts.set(index, count);
      retryAt.set(index, Date.now() + Math.min(8000, 500 * Math.pow(2, Math.min(count, 4))));
    }
    async function loadWindow(index) {
      var cached = windows.get(index), position = mediaTime(), start = Math.max(index * 60, Math.floor(position));
      if (!active() || !session || pending.size || Date.now() < (retryAt.get(index) || 0) ||
          (cached && cached.done && cached.from <= start)) return;
      var mseOffset = mseClock(instance, video.currentTime || 0);
      if (mseOffset === undefined) return; // Wait for this continuity's actual initPTS.
      var control = new AbortController(), revision = generation;
      pending.set(index, control);
      try {
        // Request the current display interval from the video's local subtitle
        // output. This does not seek or reopen the remote media input.
        var path = 'Sessions/' + encodeURIComponent(session) + '/Stream?StartPositionTicks=' + Math.round(start * 10000000) + '&EndPositionTicks=' + Math.round((index + 1) * 60 * 10000000);
        if (mseOffset !== null) path += '&MseTimestampOffsetTicks=' + mseOffset;
        var response, renewed = false;
        for (var attempt = 0; attempt < 3; attempt++) {
          response = await fetch(apiUrl(path), fetchOptions('GET', control.signal));
          if (!active() || revision !== generation || control.signal.aborted) return;
          // A long pause may outlive the idle session. Renew once without user preparation.
          if (response.status === 404 && !renewed && active() && revision === generation) {
            renewed = true;
            var oldSession = session, fresh = await createSession(control.signal);
            if (!fresh.ok) throw new Error('subtitle-http-' + fresh.status);
            var info = await fresh.json();
            if (!active() || revision !== generation || control.signal.aborted) { destroySession(info.SessionId); return; }
            session = info.SessionId; destroySession(oldSession);
            windows.clear(); carry = { header: "", events: [] }; rendered = []; if (renderer && lastHeader) renderer.setTrack(lastHeader);
            path = path.replace(encodeURIComponent(oldSession), encodeURIComponent(session));
            response = await fetch(apiUrl(path), fetchOptions('GET', control.signal));
            if (!active() || revision !== generation || control.signal.aborted) return;
          }
          if (response.status !== 503 || attempt === 2) break;
          await new Promise(function (resolve) { setTimeout(resolve, 500 * (attempt + 1)); });
          if (control.signal.aborted) return;
        }
        if (!response.ok) throw new Error('subtitle-http-' + response.status);
        var partialWindow = response.headers && response.headers.get('X-StrmBridge-Subtitle-Window') === 'partial';
        if (!response.body || !response.body.getReader) throw new Error('stream-unavailable');
        var reader = response.body.getReader(), decoder = new TextDecoder('utf-8', { fatal: true }), text = '', tail = '', bytes = 0;
        while (true) {
          var chunk = await reader.read();
          if (!active() || revision !== generation || control.signal.aborted) return;
          if (chunk.value) bytes += chunk.value.length;
          if (bytes > limit) throw new Error('output-budget');
          try { tail += decoder.decode(chunk.value, { stream: !chunk.done }); }
          catch (_) { throw new Error('invalid-subtitle'); }
          var cut = chunk.done ? tail.length : tail.lastIndexOf('\n') + 1;
          if (cut) {
            text += tail.slice(0, cut); tail = tail.slice(cut);
            var parsed = parse(text), previous = windows.get(index);
            // Same source and presentation origin: retain already delivered cues
            // across a seek instead of recreating libass' track/font state.
            if (mseOffset !== null && previous) parsed = merge([previous, parsed]);
            parsed.bytes = Math.max(bytes, (parsed.text || text).length * 4); parsed.done = chunk.done && !partialWindow; parsed.from = start;
            windows.set(index, parsed); prune(); schedulePaint();
          }
          if (chunk.done) {
            var finished = windows.get(index);
            if (!finished || finished.header.indexOf('[Events]') < 0) throw new Error('invalid-subtitle');
            finished.done = !partialWindow;
            if (partialWindow) deferRetry(index);
            else { retryAt.delete(index); retryCounts.delete(index); }
            paint(false); break;
          }
        }
      } catch (error) {
        if (active() && revision === generation && !control.signal.aborted) {
          var terminal = /^(output-budget|invalid-subtitle|subtitle-http-(?:400|401|403|404|409|422))$/.test(error.message || '');
          diagnostic('stream', error);
          if (terminal) { renderingFailed = true; dispose(); }
          else deferRetry(index);
        }
      } finally {
        control.abort();
        if (pending.get(index) === control) pending.delete(index);
        if (active() && revision !== generation && !video.paused && !video.seeking) setTimeout(tick, 0);
      }
    }
    function resetWindows() {
      generation++; retryAt.clear(); retryCounts.clear();
      windows.clear(); carry = { header: '', events: [] };
      pending.forEach(function (work) { work.abort(); });
      currentWindow = Math.floor(mediaTime() / 60); prune(); dirty = true; paint(true);
    }
    function tick() {
      if (!active()) { dispose(); return; }
      syncRenderer();
      var clock = mseClock(instance, video.currentTime || 0);
      if (clock !== undefined && clock !== windowClock) {
        if (windowClock !== undefined) resetWindows();
        windowClock = clock;
      }
      var position = mediaTime(), index = Math.floor(position / 60);
      if (index !== currentWindow) {
        currentWindow = index;
        // Normal playback can cross a boundary while the previous extraction is
        // still reading. Release obsolete work before acquiring the sole reader.
        // Keep its pending entry until finally settles, including abort cleanup.
        pending.forEach(function (work, key) { if (key !== index) work.abort(); });
        prune(); paint(true);
      }
      if (renderingFailed || video.paused || video.seeking) return;
      loadWindow(index);
      // Prefetch only after the current local window has completed.
      if ((windows.get(index) || {}).done && position >= (index + 1) * 60 - 15) loadWindow(index + 1);
    }
    function seeking() {
      waiting = true; syncRenderer();
      // Valid MSE cues retain their source timestamps across server restarts.
      // Keep the loaded track when the browser still uses the same origin;
      // recreating it at every seek needlessly rebuilds libass' font caches.
      var clock = mseClock(instance, video.currentTime || 0);
      if (clock !== null && clock !== undefined && clock === windowClock) {
        generation++; retryAt.clear(); retryCounts.clear(); pending.forEach(function (work) { work.abort(); });
      } else resetWindows();
      // Native video seeking emits frequently while scrubbing. Only the last timer reads.
      clearTimeout(seekTimer); seekTimer = setTimeout(function () { seekTimer = null; tick(); }, 250);
    }
    function pause() {
      syncRenderer();
      generation++; pending.forEach(function (work) { work.abort(); });
    }
    async function start() {
      // Loading the renderer assets can overlap the server's video startup.
      // Catch here to avoid an unhandled rejection if session creation fails.
      var module = Emby.importModule('./bower_components/javascriptsubtitlesoctopus/dist/subtitles-octopus.js')
        .then(function (value) { return { value: value }; }, function () { return { error: true }; });
      var response = await createSession();
      if (response.status === 422) {
        var rejected = await response.json();
        if (rejected.ReasonCode === 'outside-scope') { dispose(false); return options.fallback(); }
      }
      if (!response.ok) throw new Error(response.status === 503 && playbackBinding().direct ? 'direct-play-unavailable' : 'subtitle-http-' + response.status);
      var info = await response.json(); session = info.SessionId;
      if (!active()) { destroySession(session); return; }
      // Fetch available local cues while WASM/font assets initialize, rather
      // than serializing that work behind renderer construction.
      currentWindow = Math.floor(mediaTime() / 60);
      windowClock = mseClock(instance, video.currentTime || 0);
      loadWindow(currentWindow);
      var loaded = await module;
      if (loaded.error) throw new Error('renderer-failed');
      var Octopus = loaded.value;
      if (!active()) { dispose(); return; }
      container = document.createElement('div');
      container.className = 'videoSubtitles htmlvideo-subtitles-canvas-parent flex align-items-flex-start justify-content-center';
      video.parentNode.appendChild(container); instance.videoSubtitlesElem = container;
      var canvas = document.createElement('canvas'); canvas.className = 'htmlvideo-subtitles-canvas'; container.appendChild(canvas);
      var fonts = (options.source.MediaStreams || []).filter(function (s) { return s.Type === 'Attachment' && s.DeliveryUrl && /^(ttf|otf|)$/i.test(s.Codec || ''); })
        .map(function (s) { return api.serverAddress() + s.DeliveryUrl; });
      // Resolve against the real Web root, not the current hash route or API base.
      var webRoot = document.baseURI.split('#')[0].replace(/\/[^/]*$/, '/');
      renderer = new Octopus({ video: video, canvas: canvas, canvasParent: container,
        subContent: '[Script Info]\nScriptType: v4.00+\n[Events]\n',
        workerUrl: webRoot + 'bower_components/javascriptsubtitlesoctopus/dist/subtitles-octopus-worker.js',
        legacyWorkerUrl: webRoot + 'bower_components/javascriptsubtitlesoctopus/dist/subtitles-octopus-worker-legacy.js',
        fallbackFont: webRoot + 'modules/fonts/GoNotoKurrent.woff2', fonts: fonts,
        onReady: function () { if (active()) { rendererRate = null; syncRenderer(); requestFrame(); } },
        onError: function () {
          if (!active() || renderingFailed) return;
          renderingFailed = true; pause(); clearInterval(timer); cancelPaint();
          if (frameCallback !== null && typeof video.cancelVideoFrameCallback === 'function') video.cancelVideoFrameCallback(frameCallback);
          frameCallback = null; diagnostic('renderer', new Error('renderer-failed'));
          // Octopus workerError destroys its worker immediately after this
          // callback. Release the adapter on the next task to avoid destroying
          // it twice, and let the user select this same subtitle again.
          setTimeout(function () { if (active() && instance.strmBridgeSubtitle === handle) dispose(); }, 0);
        },
        lossyRender: false, renderMode: 'wasm-blend' });
      writeClock = ownClock(renderer);
      preserveFrameOrder(renderer);
      instance.currentSubtitlesOctopus = renderer;
      instance.enableSubtitlePositionFromSettings = false;
      if (window.ResizeObserver) {
        // ResizeObserver emits an initial notification even at the current size.
        // Octopus resets canvas.width on resize, clearing a just-rendered cue;
        // libass may then report no image change until the next subtitle event.
        var observedWidth = video.offsetWidth, observedHeight = video.offsetHeight;
        resizeObserver = new ResizeObserver(function () {
          if (!active() || !renderer.resize || (video.offsetWidth === observedWidth && video.offsetHeight === observedHeight)) return;
          observedWidth = video.offsetWidth; observedHeight = video.offsetHeight; renderer.resize();
        });
        resizeObserver.observe(video);
      }
      // Playback may have become ready while session/module initialization was
      // awaiting. Its playing event predates our listeners; sample the current
      // state instead of retaining the entry-time buffering flag.
      waiting = video.readyState !== undefined && video.readyState < 3;
      paint(false); syncRenderer(); requestFrame();
      video.addEventListener('seeking', seeking); video.addEventListener('pause', pause);
      video.addEventListener('playing', playing); video.addEventListener('ended', dispose);
      video.addEventListener('waiting', buffering); video.addEventListener('seeked', seeked); video.addEventListener('ratechange', rateChanged);
      timer = setInterval(tick, 500); tick();
    }
    return start().catch(function (error) { if (active()) { diagnostic('start', error); dispose(); } });
  }
  return { render: render, bindExternalClock: bindExternalClock, mseClock: mseClock, ownClock: ownClock, preserveFrameOrder: preserveFrameOrder, parse: parse, merge: merge, activeEvent: activeEvent };
});
