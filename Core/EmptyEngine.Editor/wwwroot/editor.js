/** 指定した境界に対応するペインを返す。 */
function paneOf(splitter) {
    return splitter.dataset.splitter === 'next' ? splitter.nextElementSibling : splitter.previousElementSibling;
}

/** ペイン境界のドラッグ */
export function initSplitters(containerId, dotnet) {
    const container = document.getElementById(containerId);
    const controllers = [];
    if (!container) return { dispose() {} };

    const collapsed = (pane) => pane.classList.contains('pane-collapsed');

    for (const splitter of container.querySelectorAll('[data-splitter]')) {
        const pane = paneOf(splitter);
        if (!pane) continue;

        const direction = splitter.dataset.splitter === 'next' ? -1 : 1;

        const controller = attachLockedDrag(splitter, {
            start: (e) => collapsed(pane) ? null : { x: e.clientX, width: pane.getBoundingClientRect().width },
            move: (state, x) => {
                const width = Math.max(160, state.width + direction * (x - state.x));
                pane.style.flex = `0 0 ${width}px`;
            },
            end: () => {
                const widths = {};
                for (const other of container.querySelectorAll('[data-splitter]')) {
                    const target = paneOf(other);
                    if (target && target.dataset.pane && !collapsed(target)) {
                        widths[target.dataset.pane] = target.getBoundingClientRect().width;
                    }
                }
                dotnet.invokeMethodAsync('OnPaneResized', widths).catch(() => controller.dispose());
            },
        });
        controllers.push(controller);
    }
    return { dispose: () => controllers.forEach((controller) => controller.dispose()) };
}

/** 数値フィールドのラベルのドラッグスクラブ化 */
export function attachScrub(label, dotnet, sensitivity) {
    return attachValueDrag(label, dotnet, () => ({ sensitivity, clamp: (value) => value }));
}

/** スライダーも現在値からの相対ドラッグにする。キーボード操作は input 自身に任せる。 */
export function attachRange(input, dotnet) {
    return attachValueDrag(input, dotnet, () => {
        const min = Number(input.min);
        const max = Number(input.max);
        return {
            sensitivity: (max - min) / Math.max(1, input.getBoundingClientRect().width),
            clamp: (value) => Math.max(min, Math.min(max, Math.round(value))),
        };
    });
}

function attachValueDrag(element, dotnet, settings) {
    let disposed = false;
    let previousEnd = Promise.resolve();
    const invoke = (method, ...args) => disposed ? Promise.resolve(null) : dotnet.invokeMethodAsync(method, ...args);
    const fail = () => { disposed = true; controller.dispose(); };
    const controller = attachLockedDrag(element, {
        start: (e) => {
            const state = { x: e.clientX, ...settings() };
            // 前のドラッグの確定後に基準値を読む。待っている間もロック・移動・終了は受け付ける。
            state.ready = previousEnd.then(() => invoke('BeginScrub'));
            state.ready.then((value) => { if (value === null) controller.cancel(state); }).catch(fail);
            return state;
        },
        move: (state, x) => {
            const delta = (x - state.x) * state.sensitivity;
            state.ready.then((value) => value === null ? null : invoke('SetScrubValue', state.clamp(value + delta))).catch(fail);
        },
        end: (state) => {
            // ready に登録した順に Set → End を送る。各移動で通信の往復を待たない。
            previousEnd = state.ready.then(() => invoke('EndScrub')).catch(fail);
        },
    });
    return { dispose: fail };
}

/** ギズモのドラッグ。ロック中の相対移動を SVG 座標に積算する。 */
export function attachGizmo(element, dotnet) {
    const notify = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(() => controller.dispose());
    const point = (x, y, matrix) => ({
        x: matrix.a * x + matrix.c * y + matrix.e,
        y: matrix.b * x + matrix.d * y + matrix.f,
    });
    const controller = attachLockedDrag(element, {
        start: (e) => {
            const matrix = element.getScreenCTM()?.inverse();
            if (!matrix) return null;
            const start = point(e.clientX, e.clientY, matrix);
            const hit = [...element.querySelectorAll('[data-gizmo-radius]')].some((handle) =>
                Math.hypot(start.x - Number(handle.dataset.gizmoX), start.y - Number(handle.dataset.gizmoY))
                    <= Number(handle.dataset.gizmoRadius));
            if (!hit) return null;
            const state = { matrix };
            notify('BeginDrag', start.x, start.y).then((accepted) => {
                if (accepted === false) controller.cancel(state);
            });
            return state;
        },
        move: (state, x, y) => {
            const position = point(x, y, state.matrix);
            notify('DragTo', position.x, position.y);
        },
        end: () => notify('EndDrag'),
    });
    return controller;
}

/** 相対移動で値・サイズを変える操作に共通のカーソル固定と終了処理。 */
function attachLockedDrag(element, handlers) {
    const doc = element.ownerDocument;
    const win = doc.defaultView;
    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    let drag = null;
    let dragListeners = null;
    let lockRequest = null;
    let disposed = false;

    function finish(notifyEnd = true) {
        const previous = drag;
        drag = null;
        dragListeners?.abort();
        dragListeners = null;
        if (previous && element.hasPointerCapture(previous.pointerId)) element.releasePointerCapture(previous.pointerId);
        if (doc.pointerLockElement === element) doc.exitPointerLock();
        if (previous && notifyEnd) handlers.end(previous.state);
    }

    function cleanLockListeners() {
        // 解放後に遅れてロックが成立した場合も、change を受けて即座に解除する。
        if (!disposed || lockRequest) return;
        doc.removeEventListener('pointerlockchange', onLockChange);
        doc.removeEventListener('pointerlockerror', onLockError);
    }

    function onLockChange() {
        if (doc.pointerLockElement === element) {
            lockRequest = null;
            if (drag && !disposed) drag.locked = true;
            else doc.exitPointerLock();
        } else if (drag?.locked) {
            finish();
        }
        cleanLockListeners();
    }

    function onLockError() {
        lockRequest = null;
        // 非対応・権限拒否時は pointer capture による通常ドラッグを続ける。
        cleanLockListeners();
    }

    function dispose() {
        if (disposed) return;
        disposed = true;
        finish(false);
        listeners.abort();
        cleanLockListeners();
    }

    element.addEventListener('pointerdown', (e) => {
        if (disposed || drag || lockRequest || e.button !== 0 || !e.isPrimary || doc.pointerLockElement) return;
        const state = handlers.start(e);
        if (!state) return;

        e.preventDefault();
        const current = drag = { state, x: e.clientX, y: e.clientY, pointerId: e.pointerId, pointerType: e.pointerType, locked: false };
        element.setPointerCapture(e.pointerId);
        listenWhileDragging();

        if (e.pointerType === 'mouse' && element.requestPointerLock) {
            // Blazor Server の往復を待たず、ユーザー操作のイベント内で要求する。
            lockRequest = current;
            try {
                element.requestPointerLock()?.catch(() => {
                    if (lockRequest === current) onLockError();
                });
            } catch {
                onLockError();
            }
        }
    }, options);

    function onPointerMove(e) {
        if (!drag || e.pointerId !== drag.pointerId || doc.pointerLockElement === element) return;
        if ((e.buttons & 1) === 0) { finish(); return; }
        drag.x = e.clientX;
        drag.y = e.clientY;
        handlers.move(drag.state, drag.x, drag.y);
    }

    // Pointer Lock が保証する mousemove を使用し、pointermove と二重加算しない。
    function onMouseMove(e) {
        if (!drag || doc.pointerLockElement !== element) return;
        if ((e.buttons & 1) === 0) { finish(); return; }
        drag.x += e.movementX;
        drag.y += e.movementY;
        handlers.move(drag.state, drag.x, drag.y);
    }

    const onPointerEnd = (e) => {
        if (drag && e.pointerId === drag.pointerId) finish();
    };
    function listenWhileDragging() {
        // 数値欄の数だけ document の移動リスナーが増えないよう、操作中だけ購読する。
        dragListeners = new AbortController();
        const active = { signal: dragListeners.signal };
        doc.addEventListener('pointermove', onPointerMove, active);
        doc.addEventListener('mousemove', onMouseMove, active);
        doc.addEventListener('pointerup', onPointerEnd, active);
        doc.addEventListener('pointercancel', onPointerEnd, active);
        doc.addEventListener('mouseup', (e) => {
            if (drag?.pointerType === 'mouse' && e.button === 0) finish();
        }, active);
        element.addEventListener('lostpointercapture', (e) => {
            // ロック成立時にも capture が外れるため、その場合はドラッグを終えない。
            if (!lockRequest && doc.pointerLockElement !== element) onPointerEnd(e);
        }, active);
        doc.addEventListener('keydown', (e) => { if (e.key === 'Escape') finish(); }, active);
        doc.addEventListener('visibilitychange', () => { if (doc.hidden) finish(); }, active);
        win.addEventListener('blur', () => finish(), active);
    }
    doc.addEventListener('pointerlockchange', onLockChange);
    doc.addEventListener('pointerlockerror', onLockError);
    return { dispose, cancel: (state) => { if (drag?.state === state) finish(); } };
}

/** 右クリックメニューのカーソル位置への配置 */
export function placeMenu(menu, x, y) {
    if (!menu) return;

    const margin = 4;
    const rect = menu.getBoundingClientRect();

    let left = x;
    let top = y;
    if (left + rect.width > window.innerWidth - margin) left = Math.max(margin, x - rect.width);
    if (top + rect.height > window.innerHeight - margin) top = Math.max(margin, y - rect.height);

    menu.style.left = `${left}px`;
    menu.style.top = `${top}px`;
}

/** ヒエラルキーにフォーカスを置き、矢印・Space キーの既定動作を止める。ツリー操作は C# が行う。 */
export function initHierarchyKeys(tree) {
    tree.addEventListener('pointerdown', (e) => {
        if (e.button === 0 && e.target.closest('.tree-row')
            && !e.target.closest('input, button, select, textarea, [contenteditable]')) {
            tree.focus({ preventScroll: true });
        }
    });
    tree.addEventListener('keydown', (e) => {
        if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', ' '].includes(e.key)) return;
        if (e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) return;

        // 入力欄・IME・ダイアログのキーを Blazor のツリー操作へ渡さない。
        if (e.isComposing || isTextEntry(e.target) || tree.ownerDocument.querySelector('.modal, .context-menu')) {
            e.stopPropagation();
            return;
        }
        e.preventDefault();
    });
}

/** ヒエラルキーの選択行を、必要な場合だけ表示領域へスクロールする。 */
export function scrollHierarchySelection(tree) {
    tree?.querySelector('.tree-row.selected')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

/**
 * ピッカーの絞り込み欄で上下キーがキャレットを動かさないようにする。
 * @remarks 候補の選択は C# が同じキーで行う。Blazor の preventDefault はキーごとに切り替えられない。
 */
export function holdPickerCaret(input) {
    input?.addEventListener('keydown', (e) => {
        if (!e.isComposing && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) e.preventDefault();
    });
}

/** ピッカーの選択行を、必要な場合だけ表示領域へスクロールする。 */
export function scrollPickerSelection(list) {
    list?.querySelector('.list-row.selected')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

/** 可視になった／フォーカスが戻ったことの C# への通知 */
export function watchVisibility(dotnet) {
    const notify = () => {
        if (document.visibilityState === 'visible') dotnet.invokeMethodAsync('OnBecameVisible');
    };
    document.addEventListener('visibilitychange', notify);
    window.addEventListener('focus', notify);
}

/** 文字を打つためのものではない input の種別 */
const nonTextInputTypes = new Set(['button', 'checkbox', 'color', 'file', 'image', 'radio', 'range', 'reset', 'submit']);

/** 文字入力欄か */
function isTextEntry(element) {
    if (!(element instanceof HTMLElement)) return false;
    if (element.isContentEditable || element instanceof HTMLTextAreaElement) return true;
    return element instanceof HTMLInputElement && !nonTextInputTypes.has(element.type);
}

/** キー操作に対応するショートカット名（無ければ null） */
function shortcutOf(e) {
    if (e.isComposing || e.altKey) return null;

    const typing = isTextEntry(e.target);
    if (!e.ctrlKey && !e.metaKey) {
        const inHierarchy = e.target instanceof Element && e.target.closest('[data-shortcuts="hierarchy"]');
        return e.key === 'Delete' && !e.shiftKey && !typing && inHierarchy ? 'delete' : null;
    }

    switch (e.key.toLowerCase()) {
        case 'z': return typing ? null : e.shiftKey ? 'redo' : 'undo';
        case 'y': return typing || e.shiftKey ? null : 'redo';
        case 's': return e.shiftKey ? null : 'save';
        case 'p': return e.shiftKey ? null : 'play';
        default: return null;
    }
}

/**
 * 画面共通のショートカットキーの C# への通知
 * @remarks フォーカスが body に落ちていても拾えるよう window で受ける。
 * 文字入力欄での undo/redo と Delete は欄自身に任せる。
 * ダイアログ・右クリックメニューを開いている間は既定動作だけ止めて何もしない。
 * 保存と Play は打ちかけの欄を先に確定させる（blur で change が先に届く）。
 */
export function watchShortcuts(dotnet) {
    window.addEventListener('keydown', (e) => {
        const name = shortcutOf(e);
        if (!name) return;

        e.preventDefault();
        if (e.repeat && (name === 'save' || name === 'play')) return;
        if (document.querySelector('.modal, .context-menu')) return;

        if (isTextEntry(e.target)) e.target.blur();
        dotnet.invokeMethodAsync('OnShortcut', name);
    });
}

let dropped = [];

/** 外部からのドラッグを示す DataTransfer の種別。 */
let externalDropTypes = [];

/** 外部ドラッグとして受け入れる種別を設定する。 */
export function useExternalDropTypes(types) {
    externalDropTypes = Array.from(types ?? []).map((type) => type.toLowerCase());
}

/** 外部ファイルのドラッグかを判定する。 */
function isExternalDrag(dataTransfer) {
    return Array.from(dataTransfer?.types ?? []).some((type) => externalDropTypes.includes(type.toLowerCase()));
}

/** ドロップされたファイルのパスまたは URI を取得する。 */
function itemsFrom(dataTransfer) {
    const list = dataTransfer.getData('text/uri-list') || dataTransfer.getData('text/plain');
    const items = list
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0 && !line.startsWith('#'));

    if (items.length > 0) return items;
    return Array.from(dataTransfer.files ?? []).map((file) => file.name);
}

window.addEventListener('dragover', (e) => {
    if (!isExternalDrag(e.dataTransfer)) return;

    e.preventDefault();
    const overTarget = e.target instanceof Element && e.target.closest('[data-drop]');
    e.dataTransfer.dropEffect = overTarget ? 'copy' : 'none';
}, true);

window.addEventListener('drop', (e) => {
    if (!isExternalDrag(e.dataTransfer)) return;
    dropped = itemsFrom(e.dataTransfer);
    e.preventDefault();
}, true);

window.emptyEngineTakeDrop = () => {
    const items = dropped;
    dropped = [];
    return items;
};

window.emptyEngineReveal = (path) => {
    if (window.parent === window) return false;

    window.parent.postMessage({ type: 'emptyengine:reveal', path }, '*');
    return true;
};

window.emptyEngineOpenSource = (path, line) => {
    if (window.parent === window) return false;

    window.parent.postMessage({ type: 'emptyengine:openSource', path, line }, '*');
    return true;
};

(() => {
    let watching = false;
    let leaving = false;

    function say(title, hint) {
        const t = document.querySelector('.reconnect-title');
        const h = document.querySelector('.reconnect-hint');
        if (t) t.textContent = title;
        if (h) h.textContent = hint;
    }

    const BUILD_TIMEOUT_MS = 20000;

    async function alive() {
        try {
            const response = await fetch('/healthz', { cache: 'no-store' });
            return response.ok;
        } catch {
            return false;
        }
    }

    async function watchWhileDisconnected(modal) {
        if (watching) return;
        watching = true;

        const startedAt = Date.now();
        let sawDown = false;
        let warned = false;

        for (;;) {
            if (leaving) return;

            if (!modal.className.includes('components-reconnect-show')) {
                watching = false;
                return;
            }

            const up = await alive();
            if (!up) {
                sawDown = true;
            } else if (sawDown || modal.className.includes('failed') || modal.className.includes('rejected')) {
                leaving = true;
                location.reload();
                return;
            }

            if (!warned && Date.now() - startedAt > BUILD_TIMEOUT_MS) {
                warned = true;
                say('Still not back',
                    'The build may have failed. Check the Host console. This screen comes back automatically once it is fixed.');
            }

            await new Promise((resolve) => setTimeout(resolve, 500));
        }
    }

    window.addEventListener('DOMContentLoaded', () => {
        const modal = document.getElementById('components-reconnect-modal');
        if (!modal) return;

        const observer = new MutationObserver(() => {
            if (modal.className.includes('components-reconnect-show')) watchWhileDisconnected(modal);
        });
        observer.observe(modal, { attributes: true, attributeFilter: ['class'] });
    });

    window.addEventListener('beforeunload', () => { leaving = true; });
})();
