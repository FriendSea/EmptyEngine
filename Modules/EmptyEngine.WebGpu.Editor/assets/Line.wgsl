struct Uniforms {
    model_view: mat4x4<f32>,
    projection: mat4x4<f32>,
    color: vec4<f32>,
    misc: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u: Uniforms;

@vertex
fn vs_main(@location(0) position: vec3<f32>) -> @builtin(position) vec4<f32> {
    return u.projection * u.model_view * vec4<f32>(position, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return u.color;
}
