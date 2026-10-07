struct Uniforms {
    model_view: mat4x4<f32>,
    projection: mat4x4<f32>,
    color: vec4<f32>,
    misc: vec4<f32>, // y = full length of the line (world units)
};
@group(0) @binding(0) var<uniform> u: Uniforms;

struct VsOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) viewDepth: f32,
    @location(3) normal: vec3<f32>,
};

@vertex
fn vs_main(@location(0) position: vec3<f32>, @location(1) uv: vec2<f32>) -> VsOut {
    var out: VsOut;
    out.position = u.projection * u.model_view * vec4<f32>(position, 1.0);
    out.uv = uv;
    out.color = u.color;
    out.viewDepth = out.position.w;
    out.normal = vec3<f32>(0.0, 0.0, -1.0);
    return out;
}

@group(3) @binding(0) var tex: texture_2d<f32>;
@group(3) @binding(1) var samp: sampler;

@fragment
fn fs_main(@location(0) uv: vec2<f32>, @location(1) color: vec4<f32>) -> @location(0) vec4<f32> {
    return textureSample(tex, samp, uv) * color;
}
