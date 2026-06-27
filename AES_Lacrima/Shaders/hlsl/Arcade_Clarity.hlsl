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
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float2 sampleSize = float2(min(sourceSize.x, outputSize.x), min(sourceSize.y, outputSize.y));
    float2 texel = 1.0 / sampleSize;
    float2 uv = input.uv;

    float3 center = SampleColor(uv);
    float3 neighbors =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));

    float3 sharpened = center * 1.65 - neighbors * 0.1625;
    float3 color = lerp(center, sharpened, 0.72);

    float bleedR = SampleColor(uv + float2(texel.x * 2.0, 0.0)).r;
    float bleedB = SampleColor(uv - float2(texel.x * 2.0, 0.0)).b;
    color.r = lerp(color.r, bleedR, 0.12);
    color.b = lerp(color.b, bleedB, 0.12);

    color = ApplyDisplayScanlines(color, input.pos.y, 0.10);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 1.05);
    color = pow(saturate(color), 0.98);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
