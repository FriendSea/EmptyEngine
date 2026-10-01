// Web Audio の登録・再生を C# へ公開する静的 P/Invoke モジュール。
#include <emscripten.h>

EM_JS(void, ee_audio_setup, (), {
    if (Module.__ee_audio) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    var g = Module.__ee_audio = {
        ctx: AC ? new AC() : null,
        buffers: {},
        pending: {},
        failed: {},
        voices: {},
        nextVoice: 0,
        plays: {},
        nextPlay: 0,
        resume: function () {
            if (!this.ctx || this.ctx.state !== 'suspended') return;
            var resumed = this.ctx.resume();
            if (resumed && resumed.catch) resumed.catch(function () { });
        },
        playDecoded: function (id, playId, volume) {
            var buf = this.buffers[id];
            var p = this.plays[playId];
            if (!buf || !p || !this.ctx) return;
            var src = this.ctx.createBufferSource();
            src.buffer = buf;
            var gain = this.ctx.createGain();
            gain.gain.value = volume;
            src.connect(gain);
            gain.connect(this.ctx.destination);
            var plays = this.plays;
            src.onended = function () {
                if (plays[playId] === p) delete plays[playId];
                gain.disconnect();
            };
            p.src = src;
            p.startAt = this.ctx.currentTime;
            src.start();
        },
        startVoice: function (id, startSeconds, loop, volume) {
            var m = this.voices[id];
            if (!m || !m.ready || !this.ctx) return;
            m.sources.forEach(function (src) {
                try { src.stop(); } catch (e) { }
            });
            m.sources = [];
            m.layerGains = [];
            m.ended = false;
            m.loop = loop;
            m.gain.gain.value = volume;
            var duration = m.layers[0].duration;
            var offset = Math.max(0, Math.min(startSeconds, duration));
            var loopStart = m.loopStart;
            var loopEnd = m.loopEnd > m.loopStart ? m.loopEnd : duration;
            if (loop && loopEnd > loopStart && offset >= loopEnd)
                offset = loopStart + ((offset - loopStart) % (loopEnd - loopStart));
            var at = this.ctx.currentTime + 0.05;
            for (var i = 0; i < m.layers.length; i++) {
                var layerGain = this.ctx.createGain();
                layerGain.gain.value = m.layerVolumes[i] || 0;
                layerGain.connect(m.gain);
                var src = this.ctx.createBufferSource();
                src.buffer = m.layers[i];
                src.connect(layerGain);
                if (loop && loopEnd > loopStart) {
                    src.loop = true;
                    src.loopStart = loopStart;
                    src.loopEnd = loopEnd;
                }
                src.onended = function () {
                    if (!m.loop) m.ended = true;
                };
                src.start(at, offset);
                m.layerGains.push(layerGain);
                m.sources.push(src);
            }
            m.startAt = at;
            m.startOffset = offset;
            m.playing = true;
        }
    };
    var unlock = function () { g.resume(); };
    ['pointerdown', 'keydown', 'touchstart'].forEach(function (name) {
        window.addEventListener(name, unlock, true);
    });
});

EM_JS(void, ee_audio_register, (int id, const unsigned char* data, int len), {
    var g = Module.__ee_audio;
    if (!g || !g.ctx) return;
    var bytes = HEAPU8.slice(data, data + len);
    var failed = function(e) {
        g.failed[id] = true;
        (g.pending[id] || []).forEach(function (q) { delete g.plays[q.playId]; });
        delete g.pending[id];
        if (typeof console !== 'undefined' && console.warn)
            console.warn('EmptyEngine: failed to decode audio clip ' + id, e);
    };
    try {
        g.ctx.decodeAudioData(bytes.buffer, function (buf) {
            g.buffers[id] = buf;
            var queued = g.pending[id] || [];
            delete g.pending[id];
            for (var i = 0; i < queued.length; i++)
                g.playDecoded(id, queued[i].playId, queued[i].volume);
        }, failed);
    } catch (e) {
        failed(e);
    }
});

// 再生番号を返す。デコード待ちの間も番号は生きていて、止めればデコード後に鳴らない
EM_JS(int, ee_audio_play, (int id, float volume), {
    var g = Module.__ee_audio;
    if (!g || !g.ctx) return -1;
    g.resume();
    if (g.failed[id]) return -1;
    var playId = g.nextPlay++;
    g.plays[playId] = { src: null, startAt: 0 };
    if (!g.buffers[id]) {
        (g.pending[id] = g.pending[id] || []).push({ playId: playId, volume: volume });
        return playId;
    }
    g.playDecoded(id, playId, volume);
    return playId;
});

EM_JS(int, ee_audio_playback_is_playing, (int playId), {
    var g = Module.__ee_audio;
    return g && g.plays[playId] ? 1 : 0;
});

EM_JS(double, ee_audio_playback_time, (int playId), {
    var g = Module.__ee_audio;
    var p = g && g.plays[playId];
    if (!p || !p.src || !g.ctx) return 0;
    return Math.max(0, g.ctx.currentTime - p.startAt);
});

EM_JS(void, ee_audio_playback_stop, (int playId), {
    var g = Module.__ee_audio;
    var p = g && g.plays[playId];
    if (!p) return;
    delete g.plays[playId];
    if (p.src) {
        try { p.src.stop(); } catch (e) { }
    }
});

EM_JS(int, ee_audio_voice_create, (int layerCount), {
    var g = Module.__ee_audio;
    if (!g || !g.ctx || layerCount <= 0) return -1;
    var id = g.nextVoice++;
    var gain = g.ctx.createGain();
    gain.connect(g.ctx.destination);
    g.voices[id] = {
        gain: gain, layers: [], sources: [],
        layerCount: layerCount, layerVolumes: [], loopStart: 0, loopEnd: 0,
        ready: false, playing: false, ended: false,
        startAt: 0, startOffset: 0, loop: false
    };
    return id;
});

// 各層を Web Audio の非同期デコーダーへ渡す。C# 側で PCM を作らない。
EM_JS(void, ee_audio_voice_set_source, (int id, const unsigned char* data, int len,
    const int* lengths, int layerCount, double loopStart, double loopEnd), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (!m || !g.ctx) return;
    var offset = 0;
    var decodes = [];
    for (var i = 0; i < layerCount; i++) {
        var size = HEAP32[(lengths >> 2) + i];
        (function (bytes) {
            decodes.push(new Promise(function(resolve, reject) {
                try {
                    g.ctx.decodeAudioData(bytes.buffer, resolve, reject);
                } catch (e) {
                    reject(e);
                }
            }));
        })(HEAPU8.slice(data + offset, data + offset + size));
        offset += size;
    }
    Promise.all(decodes).then(function(buffers) {
        m.layers = buffers;
        for (var j = 0; j < buffers.length; j++) {
            if (typeof m.layerVolumes[j] !== 'number')
                m.layerVolumes[j] = j === 0 ? 1 : 0;
        }
        m.loopStart = loopStart;
        m.loopEnd = loopEnd;
        m.ready = true;
        if (m.playPending) {
            var pending = m.playPending;
            m.playPending = null;
            g.startVoice(id, pending.start, pending.loop, pending.volume);
        }
    }).catch(function(e) {
        m.failed = true;
        if (typeof console !== 'undefined' && console.warn)
            console.warn('EmptyEngine: failed to decode voice ' + id, e);
    });
});

EM_JS(void, ee_audio_voice_set_volume, (int id, float volume), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (m) m.gain.gain.value = volume;
});

EM_JS(void, ee_audio_voice_set_layer_volume, (int id, int layer, float volume), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (!m || layer < 0 || layer >= m.layerCount) return;
    m.layerVolumes[layer] = volume;
    if (m.layerGains && m.layerGains[layer])
        m.layerGains[layer].gain.value = volume;
});

EM_JS(void, ee_audio_voice_play, (int id, double startSeconds, int loop, float volume), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (!m || m.failed) return;
    g.resume();
    m.gain.gain.value = volume;
    if (!m.ready) {
        m.playPending = { start: startSeconds, loop: !!loop, volume: volume };
        return;
    }
    g.startVoice(id, startSeconds, !!loop, volume);
});

EM_JS(double, ee_audio_voice_time, (int id), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (!m || !m.playing || !g.ctx) return 0;
    var seconds = m.startOffset + Math.max(0, g.ctx.currentTime - m.startAt);
    var start = m.loopStart;
    var end = m.loopEnd > m.loopStart ? m.loopEnd : m.layers[0].duration;
    if (!m.loop || end <= start) return seconds;
    if (seconds < end) return seconds;
    return start + ((seconds - start) % (end - start));
});

EM_JS(int, ee_audio_voice_is_playing, (int id), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    return !!(m && m.playing && !m.ended);
});

EM_JS(void, ee_audio_voice_destroy, (int id), {
    var g = Module.__ee_audio;
    var m = g && g.voices[id];
    if (!m) return;
    m.sources.forEach(function (src) {
        try { src.stop(); } catch (e) { }
    });
    if (m.layerGains)
        m.layerGains.forEach(function (gain) { gain.disconnect(); });
    m.gain.disconnect();
    delete g.voices[id];
});
