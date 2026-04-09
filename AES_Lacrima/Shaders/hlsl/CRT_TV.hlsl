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

float2 crt(float2 uv)
{
    uv = (uv - 0.5) * 2.0;
    uv *= 1.1;
    uv.x *= 1.0 + pow(abs(uv.y) / 5.0, 2.0);
    uv.y *= 1.0 + pow(abs(uv.x) / 4.0, 2.0);
    uv = (uv / 2.0) + 0.5;
    uv = uv * 0.92 + 0.04;
    return saturate(uv);
}

float4 main(PSIn input) : SV_TARGET
{
    float2 textureSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 inputSize = textureSize;
    float2 q = input.uv * textureSize / inputSize;
    float2 uv = crt(q) * inputSize / textureSize;

    float chroma = 0.0025;
    float3 color;
    color.r = src.Sample(samp, uv + float2(chroma, 0.0)).r;
    color.g = src.Sample(samp, uv).g;
    color.b = src.Sample(samp, uv - float2(chroma, 0.0)).b;

    float scanWave = sin(uv.y * textureSize.y * 6.28318);
    scanWave *= scanWave;
    float scanline = 1.0 - (0.5 * scanWave * 0.8);
    color *= scanline;

    float2 centerDist = q - 0.5;
    float vignette = 1.0 - dot(centerDist, centerDist) * 0.5;
    color *= saturate(vignette);

    float2 p = -1.0 + 2.0 * q;
    float f = (1.0 - p.x * p.x) * (1.0 - p.y * p.y);
    float frameShape = 0.35;
    float frameLimit = 0.30;
    float frameSharpness = 1.10;
    float frame = saturate(frameSharpness * (pow(f, frameShape) - frameLimit));
    color *= frame;

    color *= brightness;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= tint.rgb;

    return float4(color, tint.a);
}
