import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

// editor.js also registers page-wide listeners when imported.
globalThis.window = new EventTarget();
const source = await readFile(new URL('../EmptyEngine.Editor/wwwroot/editor.js', import.meta.url), 'utf8');
const { attachGizmo, attachScrub, attachRange, initSplitters } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function emit(target, type, properties = {}) {
    const event = new Event(type, { cancelable: true });
    Object.assign(event, {
        button: 0, buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true,
        clientX: 100, clientY: 100, movementX: 0, movementY: 0,
    }, properties);
    target.dispatchEvent(event);
}

function setup(t, {
    lock = 'deferred', matrix = { a: 1, b: 0, c: 0, d: 1, e: 0, f: 0 },
    mode = 'gizmo', sensitivity = 0.05, begin, initial = 10, direction = 'prev',
} = {}) {
    const doc = new EventTarget();
    doc.defaultView = new EventTarget();
    doc.pointerLockElement = null;
    doc.exitPointerLock = () => {
        doc.pointerLockElement = null;
        emit(doc, 'pointerlockchange');
    };
    const element = new EventTarget();
    element.ownerDocument = doc;
    element.min = '0';
    element.max = '255';
    element.getBoundingClientRect = () => ({ width: 255 });
    element.getScreenCTM = () => ({ inverse: () => matrix });
    element.querySelectorAll = () => [{ dataset: { gizmoX: '100', gizmoY: '100', gizmoRadius: '16' } }];
    const captured = new Set();
    element.setPointerCapture = (id) => captured.add(id);
    element.hasPointerCapture = (id) => captured.has(id);
    element.releasePointerCapture = (id) => {
        captured.delete(id);
        emit(element, 'lostpointercapture', { pointerId: id });
    };
    let requests = 0;
    if (lock !== 'unsupported') {
        element.requestPointerLock = () => {
            requests++;
            if (lock === 'denied') return Promise.reject(new Error('Denied'));
            // The legacy API returns void and announces completion via events.
        };
    }
    const calls = [];
    let value = initial;
    const dotnet = {
        invokeMethodAsync(method, ...args) {
            calls.push([method, ...args]);
            if (method === 'BeginScrub') return begin ? begin() : Promise.resolve(value);
            if (method === 'SetScrubValue') value = args[0];
            return Promise.resolve(true);
        },
    };
    const pane = { style: {}, getBoundingClientRect: () => ({ width: Number.parseFloat(pane.style.flex?.split(' ')[2] ?? '280') }) };
    let controller;
    if (mode === 'splitter') {
        element.dataset = { splitter: direction };
        element.previousElementSibling = pane;
        element.nextElementSibling = pane;
        globalThis.document = { getElementById: () => ({ querySelectorAll: () => [element] }) };
        controller = initSplitters('editor-panes', dotnet);
    } else if (mode === 'scrub') controller = attachScrub(element, dotnet, sensitivity);
    else if (mode === 'range') controller = attachRange(element, dotnet);
    else controller = attachGizmo(element, dotnet);
    t.after(() => controller.dispose());
    return {
        doc, element, calls, controller, pane,
        get value() { return value; },
        get requests() { return requests; },
        down: (properties) => emit(element, 'pointerdown', properties),
        lock() {
            doc.pointerLockElement = element;
            // Acquiring pointer lock releases pointer capture.
            element.releasePointerCapture(1);
            emit(doc, 'pointerlockchange');
        },
    };
}

test('locks only primary left-button drags on handles', (t) => {
    const s = setup(t);
    s.down({ clientX: 2, clientY: 2 });
    s.down({ button: 2 });
    s.down({ isPrimary: false });
    assert.equal(s.requests, 0);
    assert.deepEqual(s.calls, []);
    s.down();
    assert.equal(s.requests, 1);
    assert.deepEqual(s.calls, [['BeginDrag', 100, 100]]);
});

// Flush interop Promise callbacks without timers or wall-clock sleeps.
const flush = () => new Promise((resolve) => setImmediate(resolve));

for (const sensitivity of [0.01, 0.5, 0.05, 0.2]) {
    test(`numeric scrubbing retains sensitivity ${sensitivity} while the cursor is locked`, async (t) => {
        const s = setup(t, { mode: 'scrub', sensitivity });
        s.down();
        assert.equal(s.requests, 1);
        s.lock();
        emit(s.doc, 'pointermove', { movementX: 1000 });
        emit(s.doc, 'mousemove', { movementX: 1000 });
        emit(s.doc, 'mousemove', { movementX: -40, movementY: 20 });
        emit(s.doc, 'mouseup', { buttons: 0 });
        await flush();
        assert.deepEqual(s.calls, [
            ['BeginScrub'], ['SetScrubValue', 10 + 1000 * sensitivity],
            ['SetScrubValue', 10 + 960 * sensitivity], ['EndScrub'],
        ]);
        assert.equal(s.doc.pointerLockElement, null);
    });
}

for (const reason of ['escape', 'blur', 'cancel', 'unlock']) {
    test(`numeric scrubbing releases the edit hold on ${reason}`, async (t) => {
        const s = setup(t, { mode: 'scrub' });
        s.down();
        s.lock();
        if (reason === 'escape') emit(s.doc, 'keydown', { key: 'Escape' });
        if (reason === 'blur') emit(s.doc.defaultView, 'blur');
        if (reason === 'cancel') emit(s.doc, 'pointercancel');
        if (reason === 'unlock') s.doc.exitPointerLock();
        await flush();
        assert.deepEqual(s.calls, [['BeginScrub'], ['EndScrub']]);
        assert.equal(s.doc.pointerLockElement, null);
    });
}

test('a quick release before the starting value arrives flushes movement then ends, and a new drag uses the committed value', async (t) => {
    let resolve;
    let first = true;
    const pending = new Promise((done) => { resolve = done; });
    const s = setup(t, { mode: 'scrub', begin: () => {
        if (first) { first = false; return pending; }
        return Promise.resolve(s.value);
    } });
    s.down();
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 100 });
    emit(s.doc, 'mouseup');
    s.down();
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 20 });
    emit(s.doc, 'mouseup');
    await flush();
    assert.deepEqual(s.calls, [['BeginScrub']]);
    resolve(10);
    await flush();
    assert.deepEqual(s.calls, [
        ['BeginScrub'], ['SetScrubValue', 15], ['EndScrub'],
        ['BeginScrub'], ['SetScrubValue', 16], ['EndScrub'],
    ]);
});

test('disposing a field while its starting value is pending prevents late edits and unlocks', async (t) => {
    let resolve;
    const pending = new Promise((done) => { resolve = done; });
    const s = setup(t, { mode: 'scrub', begin: () => pending });
    s.down();
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 40 });
    await flush();
    s.controller.dispose();
    resolve(10);
    await flush();
    assert.deepEqual(s.calls, [['BeginScrub']]);
    assert.equal(s.doc.pointerLockElement, null);
});

test('a read-only field rejected by the server releases the cursor without edits', async (t) => {
    const s = setup(t, { mode: 'scrub', begin: () => Promise.resolve(null) });
    s.down();
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 100 });
    await flush();
    assert.deepEqual(s.calls, [['BeginScrub'], ['EndScrub']]);
    assert.equal(s.doc.pointerLockElement, null);
});

test('numeric scrubbing falls back to absolute movement when locking is denied', async (t) => {
    const s = setup(t, { mode: 'scrub', lock: 'denied' });
    s.down();
    await flush();
    emit(s.doc, 'pointermove', { clientX: 160 });
    emit(s.doc, 'pointerup');
    await flush();
    assert.deepEqual(s.calls, [['BeginScrub'], ['SetScrubValue', 13], ['EndScrub']]);
});

test('alpha dragging starts at the current value and clamps relative changes to the slider range', async (t) => {
    const s = setup(t, { mode: 'range', initial: 128 });
    s.down();
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 200 });
    emit(s.doc, 'mousemove', { movementX: -600 });
    emit(s.doc, 'mouseup');
    await flush();
    assert.deepEqual(s.calls, [['BeginScrub'], ['SetScrubValue', 255], ['SetScrubValue', 0], ['EndScrub']]);
});

for (const direction of ['prev', 'next']) {
    test(`pane resizing uses relative movement for the ${direction} pane and persists on release`, (t) => {
        const s = setup(t, { mode: 'splitter', direction });
        s.down();
        s.lock();
        emit(s.doc, 'mousemove', { movementX: 50 });
        emit(s.doc, 'mouseup');
        const expected = direction === 'prev' ? 330 : 230;
        assert.equal(s.pane.style.flex, `0 0 ${expected}px`);
        assert.deepEqual(s.calls, [['OnPaneResized', [expected]]]);
        assert.equal(s.doc.pointerLockElement, null);
    });
}

test('accumulates relative movement beyond the surface without double counting pointer events', (t) => {
    const s = setup(t);
    s.down();
    s.lock();
    emit(s.doc, 'pointermove', { movementX: 500 });
    emit(s.doc, 'mousemove', { movementX: 500, movementY: -40 });
    emit(s.doc, 'mousemove', { movementX: 20, movementY: 10 });
    assert.deepEqual(s.calls, [['BeginDrag', 100, 100], ['DragTo', 600, 60], ['DragTo', 620, 70]]);
    emit(s.doc, 'mouseup', { buttons: 0 });
    assert.equal(s.doc.pointerLockElement, null);
    assert.deepEqual(s.calls.at(-1), ['EndDrag']);
    emit(s.doc, 'mousemove', { movementX: 10 });
    assert.equal(s.calls.length, 4);
});

test('uses the SVG transform for both starting coordinates and relative movement', (t) => {
    const s = setup(t, { matrix: { a: 0.5, b: 0, c: 0, d: 0.5, e: -10, f: -20 } });
    s.down({ clientX: 220, clientY: 240 });
    s.lock();
    emit(s.doc, 'mousemove', { movementX: 40, movementY: -20 });
    assert.deepEqual(s.calls, [['BeginDrag', 100, 100], ['DragTo', 120, 90]]);
});

for (const reason of ['escape', 'unlock', 'blur', 'hidden', 'cancel']) {
    test(`ends the drag and releases the lock on ${reason}`, (t) => {
        const s = setup(t);
        s.down();
        s.lock();
        if (reason === 'escape') emit(s.doc, 'keydown', { key: 'Escape' });
        if (reason === 'unlock') s.doc.exitPointerLock();
        if (reason === 'blur') emit(s.doc.defaultView, 'blur');
        if (reason === 'hidden') { s.doc.hidden = true; emit(s.doc, 'visibilitychange'); }
        if (reason === 'cancel') emit(s.doc, 'pointercancel');
        assert.equal(s.doc.pointerLockElement, null);
        assert.deepEqual(s.calls, [['BeginDrag', 100, 100], ['EndDrag']]);
        emit(s.doc, 'mouseup');
        assert.equal(s.calls.length, 2);
    });
}

for (const lock of ['unsupported', 'denied']) {
    test(`keeps ordinary dragging functional when locking is ${lock}`, async (t) => {
        const s = setup(t, { lock });
        s.down();
        await Promise.resolve();
        emit(s.doc, 'pointermove', { clientX: 140, clientY: 120 });
        emit(s.doc, 'pointerup', { buttons: 0 });
        assert.deepEqual(s.calls, [['BeginDrag', 100, 100], ['DragTo', 140, 120], ['EndDrag']]);
        assert.equal(s.element.hasPointerCapture(1), false);
    });
}

test('touch dragging uses capture and ignores other pointers', (t) => {
    const s = setup(t);
    s.down({ pointerType: 'touch' });
    emit(s.doc, 'pointermove', { pointerId: 2, clientX: 150 });
    emit(s.doc, 'pointerup', { pointerId: 2 });
    emit(s.doc, 'pointermove', { pointerType: 'touch', clientX: 130 });
    emit(s.doc, 'pointerup', { pointerType: 'touch' });
    assert.equal(s.requests, 0);
    assert.deepEqual(s.calls, [['BeginDrag', 100, 100], ['DragTo', 130, 100], ['EndDrag']]);
});

for (const end of ['release', 'dispose']) {
    test(`releases a lock granted after ${end}`, (t) => {
        const s = setup(t);
        s.down();
        if (end === 'release') emit(s.doc, 'pointerup');
        else s.controller.dispose();
        s.lock();
        assert.equal(s.doc.pointerLockElement, null);
        assert.equal(s.calls.filter(([method]) => method === 'EndDrag').length, end === 'release' ? 1 : 0);
    });
}

test('disposing an active gizmo releases its lock and unregisters drag listeners', (t) => {
    const s = setup(t);
    s.down();
    s.lock();
    s.controller.dispose();
    emit(s.doc, 'mousemove', { movementX: 20 });
    s.down();
    assert.equal(s.doc.pointerLockElement, null);
    assert.equal(s.requests, 1);
    assert.deepEqual(s.calls, [['BeginDrag', 100, 100]]);
});
