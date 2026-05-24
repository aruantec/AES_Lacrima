cbuffer Params : register(b0)
{
    float brightness;
    float saturation;
    float sourceWidth;
    float sourceHeight;
    float4 tint;
    float outputWidth;
    float outputHeight;
    float sourceIsSrgb;
    float timeSeconds;
};

Texture2D src : register(t0);
SamplerState samp : register(s0);

struct PSIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD;
};

float3 SampleColor(float2 uv)
{
    return src.Sample(samp, saturate(uv)).rgb;
}

float GetLuma(float3 color)
{
    return dot(color, float3(0.299, 0.587, 0.114));
}

float GetSaturation(float3 color)
{
    float mx = max(max(color.r, color.g), color.b);
    float mn = min(min(color.r, color.g), color.b);
    return mx - mn;
}

float2 PixelateUv(float2 uv, float blocks)
{
    float2 blockUv = floor(uv * blocks) + 0.5;
    return blockUv / blocks;
}

float3 GradeRetroMenu(float3 color)
{
    float luma = GetLuma(color);
    float sat = GetSaturation(color);

    float3 shadowCol = float3(0.04, 0.02, 0.07);
    float3 midCol = float3(0.22, 0.1, 0.34);
    float3 panelCol = float3(0.42, 0.24, 0.58);
    float3 textCol = float3(0.72, 0.62, 0.86);
    float3 goldCol = float3(0.95, 0.78, 0.2);

    float3 graded = lerp(shadowCol, midCol, smoothstep(0.0, 0.32, luma));
    graded = lerp(graded, panelCol, smoothstep(0.22, 0.58, luma));
    graded = lerp(graded, textCol, smoothstep(0.45, 0.78, luma));
    graded = lerp(graded, goldCol, smoothstep(0.72, 0.98, luma));

    float coloredText = smoothstep(0.1, 0.42, sat) * smoothstep(0.18, 0.95, luma);
    float3 preserved = color;
    preserved.r = lerp(preserved.r, preserved.r * 1.05, step(preserved.g, preserved.r));
    preserved.g = lerp(preserved.g, preserved.g * 1.08, step(max(preserved.r, preserved.b), preserved.g));
    preserved.b = lerp(preserved.b, preserved.b * 1.08, step(preserved.r, preserved.b));

    float3 result = lerp(graded, preserved, coloredText * 0.72);

    const float levels = 5.0;
    float ql = floor(luma * levels + 0.0001) / (levels - 1.0);
    result = lerp(result, result * (ql / max(luma, 0.001)), 0.5);

    return result;
}

float4 main(PSIn input) : SV_TARGET
{
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float blocks = min(outputSize.x, outputSize.y) / 2.4;
    blocks = clamp(blocks, 64.0, 320.0);

    float2 uv = PixelateUv(input.uv, blocks);
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    float3 color = SampleColor(uv);
    float luma = GetLuma(color);

    color = GradeRetroMenu(color);

    float edge =
        abs(luma - GetLuma(SampleColor(uv + float2(texel.x, 0.0)))) +
        abs(luma - GetLuma(SampleColor(uv - float2(texel.x, 0.0)))) +
        abs(luma - GetLuma(SampleColor(uv + float2(0.0, texel.y)))) +
        abs(luma - GetLuma(SampleColor(uv - float2(0.0, texel.y))));
    float border = smoothstep(0.12, 0.42, edge);
    color = lerp(color, float3(0.03, 0.01, 0.06), border * 0.35);
    color += float3(0.75, 0.58, 0.12) * smoothstep(0.55, 0.95, border) * smoothstep(0.45, 0.95, luma) * 0.18;

    float2 cell = frac(input.uv * blocks);
    float grid = smoothstep(0.94, 1.0, max(cell.x, cell.y));
    color *= 1.0 - grid * 0.06;

    float lumaOut = GetLuma(color);
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.08);
    color = pow(saturate(color), 0.97);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
