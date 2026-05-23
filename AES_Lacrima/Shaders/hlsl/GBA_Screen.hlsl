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

float4 main(PSIn input) : SV_TARGET
{
    float3 color = src.Sample(samp, input.uv).rgb;

    color = pow(saturate(color), 1.5);

    float3 matrixed;
    matrixed.r = dot(color, float3(0.84, 0.16, 0.0));
    matrixed.g = dot(color, float3(0.08, 0.77, 0.15));
    matrixed.b = dot(color, float3(0.15, 0.0, 0.81));
    color = pow(saturate(matrixed), 1.0 / 1.2);

    float2 texSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 cell = frac(input.uv * texSize);
    float grid = 0.92 + 0.08 * sin(cell.x * 3.14159 * 2.0) * sin(cell.y * 3.14159);
    color *= grid;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 0.88);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
