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
    float2 texel = 1.0 / float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 uv = input.uv;

    float3 center = SampleColor(uv);
    float3 blur =
        SampleColor(uv + float2(texel.x, 0.0)) +
        SampleColor(uv - float2(texel.x, 0.0)) +
        SampleColor(uv + float2(0.0, texel.y)) +
        SampleColor(uv - float2(0.0, texel.y));
    blur *= 0.25;

    float3 bloom = max(center - 0.55, 0.0) * 1.8 + max(blur - 0.45, 0.0) * 1.1;
    float3 color = center + bloom * 0.35;

    color = color / (1.0 + color);

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    float vibrance = 1.18;
    color = lerp(float3(luma, luma, luma), color, saturation * vibrance);
    color *= brightness;
    color *= tint.rgb;

    return float4(color, tint.a);
}
