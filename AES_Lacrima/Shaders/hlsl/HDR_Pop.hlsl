cbuffer Params : register(b0)
{
    float brightness;
    float saturation;
    float sourceWidth;
    float sourceHeight;
    float4 tint;
    float outputWidth;
    float outputHeight;
    float2 _padding0;
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

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float2 sampleSize = float2(min(sourceSize.x, outputSize.x), min(sourceSize.y, outputSize.y));
    float2 texel = 1.0 / sampleSize;
    float2 uv = input.uv;

    float3 center = SampleColor(uv);
    float3 blur =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));
    blur *= 0.25;

    float3 bloom = max(center - 0.45, 0.0) * 2.6 + max(blur - 0.35, 0.0) * 1.7;
    float3 color = center + bloom * 0.62;

    color = color / (1.0 + color);
    color = saturate(color);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    float vibrance = 1.42;
    color = lerp(float3(luma, luma, luma), color, saturation * vibrance);
    color *= brightness;
    color *= tint.rgb;
    color = saturate(color);

    return float4(color, tint.a);
}
