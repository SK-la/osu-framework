#ifndef ACRYLIC_BLUR_FS
#define ACRYLIC_BLUR_FS

#include "sh_Utils.h"

#undef INV_SQRT_2PI
#define INV_SQRT_2PI 0.39894

layout(location = 2) in mediump vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform m_BlurParameters
{
	mediump vec2 g_TexSize;
	int g_Radius;
	mediump float g_Sigma;
	highp vec2 g_BlurDirection;
};

layout(set = 1, binding = 0) uniform lowp texture2D m_Texture;
layout(set = 1, binding = 1) uniform lowp sampler m_Sampler;

layout(location = 0) out vec4 o_Colour;

mediump float computeGauss(in mediump float x, in mediump float sigma)
{
	return INV_SQRT_2PI * exp(-0.5*x*x / (sigma*sigma)) / sigma;
}

lowp vec4 blur(int radius, highp vec2 direction, mediump vec2 texCoord, mediump vec2 texSize, mediump float sigma)
{
	mediump vec4 sum = vec4(0.0);
	mediump float totalWeight = 0.0;

	// 中心像素
	mediump float weight = computeGauss(0.0, sigma);
	sum += texture(sampler2D(m_Texture, m_Sampler), texCoord) * weight;
	totalWeight += weight;

	// 对称采样 - 修复采样位置和权重
	for (int i = 1; i <= radius; ++i)
	{
		// 使用正确的采样位置（1, 2, 3... 而不是1.5, 3.5...）
		mediump float x = float(i);
		weight = computeGauss(x, sigma);
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord + direction * x / texSize) * weight;
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord - direction * x / texSize) * weight;
		totalWeight += 2.0 * weight;
	}

	return sum / totalWeight;
}

void main(void)
{
	// 应用模糊
	vec4 blurredColour = blur(g_Radius, g_BlurDirection, v_TexCoord, g_TexSize, g_Sigma);
	
	o_Colour = blurredColour;
}

#endif