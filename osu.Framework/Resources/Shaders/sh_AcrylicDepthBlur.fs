#ifndef ACRYLIC_DEPTH_BLUR_FS
#define ACRYLIC_DEPTH_BLUR_FS

#include "sh_Utils.h"
#include "sh_Masking.h"

#undef INV_SQRT_2PI
#define INV_SQRT_2PI 0.39894

layout(location = 0) in highp vec4 v_Colour;
layout(location = 1) in mediump vec2 v_TexCoord;
layout(location = 2) in highp vec4 v_ScreenPosition;

// 模糊参数
layout(std140, set = 0, binding = 0) uniform m_AcrylicParameters
{
    mediump vec2 g_ScreenSize;          // 屏幕尺寸
    int g_BlurRadius;                    // 模糊半径
    mediump float g_BlurSigma;           // 高斯模糊 sigma
    mediump float g_BlurStrength;        // 模糊强度(0-1)
    mediump float g_TintAlpha;           // 着色透明度
    mediump vec3 g_TintColor;            // 着色颜色
    highp float g_CurrentDepth;          // 当前容器的深度
};

// 场景纹理(后台缓冲区)
layout(set = 1, binding = 0) uniform lowp texture2D m_SceneTexture;
layout(set = 1, binding = 1) uniform lowp sampler m_SceneSampler;

// 深度纹理
layout(set = 2, binding = 0) uniform highp texture2D m_DepthTexture;
layout(set = 2, binding = 1) uniform highp sampler m_DepthSampler;

layout(location = 0) out vec4 o_Colour;

// 计算高斯权重
mediump float computeGauss(in mediump float x, in mediump float sigma)
{
    return INV_SQRT_2PI * exp(-0.5 * x * x / (sigma * sigma)) / sigma;
}

// 基于深度的采样 - 只采样比当前深度更深的像素
lowp vec4 sampleWithDepth(vec2 uv, highp float currentDepth)
{
    // 采样深度值
    highp float depth = texture(sampler2D(m_DepthTexture, m_DepthSampler), uv).r;
    
    // 只有当采样点的深度大于当前深度时才采样(更深的内容)
    // 在深度缓冲区中,更大的值表示更深的深度
    if (depth > currentDepth)
    {
        return texture(sampler2D(m_SceneTexture, m_SceneSampler), uv);
    }
    else
    {
        // 如果采样点在当前层之上或同层,返回透明色
        return vec4(0.0);
    }
}

// 双通道可分离高斯模糊(水平+垂直)
lowp vec4 applyDepthAwareBlur(vec2 screenCoord, highp float currentDepth, int radius, float sigma)
{
    vec2 pixelSize = 1.0 / g_ScreenSize;
    
    // 第一步: 水平模糊
    mediump float factor = computeGauss(0.0, sigma);
    mediump vec4 horizontalSum = sampleWithDepth(screenCoord, currentDepth) * factor;
    mediump float totalFactor = factor;
    int validSamples = 1;
    
    for (int i = 1; i <= radius; i++)
    {
        factor = computeGauss(float(i), sigma);
        
        vec4 sample1 = sampleWithDepth(screenCoord + vec2(float(i) * pixelSize.x, 0.0), currentDepth);
        vec4 sample2 = sampleWithDepth(screenCoord - vec2(float(i) * pixelSize.x, 0.0), currentDepth);
        
        // 只有当采样有效时才累加
        if (sample1.a > 0.0)
        {
            horizontalSum += sample1 * factor;
            totalFactor += factor;
            validSamples++;
        }
        
        if (sample2.a > 0.0)
        {
            horizontalSum += sample2 * factor;
            totalFactor += factor;
            validSamples++;
        }
    }
    
    vec4 horizontalBlur = totalFactor > 0.0 ? horizontalSum / totalFactor : vec4(0.0);
    
    // 第二步: 垂直模糊(对水平模糊的结果进行)
    // 为简化,我们在fragment shader中做简化版的双通道模糊
    // 实际生产环境可以用两个pass来实现更高效的可分离模糊
    factor = computeGauss(0.0, sigma);
    mediump vec4 finalSum = horizontalBlur * factor;
    totalFactor = factor;
    
    for (int i = 1; i <= radius; i++)
    {
        factor = computeGauss(float(i), sigma);
        
        vec4 sample1 = sampleWithDepth(screenCoord + vec2(0.0, float(i) * pixelSize.y), currentDepth);
        vec4 sample2 = sampleWithDepth(screenCoord - vec2(0.0, float(i) * pixelSize.y), currentDepth);
        
        if (sample1.a > 0.0)
        {
            finalSum += sample1 * factor;
            totalFactor += factor;
        }
        
        if (sample2.a > 0.0)
        {
            finalSum += sample2 * factor;
            totalFactor += factor;
        }
    }
    
    return totalFactor > 0.0 ? finalSum / totalFactor : vec4(0.0);
}

void main(void)
{
    // 计算屏幕空间UV坐标
    vec2 screenCoord = (v_ScreenPosition.xy / v_ScreenPosition.w) * 0.5 + 0.5;
    
    // 获取当前片段的深度
    highp float currentDepth = g_CurrentDepth;
    
    // 应用深度感知模糊
    vec4 blurredColor = applyDepthAwareBlur(
        screenCoord,
        currentDepth,
        g_BlurRadius,
        g_BlurSigma
    );
    
    // 应用着色层
    vec4 tintColor = vec4(g_TintColor, g_TintAlpha);
    vec4 finalColor = blend(tintColor, blurredColor);
    
    // 应用遮罩(圆角等)
    vec2 wrappedCoord = wrap(v_TexCoord, v_TexRect);
    o_Colour = getRoundedColor(finalColor * v_Colour, wrappedCoord);
}

#endif
