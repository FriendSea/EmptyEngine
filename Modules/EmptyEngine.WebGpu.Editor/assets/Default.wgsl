// The fragment shader of the default materials. It reads only the varyings every bundled vertex shader writes.
@group(3) @binding(0) var tex: texture_2d<f32>;
@group(3) @binding(1) var samp: sampler;

@fragment
fn fs_main(@location(0) uv: vec2<f32>, @location(1) color: vec4<f32>) -> @location(0) vec4<f32> {
    return textureSample(tex, samp, uv) * color;
}
