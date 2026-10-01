// desktop と P/Invoke モジュール名を揃えるため、ファイル名を webgpu_dawn にする。
#include <emscripten.h>
#include <emscripten/html5.h>

void ee_canvas_size(int* width, int* height) { emscripten_get_canvas_element_size("#canvas", width, height); }
