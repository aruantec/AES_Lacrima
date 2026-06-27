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

float3 FetchPixel(int2 pos, float2 sourceSize)
{
    float2 uv = (float2(pos) + 0.5) / sourceSize;
    return src.Sample(samp, saturate(uv)).rgb;
}

bool SameColor(float3 a, float3 b)
{
    return all(abs(a - b) < (1.0 / 255.0));
}

float4 main(PSIn input) : SV_TARGET
{
    float2 sourceSize = float2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    float2 coord = input.uv * sourceSize;
    int2 fp = int2(floor(coord));
    float2 fc = frac(coord);

    float3 p = FetchPixel(fp, sourceSize);
    float3 b = FetchPixel(fp + int2(0, -1), sourceSize);
    float3 d = FetchPixel(fp + int2(-1, 0), sourceSize);
    float3 bottom = FetchPixel(fp + int2(0, 1), sourceSize);
    float3 right = FetchPixel(fp + int2(1, 0), sourceSize);

    float3 color = p;
    bool top = fc.y < 0.5;
    bool left = fc.x < 0.5;

    if (top && left)
    {
        if (SameColor(d, b) && !SameColor(b, bottom) && !SameColor(d, right))
            color = d;
    }
    else if (top && !left)
    {
        if (SameColor(b, bottom) && !SameColor(b, d) && !SameColor(bottom, right))
            color = bottom;
    }
    else if (!top && left)
    {
        if (SameColor(d, right) && !SameColor(d, b) && !SameColor(right, bottom))
            color = d;
    }
    else
    {
        if (SameColor(right, bottom) && !SameColor(d, right) && !SameColor(bottom, b))
            color = right;
    }

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    return float4(saturate(color), tint.a);
}
