struct Uniforms {
    transform: mat4x4<f32>,
    uvRect: vec4<f32>,
    anim: vec4<f32>, // x = seconds since this sprite first drew, y = frames per second, z = frame count
    color: vec4<f32>,
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
fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>) -> VsOut {
    var out: VsOut;
    out.position = u.transform * vec4<f32>(position, 0.0, 1.0);
    let frameCount = max(u.anim.z, 1.0);
    let frame = floor(u.anim.x * u.anim.y) % frameCount;
    out.uv = u.uvRect.xy + vec2<f32>(frame * u.uvRect.z, 0.0) + uv * u.uvRect.zw;
    out.color = u.color;
    out.viewDepth = out.position.w;
    out.normal = vec3<f32>(0.0, 0.0, -1.0);
    return out;
}
