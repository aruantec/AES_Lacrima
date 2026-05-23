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

float rand(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

float3 ApplyDisplayScanlines(float3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

float Rect(float2 p, float2 origin, float2 size)
{
    float2 d = p - origin;
    return step(0.0, d.x) * step(0.0, d.y) * step(d.x, size.x) * step(d.y, size.y);
}

float SegmentLine(float2 p, float2 a, float2 b, float thickness)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-5));
    float d = length(pa - ba * h);
    return smoothstep(thickness, thickness * 0.45, d);
}

float DrawSevenSegDigit(float2 p, int digit)
{
    static const int patterns[10] = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };
    int bits = patterns[digit % 10];

    float s = 0.0;
    const float t = 0.055;
    if ((bits & 1) != 0) s += SegmentLine(p, float2(0.12, 0.88), float2(0.88, 0.88), t);
    if ((bits & 2) != 0) s += SegmentLine(p, float2(0.88, 0.88), float2(0.88, 0.52), t);
    if ((bits & 4) != 0) s += SegmentLine(p, float2(0.88, 0.48), float2(0.88, 0.12), t);
    if ((bits & 8) != 0) s += SegmentLine(p, float2(0.12, 0.12), float2(0.88, 0.12), t);
    if ((bits & 16) != 0) s += SegmentLine(p, float2(0.12, 0.48), float2(0.12, 0.12), t);
    if ((bits & 32) != 0) s += SegmentLine(p, float2(0.12, 0.88), float2(0.12, 0.52), t);
    if ((bits & 64) != 0) s += SegmentLine(p, float2(0.12, 0.48), float2(0.88, 0.48), t);
    return saturate(s);
}

float DrawColon(float2 p)
{
    float d1 = smoothstep(0.06, 0.02, length(p - float2(0.5, 0.68)));
    float d2 = smoothstep(0.06, 0.02, length(p - float2(0.5, 0.32)));
    return saturate(d1 + d2);
}

float DrawLetterP(float2 p) { return Rect(p, float2(0.1, 0.12), float2(0.18, 0.76)) + Rect(p, float2(0.1, 0.72), float2(0.55, 0.16)) + Rect(p, float2(0.48, 0.42), float2(0.18, 0.46)); }
float DrawLetterL(float2 p) { return Rect(p, float2(0.1, 0.12), float2(0.18, 0.76)) + Rect(p, float2(0.1, 0.12), float2(0.62, 0.16)); }
float DrawLetterA(float2 p) { return Rect(p, float2(0.38, 0.12), float2(0.18, 0.76)) + Rect(p, float2(0.1, 0.72), float2(0.72, 0.16)) + Rect(p, float2(0.1, 0.44), float2(0.72, 0.14)); }
float DrawLetterY(float2 p) { return Rect(p, float2(0.1, 0.44), float2(0.18, 0.44)) + Rect(p, float2(0.52, 0.44), float2(0.18, 0.44)) + Rect(p, float2(0.28, 0.12), float2(0.18, 0.36)); }

float DrawPlayLabel(float2 p, float ch)
{
    if (ch < 0.5) return DrawLetterP(p);
    if (ch < 1.5) return DrawLetterL(p);
    if (ch < 2.5) return DrawLetterA(p);
    return DrawLetterY(p);
}

float3 DrawVcrHud(float3 color, float2 screenUv, float2 outputSize, float t)
{
    float2 hudBR = float2(0.98, 0.98);
    float2 hudSize = float2(280.0, 72.0) / max(outputSize, float2(1.0, 1.0));
    float2 hudTL = hudBR - hudSize;
    float2 hp = (screenUv - hudTL) / max(hudSize, float2(1e-4, 1e-4));
    if (hp.x < 0.0 || hp.x > 1.0 || hp.y < 0.0 || hp.y > 1.0)
        return color;

    float panel = smoothstep(0.0, 0.02, hp.x) * smoothstep(0.0, 0.02, hp.y)
        * smoothstep(0.0, 0.02, 1.0 - hp.x) * smoothstep(0.0, 0.02, 1.0 - hp.y);
    float3 hudBg = float3(0.04, 0.035, 0.02);
    float3 amber = float3(1.0, 0.72, 0.18);
    float3 green = float3(0.35, 1.0, 0.45);

    color = lerp(color, hudBg, panel * 0.82);

    int totalSec = (int)floor(t);
    int mins = (totalSec / 60) % 100;
    int secs = totalSec % 60;
    int d0 = mins / 10; int d1 = mins % 10;
    int d2 = secs / 10; int d3 = secs % 10;

    float digitW = 0.11;
    float digitH = 0.42;
    float baseY = 0.48;
    float x0 = 0.06;

    float clockGlow = 0.0;
    float2 dUv;
    dUv = (hp - float2(x0 + digitW * 0.0, baseY)) / float2(digitW, digitH);
    clockGlow = max(clockGlow, DrawSevenSegDigit(dUv, d0));
    dUv = (hp - float2(x0 + digitW * 1.05, baseY)) / float2(digitW, digitH);
    clockGlow = max(clockGlow, DrawSevenSegDigit(dUv, d1));
    dUv = (hp - float2(x0 + digitW * 2.15, baseY)) / float2(digitW * 0.35, digitH);
    clockGlow = max(clockGlow, DrawColon(dUv) * 0.9);
    dUv = (hp - float2(x0 + digitW * 2.65, baseY)) / float2(digitW, digitH);
    clockGlow = max(clockGlow, DrawSevenSegDigit(dUv, d2));
    dUv = (hp - float2(x0 + digitW * 3.70, baseY)) / float2(digitW, digitH);
    clockGlow = max(clockGlow, DrawSevenSegDigit(dUv, d3));

    color += amber * clockGlow * panel * 1.15;

    float playBlink = step(0.5, frac(t * 1.5));
    for (int i = 0; i < 4; i++)
    {
        float2 lp = (hp - float2(0.52 + float(i) * 0.09, 0.14)) / float2(0.07, 0.22);
        float glyph = DrawPlayLabel(lp, (float)i);
        color += green * glyph * panel * (0.55 + 0.45 * playBlink);
    }

    float spLabel = Rect(hp, float2(0.06, 0.08), float2(0.04, 0.12))
        + Rect(hp, float2(0.11, 0.08), float2(0.04, 0.12));
    color += amber * spLabel * panel * 0.7;

    for (int b = 0; b < 5; b++)
    {
        float track = 0.35 + 0.55 * saturate(sin(t * 2.3 + b * 1.7) * 0.5 + 0.5);
        track *= saturate(sin(t * 9.0 + b * 2.1) * 0.5 + 0.55);
        float2 barOrigin = float2(0.72 + float(b) * 0.045, 0.18);
        float bar = Rect(hp, barOrigin, float2(0.028, 0.22 * track));
        color += lerp(amber, green, track) * bar * panel;
    }

    float recBlink = step(0.6, frac(t * 2.0));
    float recDot = smoothstep(0.04, 0.015, length(hp - float2(0.68, 0.78)));
    color += float3(1.0, 0.15, 0.1) * recDot * recBlink * panel;

    float recBar = Rect(hp, float2(0.71, 0.72), float2(0.12, 0.08));
    color += float3(1.0, 0.2, 0.12) * recBar * panel * 0.35;

    float labelSp = Rect(hp, float2(0.50, 0.74), float2(0.03, 0.06))
        + Rect(hp, float2(0.54, 0.74), float2(0.03, 0.06));
    color += amber * labelSp * panel * 0.5;

    return color;
}

float4 main(PSIn input) : SV_TARGET
{
    float iTime = timeSeconds;
    float2 uv = input.uv;
    float2 outputSize = float2(max(outputWidth, 1.0), max(outputHeight, 1.0));

    float jump = (rand(float2(iTime, 0.0)) > 0.992 ? 0.008 * sin(iTime * 20.0) : 0.0);
    uv.y = frac(uv.y + jump);

    float bandSpeed = 0.12 + 0.08 * sin(iTime * 0.4);
    float bandPos = frac(-iTime * bandSpeed);
    float bandDist = abs(uv.y - bandPos);
    float bandIntensity = smoothstep(0.12, 0.0, bandDist);

    float tear = (rand(float2(iTime, uv.y * 0.2)) - 0.5) * 0.05 * bandIntensity;
    uv.x += tear;

    float wobble = sin(iTime * 4.0 + uv.y * 10.0) * 0.0012;
    uv.x += wobble;

    float headArea = 0.08;
    float headMask = smoothstep(headArea, 0.0, uv.y);
    float headSwirl = sin(uv.y * 40.0 - iTime * 12.0) * 0.015 * headMask;
    uv.x += headSwirl;
    float headNoise = (rand(float2(iTime * 2.0, uv.y)) - 0.5) * 0.04 * headMask;
    uv.x += headNoise;

    float2 sampledUv = saturate(uv);
    float chroma = (0.003 + 0.001 * sin(iTime * 0.5)) / max(sourceWidth, 1.0) * 3.0;
    float3 color;
    color.r = src.Sample(samp, sampledUv + float2(chroma, 0.0)).r;
    color.g = src.Sample(samp, sampledUv).g;
    color.b = src.Sample(samp, sampledUv - float2(chroma, 0.0)).b;

    float snow = (rand(sampledUv + iTime) - 0.5) * 0.08;
    float bandStatic = (rand(sampledUv * 0.5 + iTime) - 0.5) * 0.5 * bandIntensity;
    color += (snow + bandStatic) * (1.0 + headMask);

    color = ApplyDisplayScanlines(color, input.pos.y, 0.12);

    float2 vigCenter = input.uv - 0.5;
    float vig = 1.0 - dot(vigCenter, vigCenter) * 0.4;
    color *= vig;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, saturation * 0.9);
    color *= brightness;
    color *= tint.rgb;

    color = DrawVcrHud(color, input.uv, outputSize, iTime);

    return float4(saturate(color), tint.a);
}
