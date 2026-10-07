struct Uniforms {
    mvp: mat4x4<f32>,
    normalMatrix: mat4x4<f32>,
    tint: vec4<f32>,
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
fn vs_main(@location(0) position: vec3<f32>, @location(1) normal: vec3<f32>, @location(2) uv: vec2<f32>) -> VsOut {
    var out: VsOut;
    out.position = u.mvp * vec4<f32>(position, 1.0);
    out.uv = uv;
    out.color = u.tint;
    out.viewDepth = out.position.w;
    out.normal = (u.normalMatrix * vec4<f32>(normal, 0.0)).xyz;
    return out;
}
