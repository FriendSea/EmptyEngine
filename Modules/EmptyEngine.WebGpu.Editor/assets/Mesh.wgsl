struct Uniforms {
    mvp: mat4x4<f32>,
    normalMatrix: mat4x4<f32>,
    tint: vec4<f32>,
    light: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var base_texture: texture_2d<f32>;
@group(0) @binding(2) var samp: sampler;

struct VsOut {
    @builtin(position) position: vec4<f32>,
    @location(0) normal: vec3<f32>,
    @location(1) uv: vec2<f32>,
};

@vertex
fn vs_main(@location(0) position: vec3<f32>, @location(1) normal: vec3<f32>, @location(2) uv: vec2<f32>) -> VsOut {
    var out: VsOut;
    out.position = u.mvp * vec4<f32>(position, 1.0);
    out.normal = (u.normalMatrix * vec4<f32>(normal, 0.0)).xyz;
    out.uv = uv;
    return out;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let n = normalize(in.normal);
    let diffuse = max(dot(n, -u.light.xyz), 0.0);
    let lighting = u.light.w + (1.0 - u.light.w) * diffuse;
    let base = textureSample(base_texture, samp, in.uv) * u.tint;
    return vec4<f32>(base.rgb * lighting, base.a);
}
