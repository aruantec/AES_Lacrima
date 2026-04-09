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

float4 main(PSIn input) : SV_TARGET
{
    float2 texSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 uv = input.uv;

    float3 color;
    color.r = src.Sample(samp, uv + float2(0.0015, 0.0)).r;
    color.g = src.Sample(samp, uv).g;
    color.b = src.Sample(samp, uv - float2(0.0015, 0.0)).b;

    float scan = sin(uv.y * texSize.y * 6.28318);
    scan = scan * scan;
    color *= 1.0 - scan * 0.28;

    float slot = 0.94 + 0.06 * sin(uv.x * texSize.x * 3.14159);
    color *= slot;

    float2 dist = uv - 0.5;
    float vignette = saturate(1.0 - dot(dist, dist) * 0.35);
    color *= vignette;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 1.08);
    color *= brightness;
    color *= tint.rgb;

    return float4(color, tint.a);
}
