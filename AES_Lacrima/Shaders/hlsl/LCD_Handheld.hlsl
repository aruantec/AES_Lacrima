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
    float2 pixel = input.uv * texSize;
    float2 cell = frac(pixel);

    float3 color = src.Sample(samp, input.uv).rgb;

    float rowMask = lerp(0.88, 1.04, smoothstep(0.10, 0.55, sin(cell.y * 3.14159)));
    float columnMask = 0.92 + 0.08 * sin(cell.x * 3.14159 * 3.0);
    color *= rowMask * columnMask;

    float3 lcdTint = float3(0.90, 1.02, 0.86);
    color *= lcdTint;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 0.92);
    color *= brightness;
    color *= tint.rgb;

    return float4(color, tint.a);
}
