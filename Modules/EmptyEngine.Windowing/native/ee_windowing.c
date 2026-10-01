// ブラウザのフレーム更新と入力を C# へ公開する静的 P/Invoke モジュール。
#include <emscripten.h>
#include <emscripten/html5.h>
#include <emscripten/console.h>

// abort 時にもログを残すため、バッファを経由しない。
void ee_windowing_log(const char* s) { emscripten_console_log(s); }

// 制御を JS へ返すため、無限ループの模擬は無効にする。
void ee_set_main_loop(void (*frame)(void)) { emscripten_set_main_loop(frame, 0, 0); }

// C# へキーイベントを転送するコールバック型。code は KeyboardEvent.code（"KeyA"/"ArrowLeft" 等）。
// 戻り値は「ゲームが扱うキーか」で、1 のとき既定動作を抑止（preventDefault）する。
typedef int (*ee_key_callback)(const char* code, int is_down);

static ee_key_callback s_ee_key_cb = 0;

static EM_BOOL ee_on_key(int event_type, const EmscriptenKeyboardEvent* e, void* user_data)
{
    int is_down = (event_type == EMSCRIPTEN_EVENT_KEYDOWN);
    int handled = s_ee_key_cb ? s_ee_key_cb(e->code, is_down) : 0;
    // ゲームが扱うキーだけ既定動作を抑止する（矢印/Space のページスクロール等）。ただし修飾キー併用時は
    // ブラウザのショートカット（Ctrl+R 再読込 / Ctrl+W 等）を潰さないよう抑止しない。
    int suppress = handled && !e->ctrlKey && !e->altKey && !e->metaKey;
    return suppress ? EM_TRUE : EM_FALSE;
}

void ee_install_key_callback(ee_key_callback cb)
{
    s_ee_key_cb = cb;
    emscripten_set_keydown_callback(EMSCRIPTEN_EVENT_TARGET_WINDOW, 0, EM_TRUE, ee_on_key);
    emscripten_set_keyup_callback(EMSCRIPTEN_EVENT_TARGET_WINDOW, 0, EM_TRUE, ee_on_key);
}

// ページの余白へのタッチを拾わないよう、canvas を購読する。
#define EE_TOUCH_TARGET "#canvas"

// C# 側が配列を直接読むため、TouchPoint とメモリレイアウトを一致させる。
typedef struct ee_touch_point { int id; float x, y; } ee_touch_point;

// 接触点の「全集合」を毎回渡すコールバック型。座標は 0..1 正規化（左上原点・y 下向き）、
// aspect は正規化に使った canvas の 幅÷高さ。
typedef void (*ee_touch_callback)(const ee_touch_point* points, int count, float aspect);

static ee_touch_callback s_ee_touch_cb = 0;

static EM_BOOL ee_on_touch(int event_type, const EmscriptenTouchEvent* e, void* user_data)
{
    if (!s_ee_touch_cb) return EM_FALSE;

    double width = 0.0, height = 0.0;
    emscripten_get_element_css_size(EE_TOUCH_TARGET, &width, &height);
    if (width <= 0.0) width = 1.0;
    if (height <= 0.0) height = 1.0;

    // 終了・キャンセルの変化点は離れた指なので、接触中の集合から除く。
    int drop_changed = (event_type == EMSCRIPTEN_EVENT_TOUCHEND || event_type == EMSCRIPTEN_EVENT_TOUCHCANCEL);

    ee_touch_point points[32];
    int count = 0;
    for (int i = 0; i < e->numTouches && count < 32; ++i)
    {
        const EmscriptenTouchPoint* t = &e->touches[i];
        if (drop_changed && t->isChanged) continue;
        points[count].id = t->identifier;
        points[count].x = (float)(t->targetX / width);
        points[count].y = (float)(t->targetY / height);
        ++count;
    }

    s_ee_touch_cb(points, count, (float)(width / height));
    // canvas 上の指はゲームのものなので、スクロール・ピンチズーム・ダブルタップ拡大の既定動作は常に抑止する
    // （キーと違い、ブラウザのショートカットと食い合う余地が無い）。
    return EM_TRUE;
}

void ee_install_touch_callback(ee_touch_callback cb)
{
    s_ee_touch_cb = cb;
    emscripten_set_touchstart_callback(EE_TOUCH_TARGET, 0, EM_TRUE, ee_on_touch);
    emscripten_set_touchmove_callback(EE_TOUCH_TARGET, 0, EM_TRUE, ee_on_touch);
    emscripten_set_touchend_callback(EE_TOUCH_TARGET, 0, EM_TRUE, ee_on_touch);
    emscripten_set_touchcancel_callback(EE_TOUCH_TARGET, 0, EM_TRUE, ee_on_touch);
}

// 座標と表示面サイズは CSS ピクセル。ボタンのビットは MouseButtons と同じ。
EM_JS(void, ee_read_mouse, (float* values), {
    const canvas = document.querySelector('#canvas');
    if (!canvas) {
        for (let i = 0; i < 7; ++i) HEAPF32[(values >> 2) + i] = 0;
        return;
    }
    if (!canvas.eeMouse) {
        const state = canvas.eeMouse = { x: -1, y: -1, buttons: 0, scroll: 0, seen: false };
        const update = e => {
            if (e.pointerType !== 'mouse') return;
            state.x = e.clientX;
            state.y = e.clientY;
            state.buttons = e.buttons;
            state.seen = true;
        };
        canvas.addEventListener('pointermove', update);
        canvas.addEventListener('pointerenter', update);
        canvas.addEventListener('pointerdown', e => {
            // preventDefault はクリックでのフォーカス移動も止めるため、自分で canvas（埋め込み先では iframe ごと）へ移す。
            window.focus();
            canvas.focus({ preventScroll: true });
            if (e.pointerType !== 'mouse') return;
            update(e);
            canvas.setPointerCapture(e.pointerId);
            e.preventDefault();
        });
        canvas.addEventListener('pointerup', update);
        canvas.addEventListener('pointercancel', () => { state.buttons = 0; state.seen = false; });
        canvas.addEventListener('lostpointercapture', () => { state.buttons = 0; });
        canvas.addEventListener('pointerleave', e => { if (!state.buttons) state.seen = false; });
        window.addEventListener('blur', () => { state.buttons = 0; state.seen = false; });
        canvas.addEventListener('wheel', e => {
            state.x = e.clientX;
            state.y = e.clientY;
            state.seen = true;
            state.scroll -= e.deltaY / (e.deltaMode === 1 ? 3 : e.deltaMode === 2 ? 1 : 100);
            e.preventDefault();
        }, { passive: false });
        canvas.addEventListener('contextmenu', e => e.preventDefault());
    }
    const state = canvas.eeMouse;
    const rect = canvas.getBoundingClientRect();
    const x = state.x - rect.left, y = state.y - rect.top;
    const inside = x >= 0 && y >= 0 && x < rect.width && y < rect.height;
    const active = document.hasFocus() && state.seen && (inside || state.buttons !== 0);
    const data = [x, y, rect.width, rect.height, state.buttons, state.scroll, active ? 1 : 0];
    for (let i = 0; i < 7; ++i) HEAPF32[(values >> 2) + i] = data[i];
});

// GamepadState と同一レイアウト。ボタンの番号は GamepadButtons と対応する。
#include <stdint.h>
#include <string.h>
typedef struct ee_gamepad_state {
    int32_t id;
    uint32_t buttons;
    float left_x, left_y, right_x, right_y, left_trigger, right_trigger;
} ee_gamepad_state;

int ee_sample_gamepads(void)
{
    if (emscripten_sample_gamepad_data() != EMSCRIPTEN_RESULT_SUCCESS) return 0;
    return emscripten_get_num_gamepads();
}

int ee_get_gamepad(int index, ee_gamepad_state* out)
{
    memset(out, 0, sizeof(*out));
    EmscriptenGamepadEvent pad;
    if (emscripten_get_gamepad_status(index, &pad) != EMSCRIPTEN_RESULT_SUCCESS ||
        !pad.connected || strcmp(pad.mapping, "standard") != 0) return 0;
    out->id = index;
    const int button_indices[] = {0, 1, 2, 3, 4, 5, 8, 9, 16, 10, 11, 12, 15, 13, 14};
    for (int bit = 0; bit < 15; ++bit)
        if (button_indices[bit] < pad.numButtons && pad.digitalButton[button_indices[bit]])
            out->buttons |= 1u << bit;
    if (pad.numAxes > 0) out->left_x = (float)pad.axis[0];
    if (pad.numAxes > 1) out->left_y = -(float)pad.axis[1];
    if (pad.numAxes > 2) out->right_x = (float)pad.axis[2];
    if (pad.numAxes > 3) out->right_y = -(float)pad.axis[3];
    if (pad.numButtons > 6) out->left_trigger = (float)pad.analogButton[6];
    if (pad.numButtons > 7) out->right_trigger = (float)pad.analogButton[7];
    return 1;
}
