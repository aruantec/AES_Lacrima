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

float3 EdgeAwareSmooth(float2 uv, float2 texel, float strength)
{
    float3 center = SampleColor(uv);
    float3 sum = center;
    float weightSum = 1.0;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0)
                continue;

            float2 offset = float2(x, y) * texel;
            float3 neighbor = SampleColor(uv + offset);
            float diff = dot(abs(neighbor - center), float3(1.0, 1.0, 1.0));
            float w = exp(-diff * (4.5 + strength));
            sum += neighbor * w;
            weightSum += w;
        }
    }

    return sum / weightSum;
}

float3 WideSmooth(float2 uv, float2 texel)
{
    float3 sum =
        SampleColor(uv) +
        SampleColor(uv + float2(texel.x * 2.0, 0.0)) +
        SampleColor(uv - float2(texel.x * 2.0, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y * 2.0)) +
        SampleColor(uv - float2(0.0, texel.y * 2.0));
    return sum * 0.2;
}

float3 SoftCelColor(float3 color, float levels)
{
    float luma = max(GetLuma(color), 0.001);
    float x = luma * levels;
    float band = floor(x + 0.0001) / (levels - 1.0);
    float next = min(band + 1.0 / (levels - 1.0), 1.0);
    float blend = smoothstep(0.0, 0.72, frac(x));
    float quantized = lerp(band, next, blend);
    return color * (quantized / luma);
}

float3 ApplyVibrance(float3 color, float amount)
{
    float luma = GetLuma(color);
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return lerp(float3(luma, luma, luma), color, 1.0 + boost);
}

float EdgeStrength(float2 uv, float2 texel)
{
    float c = GetLuma(SampleColor(uv));
    float l = GetLuma(SampleColor(uv - float2(texel.x, 0.0)));
    float r = GetLuma(SampleColor(uv + float2(texel.x, 0.0)));
    float u = GetLuma(SampleColor(uv - float2(0.0, texel.y)));
    float d = GetLuma(SampleColor(uv + float2(0.0, texel.y)));
    return abs(c * 4.0 - l - r - u - d);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float2 texel = 1.0 / sourceSize;
    float2 uv = input.uv;

    float upscale = max(outputSize.x / sourceSize.x, outputSize.y / sourceSize.y);
    float smoothAmount = saturate((upscale - 1.0) / 2.5);

    float3 color = EdgeAwareSmooth(uv, texel, smoothAmount * 6.0);
    float3 wide = WideSmooth(uv, texel);
    float flatness = 1.0 - saturate(EdgeStrength(uv, texel * 1.1) * 3.5);
    color = lerp(color, wide, flatness * (0.35 + smoothAmount * 0.35));

    color = SoftCelColor(color, 6.0);

    float3 bloom =
        WideSmooth(uv, texel * 1.5) +
        SampleColor(uv + float2(texel.x, texel.y)) +
        SampleColor(uv - float2(texel.x, texel.y));
    bloom *= 0.333;
    float3 glow = max(bloom - 0.38, 0.0);
    color += glow * (0.22 + smoothAmount * 0.12);

    color = ApplyVibrance(color, 0.42 * saturation);

    float3 graded;
    graded.r = dot(color, float3(1.08, 0.05, 0.02));
    graded.g = dot(color, float3(0.0, 1.04, 0.03));
    graded.b = dot(color, float3(0.02, 0.06, 1.10));
    color = lerp(color, graded, 0.55);

    float luma = GetLuma(color);
    color += float3(0.018, 0.014, 0.030) * (1.0 - smoothstep(0.0, 0.45, luma));
    color += float3(0.035, 0.020, 0.010) * smoothstep(0.62, 1.0, luma);

    float edge = EdgeStrength(uv, texel * 1.25);
    float outline = smoothstep(0.08, 0.28, edge);
    color = lerp(color, float3(0.10, 0.11, 0.20), outline * 0.18);

    float lumaOut = GetLuma(color);
    color = lerp(float3(lumaOut, lumaOut, lumaOut), color, saturation * 1.25);
    color = pow(saturate(color), 0.94);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
