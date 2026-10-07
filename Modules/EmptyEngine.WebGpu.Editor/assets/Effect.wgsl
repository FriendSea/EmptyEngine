struct Fx {
    transform: mat4x4<f32>,         // owner transform * view projection
    time: f32,                      // seconds since this effect first drew
    count: f32,                     // QuadCount
    simulation_space: f32,
    _padding: f32,
    color: vec4<f32>,
    projection: mat4x4<f32>,
    current_transform: mat4x4<f32>,
    emission: vec4<f32>,            // x: 1 = emitting
};
@group(0) @binding(0) var<uniform> fx: Fx;

struct VsOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) viewDepth: f32,
    @location(3) normal: vec3<f32>,
};

// One unit quad per QuadCount, all at the owner's position. Custom effects replace this vertex shader.
@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> VsOut {
    let c = index % 4u;
    let corner = vec2<f32>(
        select(-0.5, 0.5, c == 1u || c == 2u),
        select(-0.5, 0.5, c >= 2u));

    var out: VsOut;
    out.position = fx.transform * vec4<f32>(corner, 0.0, 1.0);
    out.uv = vec2<f32>(corner.x + 0.5, 0.5 - corner.y);
    out.color = fx.color;
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
