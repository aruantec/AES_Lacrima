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

float3 ApplyDisplayScanlines(float3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

float3 SampleColor(float2 uv)
{
    return src.Sample(samp, saturate(uv)).rgb;
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 texel = 1.0 / sourceSize;
    float2 uv = input.uv;

    float chroma = 1.8 / sourceSize.x;
    float3 color;
    color.r = SampleColor(uv + float2(chroma, 0.0)).r;
    color.g = SampleColor(uv).g;
    color.b = SampleColor(uv - float2(chroma, 0.0)).b;

    float3 blur =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));
    blur *= 0.25;

    float3 glow = max(blur - 0.35, 0.0);
    color += glow * 0.18;

    float x = input.pos.x;
    float3 mask = float3(
        0.5 + 0.5 * sin(x * 3.14159265),
        0.5 + 0.5 * sin(x * 3.14159265 + 2.094),
        0.5 + 0.5 * sin(x * 3.14159265 + 4.189));
    mask = lerp(float3(1.0, 1.0, 1.0), mask, 0.28);
    color *= mask;

    color = ApplyDisplayScanlines(color, input.pos.y, 0.20);

    float2 dist = uv - 0.5;
    color *= saturate(1.0 - dot(dist, dist) * 0.42);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color = pow(saturate(color), 0.96);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
