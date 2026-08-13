/**
 * PlutoSVG FreeType OT-SVG hooks export.
 * Provides a stable DLL entry point for FreeTypeSharp / osu!framework.
 */
#include "plutosvg-ft.h"

#if defined(_WIN32) || defined(__CYGWIN__)
#define PLUTOSVGFT_EXPORT __declspec(dllexport)
#else
#define PLUTOSVGFT_EXPORT __attribute__((visibility("default")))
#endif

PLUTOSVGFT_EXPORT SVG_RendererHooks* plutosvg_get_ft_hooks(void)
{
    return &plutosvg_ft_hooks;
}
