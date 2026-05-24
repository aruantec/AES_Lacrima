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

float SobelEdge(float2 uv, float2 texel)
{
    float tl = GetLuma(SampleColor(uv + float2(-texel.x, -texel.y)));
    float t = GetLuma(SampleColor(uv + float2(0.0, -texel.y)));
    float tr = GetLuma(SampleColor(uv + float2(texel.x, -texel.y)));
    float l = GetLuma(SampleColor(uv + float2(-texel.x, 0.0)));
    float r = GetLuma(SampleColor(uv + float2(texel.x, 0.0)));
    float bl = GetLuma(SampleColor(uv + float2(-texel.x, texel.y)));
    float b = GetLuma(SampleColor(uv + float2(0.0, texel.y)));
    float br = GetLuma(SampleColor(uv + float2(texel.x, texel.y)));

    float gx = -tl - 2.0 * l - bl + tr + 2.0 * r + br;
    float gy = -tl - 2.0 * t - tr + bl + 2.0 * b + br;
    return sqrt(gx * gx + gy * gy);
}

float3 PosterizeColor(float3 color, float levels)
{
    float luma = max(GetLuma(color), 0.001);
    float x = luma * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.45, frac(x));
    float quantized = lerp(band, next, blend);
    return color * (quantized / luma);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 uv = input.uv;

    float3 color = SampleColor(uv);
    const float shadeLevels = 6.0;
    color = PosterizeColor(color, shadeLevels);

    float edge = SobelEdge(uv, texel * 1.1);
    float outline = smoothstep(0.14, 0.38, edge);
    color = lerp(color, float3(0.06, 0.06, 0.1), outline * 0.42);

    float lumaOut = GetLuma(color);
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.15);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
