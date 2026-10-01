#include <cstdint>

#include "basis_universal/transcoder/basisu_transcoder.h"

#if defined(_WIN32)
#define EE_BASIS_EXPORT extern "C" __declspec(dllexport)
#else
#define EE_BASIS_EXPORT extern "C" __attribute__((used)) __attribute__((visibility("default")))
#endif

namespace
{
    bool choose_format(uint32_t target, basist::transcoder_texture_format& format)
    {
        switch (target)
        {
        case 0: format = basist::transcoder_texture_format::cTFRGBA32; return true;
        case 1: format = basist::transcoder_texture_format::cTFBC7_RGBA; return true;
        case 2: format = basist::transcoder_texture_format::cTFASTC_LDR_4x4_RGBA; return true;
        case 3: format = basist::transcoder_texture_format::cTFETC2_RGBA; return true;
        default: return false;
        }
    }
}

// Thin engine-owned ABI over the unmodified Basis Universal transcoder.
// Returns 0 on success; non-zero values identify validation/transcode failures.
EE_BASIS_EXPORT uint32_t ee_basis_transcode_ktx2_uastc(
    const uint8_t* data,
    uint32_t data_size,
    uint32_t expected_width,
    uint32_t expected_height,
    uint32_t target,
    uint8_t* output,
    uint32_t output_size)
{
    if (!data || !data_size || !expected_width || !expected_height || !output)
        return 1;

    basist::transcoder_texture_format format;
    if (!choose_format(target, format))
        return 1;

    basist::basisu_transcoder_init();

    basist::ktx2_transcoder transcoder;
    if (!transcoder.init(data, data_size))
        return 2;
    if (transcoder.get_width() != expected_width ||
        transcoder.get_height() != expected_height ||
        transcoder.get_levels() != 1 ||
        !transcoder.is_uastc())
        return 3;

    const uint32_t required_size = basist::basis_compute_transcoded_image_size_in_bytes(
        format, expected_width, expected_height);
    const uint32_t bytes_per_block_or_pixel = basist::basis_get_bytes_per_block_or_pixel(format);
    if (!required_size || output_size != required_size || !bytes_per_block_or_pixel)
        return 4;
    if (!transcoder.start_transcoding())
        return 5;

    const uint32_t output_blocks_or_pixels = required_size / bytes_per_block_or_pixel;
    return transcoder.transcode_image_level(
        0, 0, 0, output, output_blocks_or_pixels, format) ? 0u : 6u;
}
