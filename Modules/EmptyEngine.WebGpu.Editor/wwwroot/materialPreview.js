// Draws one material on a quad with the browser's WebGPU, for the material inspector.
// The bind groups mirror the runtime: 0 = component, 1 = globals (all zero),
// 2 = material params / sampler / extra textures, 3 = the material's main texture and its sampler.
// No component draws here, so a fragment shader that reads group 0 gets stand-in values:
// 1 for every value of the component uniform, the shader's defaults for the component Params,
// and an empty Effect history.

const GLOBAL_SLOTS = 8;
const COMPONENT_UNIFORM_BINDING = 0;
const COMPONENT_PARAMS_BINDING = 3;
const HISTORY_BINDING = 31;
const COMPONENT_UNIFORM_FLOATS = 1024;
const HISTORY_FLOATS = 4 + 128 * 20;
const DEPTH_FORMAT = 'depth24plus';

// Writes the varyings every bundled vertex shader writes, so any material's fs_main links against it.
const QUAD_VERTEX = `
struct VsOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) viewDepth: f32,
    @location(3) normal: vec3<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> VsOut {
    let corner = vec2<f32>(f32(index & 1u), f32((index >> 1u) & 1u));
    var out: VsOut;
    out.position = vec4<f32>((corner * 2.0 - 1.0) * 0.86, 0.5, 1.0);
    out.uv = vec2<f32>(corner.x, 1.0 - corner.y);
    out.color = vec4<f32>(1.0);
    out.viewDepth = 1.0;
    out.normal = vec3<f32>(0.0, 0.0, -1.0);
    return out;
}`;

const BACKDROP = `
@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let corner = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(corner * 2.0 - 1.0, 1.0, 1.0);
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let cell = (u32(position.x) / 16u + u32(position.y) / 16u) % 2u;
    let shade = select(0.17, 0.24, cell == 1u);
    return vec4<f32>(shade, shade, shade, 1.0);
}`;

const BLENDS = {
    Alpha: {
        color: { srcFactor: 'src-alpha', dstFactor: 'one-minus-src-alpha' },
        alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' },
    },
    Invert: {
        color: { srcFactor: 'one-minus-dst', dstFactor: 'zero' },
        alpha: { srcFactor: 'one', dstFactor: 'zero' },
    },
    DstAlphaMask: {
        color: { srcFactor: 'one-minus-dst-alpha', dstFactor: 'dst-alpha' },
        alpha: { srcFactor: 'one-minus-dst-alpha', dstFactor: 'dst-alpha' },
    },
};

const DEPTH_COMPARES = { LessEqual: 'less-equal', Less: 'less', Always: 'always' };

let shared;
const textures = new Map();   // asset key -> GPUTextureView
const canvases = new WeakMap();

function gpu() {
    shared ??= createShared().catch((error) => {
        shared = undefined;
        throw error;
    });
    return shared;
}

async function createShared() {
    if (!navigator.gpu) throw new Error('WebGPU is not available in this view.');
    const adapter = await navigator.gpu.requestAdapter();
    if (!adapter) throw new Error('No WebGPU adapter is available.');
    const device = await adapter.requestDevice();
    device.lost.then(() => {
        shared = undefined;
        textures.clear();
    });

    const format = navigator.gpu.getPreferredCanvasFormat();
    const sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

    const white = device.createTexture({
        size: [1, 1],
        format: 'rgba8unorm',
        usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
    });
    device.queue.writeTexture({ texture: white }, new Uint8Array([255, 255, 255, 255]), {}, [1, 1]);
    const whiteView = white.createView();

    const emptyLayout = device.createBindGroupLayout({ entries: [] });
    const componentLayout = device.createBindGroupLayout({
        entries: [
            { binding: COMPONENT_UNIFORM_BINDING, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
            { binding: COMPONENT_PARAMS_BINDING, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
            { binding: HISTORY_BINDING, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'read-only-storage' } },
        ],
    });
    const ones = new Float32Array(COMPONENT_UNIFORM_FLOATS).fill(1);
    const componentUniform = device.createBuffer({
        size: ones.byteLength,
        usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    device.queue.writeBuffer(componentUniform, 0, ones);
    const globalsLayout = device.createBindGroupLayout({
        entries: Array.from({ length: GLOBAL_SLOTS }, (_, binding) => ({
            binding,
            visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
            buffer: { type: 'uniform' },
        })),
    });
    const mainLayout = device.createBindGroupLayout({
        entries: [
            { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: 'float' } },
            { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
        ],
    });

    const backdropModule = device.createShaderModule({ code: BACKDROP });
    return {
        device,
        format,
        sampler,
        whiteView,
        emptyLayout,
        componentLayout,
        componentUniform,
        history: device.createBuffer({ size: HISTORY_FLOATS * 4, usage: GPUBufferUsage.STORAGE }),
        globalsLayout,
        mainLayout,
        quadVertex: device.createShaderModule({ code: QUAD_VERTEX }),
        emptyGroup: device.createBindGroup({ layout: emptyLayout, entries: [] }),
        globalsGroup: device.createBindGroup({
            layout: globalsLayout,
            entries: Array.from({ length: GLOBAL_SLOTS }, (_, binding) => ({
                binding,
                resource: { buffer: device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM }) },
            })),
        }),
        backdrop: device.createRenderPipeline({
            layout: 'auto',
            vertex: { module: backdropModule, entryPoint: 'vs_main' },
            fragment: { module: backdropModule, entryPoint: 'fs_main', targets: [{ format }] },
            depthStencil: { format: DEPTH_FORMAT, depthWriteEnabled: false, depthCompare: 'always' },
        }),
    };
}

function surface(canvas, g) {
    let state = canvases.get(canvas);
    if (state && state.device === g.device) return state;

    const context = canvas.getContext('webgpu');
    context.configure({ device: g.device, format: g.format, alphaMode: 'opaque' });
    state = {
        device: g.device,
        context,
        depth: g.device.createTexture({
            size: [canvas.width, canvas.height],
            format: DEPTH_FORMAT,
            usage: GPUTextureUsage.RENDER_ATTACHMENT,
        }),
    };
    canvases.set(canvas, state);
    return state;
}

function uniformBuffer(device, values) {
    const data = new Float32Array(values);
    const buffer = device.createBuffer({
        size: data.byteLength,
        usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    device.queue.writeBuffer(buffer, 0, data);
    return buffer;
}

// Builds the material's pipeline against one layout for group 0. Returns { error } when it does not validate.
async function buildPipeline(g, spec, module, materialLayout, componentLayout) {
    const { device } = g;
    device.pushErrorScope('validation');
    const pipeline = device.createRenderPipeline({
        layout: device.createPipelineLayout({
            bindGroupLayouts: [componentLayout, g.globalsLayout, materialLayout, g.mainLayout],
        }),
        vertex: { module: g.quadVertex, entryPoint: 'vs_main' },
        fragment: {
            module,
            entryPoint: 'fs_main',
            targets: [{ format: g.format, blend: BLENDS[spec.blend] }],
        },
        primitive: { topology: 'triangle-strip' },
        depthStencil: {
            format: DEPTH_FORMAT,
            depthWriteEnabled: spec.depthWrite,
            depthCompare: DEPTH_COMPARES[spec.depthCompare] ?? 'less-equal',
        },
    });
    const validation = await device.popErrorScope();
    return validation ? { error: validation.message } : { pipeline };
}

// Builds everything the material's draw needs. Returns { error } when the shader cannot be drawn here.
async function buildMaterial(g, spec) {
    const { device } = g;
    device.pushErrorScope('validation');
    const module = device.createShaderModule({ code: spec.fragment });
    const materialLayout = device.createBindGroupLayout({
        entries: [
            { binding: 0, visibility: GPUShaderStage.FRAGMENT, buffer: { type: 'uniform' } },
            { binding: 1, visibility: GPUShaderStage.FRAGMENT, sampler: { type: 'filtering' } },
            ...spec.textures.map((slot) => ({
                binding: slot.binding,
                visibility: GPUShaderStage.FRAGMENT,
                texture: { sampleType: 'float' },
            })),
        ],
    });
    const group = device.createBindGroup({
        layout: materialLayout,
        entries: [
            { binding: 0, resource: { buffer: uniformBuffer(device, spec.params) } },
            { binding: 1, resource: g.sampler },
            ...spec.textures.map((slot) => ({
                binding: slot.binding,
                resource: textures.get(slot.key) ?? g.whiteView,
            })),
        ],
    });
    const main = device.createBindGroup({
        layout: g.mainLayout,
        entries: [
            { binding: 0, resource: textures.get(spec.mainTexture) ?? g.whiteView },
            { binding: 1, resource: g.sampler },
        ],
    });
    const info = await module.getCompilationInfo();
    const validation = await device.popErrorScope();
    const compile = info.messages
        .filter((message) => message.type === 'error')
        .map((message) => `${message.lineNum}:${message.linePos} ${message.message}`);
    if (compile.length > 0) return { error: compile.join('\n') };
    if (validation) return { error: validation.message };

    // A shader that leaves group 0 alone validates against an empty one; only the others need stand-ins.
    const plain = await buildPipeline(g, spec, module, materialLayout, g.emptyLayout);
    if (!plain.error) return { pipeline: plain.pipeline, group, main, component: g.emptyGroup, standIn: false };

    const withComponent = await buildPipeline(g, spec, module, materialLayout, g.componentLayout);
    if (withComponent.error) return { error: withComponent.error };

    device.pushErrorScope('validation');
    const component = device.createBindGroup({
        layout: g.componentLayout,
        entries: [
            { binding: COMPONENT_UNIFORM_BINDING, resource: { buffer: g.componentUniform } },
            { binding: COMPONENT_PARAMS_BINDING, resource: { buffer: uniformBuffer(device, spec.componentParams) } },
            { binding: HISTORY_BINDING, resource: { buffer: g.history } },
        ],
    });
    const bindError = await device.popErrorScope();
    if (bindError) return { error: bindError.message };
    return { pipeline: withComponent.pipeline, group, main, component, standIn: true };
}

/** Asset keys among `keys` whose pixels have not been uploaded yet. */
export async function missingTextures(keys) {
    try {
        await gpu();
    } catch {
        return [];
    }
    return keys.filter((key) => !textures.has(key));
}

/** Uploads RGBA8 pixels for one texture asset. */
export async function uploadTexture(key, width, height, pixels) {
    const g = await gpu();
    const data = new Uint8Array(await pixels.arrayBuffer());
    const texture = g.device.createTexture({
        size: [width, height],
        format: 'rgba8unorm',
        usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
    });
    g.device.queue.writeTexture({ texture }, data, { bytesPerRow: width * 4, rowsPerImage: height }, [width, height]);
    textures.set(key, texture.createView());
}

/** Draws the material. Returns { error, standIn }: why it could not be drawn, and whether group 0 was a stand-in. */
export async function render(canvas, spec) {
    try {
        const g = await gpu();
        const state = surface(canvas, g);
        const material = spec.fragment
            ? await buildMaterial(g, spec)
            : { error: 'The material has no shader with fs_main.' };

        g.device.pushErrorScope('validation');
        const encoder = g.device.createCommandEncoder();
        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                view: state.context.getCurrentTexture().createView(),
                clearValue: { r: 0, g: 0, b: 0, a: 1 },
                loadOp: 'clear',
                storeOp: 'store',
            }],
            depthStencilAttachment: {
                view: state.depth.createView(),
                depthClearValue: 1,
                depthLoadOp: 'clear',
                depthStoreOp: 'store',
            },
        });
        pass.setPipeline(g.backdrop);
        pass.draw(3);
        if (!material.error) {
            pass.setPipeline(material.pipeline);
            pass.setBindGroup(0, material.component);
            pass.setBindGroup(1, g.globalsGroup);
            pass.setBindGroup(2, material.group);
            pass.setBindGroup(3, material.main);
            pass.draw(4);
        }
        pass.end();
        g.device.queue.submit([encoder.finish()]);
        const drawError = await g.device.popErrorScope();

        return { error: material.error ?? drawError?.message ?? null, standIn: material.standIn === true };
    } catch (error) {
        return { error: String(error?.message ?? error), standIn: false };
    }
}

/** Frees what a canvas held. */
export function release(canvas) {
    const state = canvases.get(canvas);
    if (!state) return;

    canvases.delete(canvas);
    state.depth.destroy();
    state.context.unconfigure();
}
