#ifndef ACRYLIC_DEPTH_BLUR_VS
#define ACRYLIC_DEPTH_BLUR_VS

#include "sh_Utils.h"

layout(location = 0) in highp vec2 m_Position;
layout(location = 1) in lowp vec4 m_Colour;
layout(location = 2) in highp vec2 m_TexCoord;

layout(location = 0) out highp vec4 v_Colour;
layout(location = 1) out highp vec2 v_TexCoord;
layout(location = 2) out highp vec4 v_ScreenPosition;

void main(void)
{
    // 传递顶点颜色和纹理坐标
    v_Colour = m_Colour;
    v_TexCoord = m_TexCoord;
    
    // 计算屏幕空间位置(用于深度采样)
    gl_Position = g_ProjMatrix * vec4(m_Position, 1.0, 1.0);
    v_ScreenPosition = gl_Position;
}

#endif
