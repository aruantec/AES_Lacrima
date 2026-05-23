#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;

void main()
{
    vTex = TexCoord;
    gl_Position = vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform float FrameCount;
uniform float FrameDirection;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

#define timeSeconds (FrameCount / 60.0)
#define brightness uBrightness
#define saturation uSaturation
#define tint uColorTint
#define sourceWidth TextureSize.x
#define sourceHeight TextureSize.y
#define outputWidth OutputSize.x
#define outputHeight OutputSize.y
#define sourceIsSrgb 1.0

float rand(vec2 co)
{
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

float FilmGrain(vec2 uv, vec2 outputSize, float t, float luma)
{
    vec2 px = uv * outputSize;
    vec2 seed = px * 1.55 + vec2(t * 37.1, t * 19.7);
    float fine = rand(seed) - 0.5;
    float coarse = rand(floor(seed * 0.32) + t * 4.0) - 0.5;
    float grain = fine * 0.74 + coarse * 0.26;
    float lumaMask = 0.55 + 0.45 * smoothstep(0.0, 0.18, luma) * (1.0 - smoothstep(0.82, 1.0, luma) * 0.35);
    return grain * lumaMask;
}

vec3 ApplyFilmGrain(vec3 color, vec2 uv, vec2 outputSize, float t)
{
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    float grain = FilmGrain(uv, outputSize, t, luma);
    vec2 px = uv * outputSize;
    vec3 chromaGrain;
    chromaGrain.r = (rand(px * 2.05 + t * 23.0) - 0.5);
    chromaGrain.g = (rand(px * 1.72 - t * 19.0) - 0.5);
    chromaGrain.b = (rand(px * 1.91 + t * 27.0) - 0.5);
    float mask = 0.62 + 0.38 * smoothstep(0.0, 0.25, luma);
    return color + grain * 0.075 + chromaGrain * 0.025 * mask;
}

vec3 ApplyVhsSignalNoise(vec3 color, vec2 uv, vec2 outputSize, float t, float headMask, float bandIntensity)
{
    vec2 px = uv * outputSize;
    float hiss = (rand(px * 1.35 + vec2(t * 41.0, t * 29.0)) - 0.5) * 0.09;
    float blockHiss = (rand(floor(px * 0.72) + t * 6.5) - 0.5) * 0.062;
    float snow = (rand(uv * 640.0 + t * 13.0) - 0.5) * 0.08;
    float bandStatic = (rand(uv * 320.0 + t * 9.0) - 0.5) * 0.55 * bandIntensity;
    float sparkle = step(0.988, rand(floor(px * 0.45) + t * 2.7)) * (rand(px + t) * 0.30 + 0.06);
    float dropout = step(0.996, rand(vec2(t * 0.35, floor(uv.y * 120.0)))) * (rand(uv + t) - 0.2) * 0.48;
    float noise = hiss + blockHiss + snow + bandStatic + sparkle + dropout;
    noise *= 1.0 + headMask * 0.24;
    return color + noise;
}

vec3 ApplyDisplayScanlines(vec3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

float Rect(vec2 p, vec2 origin, vec2 size)
{
    vec2 d = p - origin;
    return step(0.0, d.x) * step(0.0, d.y) * step(d.x, size.x) * step(d.y, size.y);
}

// 5x7 OSD font (row 0 = top of glyph box). Each row is 5 bits, MSB = left column.
float Font5x7Glyph(vec2 p, uint r0, uint r1, uint r2, uint r3, uint r4, uint r5, uint r6)
{
    p = clamp(p, 0.0, 1.0);
    int col = clamp((int)(p.x * 5.0), 0, 4);
    int row = clamp((int)(p.y * 7.0), 0, 6);
    uint bits = r0;
    if (row == 1) bits = r1;
    else if (row == 2) bits = r2;
    else if (row == 3) bits = r3;
    else if (row == 4) bits = r4;
    else if (row == 5) bits = r5;
    else if (row == 6) bits = r6;
    return float((bits >> (4 - col)) & 1u);
}

float DrawHudCharP(vec2 p) { return Font5x7Glyph(p, 31u, 17u, 17u, 30u, 16u, 16u, 16u); }
float DrawHudCharL(vec2 p) { return Font5x7Glyph(p, 16u, 16u, 16u, 16u, 16u, 16u, 31u); }
float DrawHudCharA(vec2 p) { return Font5x7Glyph(p, 14u, 17u, 17u, 31u, 17u, 17u, 17u); }
float DrawHudCharY(vec2 p) { return Font5x7Glyph(p, 17u, 17u, 17u, 14u, 4u, 4u, 4u); }
float DrawHudCharS(vec2 p) { return Font5x7Glyph(p, 15u, 16u, 16u, 14u, 1u, 1u, 30u); }

float DrawHudChar(vec2 p, int code)
{
    if (code == 0) return DrawHudCharP(p);
    if (code == 1) return DrawHudCharL(p);
    if (code == 2) return DrawHudCharA(p);
    if (code == 3) return DrawHudCharY(p);
    if (code == 4) return DrawHudCharS(p);
    return 0.0;
}

float DrawHudDigit(vec2 p, int digit)
{
    digit = digit % 10;
    if (digit == 0) return Font5x7Glyph(p, 31u, 17u, 17u, 17u, 17u, 17u, 31u);
    if (digit == 1) return Font5x7Glyph(p, 12u, 12u, 4u, 4u, 4u, 4u, 14u);
    if (digit == 2) return Font5x7Glyph(p, 30u, 1u, 1u, 30u, 16u, 16u, 31u);
    if (digit == 3) return Font5x7Glyph(p, 30u, 1u, 1u, 14u, 1u, 1u, 30u);
    if (digit == 4) return Font5x7Glyph(p, 18u, 18u, 18u, 31u, 2u, 2u, 2u);
    if (digit == 5) return Font5x7Glyph(p, 31u, 16u, 16u, 30u, 1u, 1u, 30u);
    if (digit == 6) return Font5x7Glyph(p, 14u, 16u, 16u, 30u, 17u, 17u, 14u);
    if (digit == 7) return Font5x7Glyph(p, 31u, 1u, 2u, 4u, 8u, 16u, 16u);
    if (digit == 8) return Font5x7Glyph(p, 31u, 17u, 17u, 31u, 17u, 17u, 31u);
    return Font5x7Glyph(p, 14u, 17u, 17u, 14u, 1u, 1u, 14u);
}

float DrawHudColon(vec2 p)
{
    return Rect(p, vec2(0.34, 0.60), vec2(0.32, 0.16))
         + Rect(p, vec2(0.34, 0.24), vec2(0.32, 0.16));
}

float DrawPlayTriangle(vec2 p)
{
    vec2 v0 = vec2(0.07, 0.08);
    vec2 v1 = vec2(0.07, 0.92);
    vec2 v2 = vec2(0.96, 0.50);
    vec2 e0 = v1 - v0;
    vec2 e1 = v2 - v1;
    vec2 e2 = v0 - v2;
    vec2 c0 = p - v0;
    vec2 c1 = p - v1;
    vec2 c2 = p - v2;
    float s0 = e0.x * c0.y - e0.y * c0.x;
    float s1 = e1.x * c1.y - e1.y * c1.x;
    float s2 = e2.x * c2.y - e2.y * c2.x;
    float inside = step(0.0, s0) * step(0.0, s1) * step(0.0, s2);
    float outside = step(s0, 0.0) * step(s1, 0.0) * step(s2, 0.0);
    return clamp(inside + outside, 0.0, 1.0);
}

vec2 HudLocalUv(vec2 screenUv, vec2 outputSize, vec2 originPx, vec2 sizePx)
{
    vec2 px = screenUv * outputSize;
    return (px - originPx) / max(sizePx, vec2(1.0, 1.0));
}

float HudGlyphInBox(vec2 localUv)
{
    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return 0.0;
    return 1.0;
}

vec3 ApplyHudGlyph(vec3 color, float glyph)
{
    vec3 white = vec3(0.94, 0.96, 0.98);
    float glow = glyph * 0.92;
    color = mix(color, white, glow);
    color.r += glyph * 0.03;
    color.b += glyph * 0.04;
    return color;
}

vec3 DrawVcrHud(vec3 color, vec2 screenUv, vec2 outputSize, float t)
{
    float scale = max(outputSize.y, 480.0) / 720.0;
    float margin = 22.0 * scale;
    vec3 hudColor = color;

    // Top-left: PLAY + solid triangle (VCR OSD style).
    float letterW = 22.0 * scale;
    float letterH = 30.0 * scale;
    float letterGap = 4.0 * scale;
    vec2 playOrigin = vec2(margin, margin);

    vec2 luvP = HudLocalUv(screenUv, outputSize, playOrigin, vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(luvP) * DrawHudChar(luvP, 0));
    vec2 luvL = HudLocalUv(screenUv, outputSize, playOrigin + vec2(letterW + letterGap, 0.0), vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(luvL) * DrawHudChar(luvL, 1));
    vec2 luvA = HudLocalUv(screenUv, outputSize, playOrigin + vec2((letterW + letterGap) * 2.0, 0.0), vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(luvA) * DrawHudChar(luvA, 2));
    vec2 luvY = HudLocalUv(screenUv, outputSize, playOrigin + vec2((letterW + letterGap) * 3.0, 0.0), vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(luvY) * DrawHudChar(luvY, 3));

    float triW = 36.0 * scale;
    float triH = letterH * 1.06;
    vec2 triOrigin = playOrigin + vec2((letterW + letterGap) * 4.0 + 8.0 * scale, (letterH - triH) * 0.5);
    vec2 tuv = HudLocalUv(screenUv, outputSize, triOrigin, vec2(triW, triH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(tuv) * DrawPlayTriangle(tuv));

    // Bottom-left: SP.
    vec2 spOrigin = vec2(margin, outputSize.y - margin - letterH);
    vec2 sUv = HudLocalUv(screenUv, outputSize, spOrigin, vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(sUv) * DrawHudChar(sUv, 4));
    vec2 pUv = HudLocalUv(screenUv, outputSize, spOrigin + vec2(letterW + letterGap, 0.0), vec2(letterW, letterH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(pUv) * DrawHudChar(pUv, 0));

    // Bottom-right: HH:MM:SS timecode (e.g. 00:00:03).
    int totalSec = (int)floor(t);
    int h0 = (totalSec / 3600) % 100 / 10;
    int h1 = (totalSec / 3600) % 10;
    int m0 = (totalSec / 60) % 60 / 10;
    int m1 = (totalSec / 60) % 10;
    int s0 = (totalSec % 60) / 10;
    int s1 = totalSec % 10;

    float digitW = letterW;
    float digitH = letterH;
    float colonW = 12.0 * scale;
    float digitGap = 2.0 * scale;
    float clockW = digitW * 6.0 + colonW * 2.0 + digitGap * 5.0;
    vec2 clockOrigin = vec2(outputSize.x - margin - clockW, outputSize.y - margin - digitH);
    float x = clockOrigin.x;

    vec2 duv;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, h0));
    x += digitW + digitGap;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, h1));
    x += digitW + digitGap;
    vec2 cuv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(colonW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(cuv) * DrawHudColon(cuv));
    x += colonW + digitGap;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, m0));
    x += digitW + digitGap;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, m1));
    x += digitW + digitGap;
    cuv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(colonW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(cuv) * DrawHudColon(cuv));
    x += colonW + digitGap;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, s0));
    x += digitW + digitGap;
    duv = HudLocalUv(screenUv, outputSize, vec2(x, clockOrigin.y), vec2(digitW, digitH));
    hudColor = ApplyHudGlyph(hudColor, HudGlyphInBox(duv) * DrawHudDigit(duv, s1));

    return hudColor;
}

void main()
{
    float iTime = timeSeconds;
    vec2 uv = vTex;
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));

    float jump = (rand(vec2(iTime, 0.0)) > 0.992 ? 0.008 * sin(iTime * 20.0) : 0.0);
    uv.y = fract(uv.y + jump);

    float bandSpeed = 0.06 + 0.04 * sin(iTime * 0.4);
    float bandPos = fract(-iTime * bandSpeed);
    float bandDist = abs(uv.y - bandPos);
    float bandIntensity = smoothstep(0.12, 0.0, bandDist);

    float tear = (rand(vec2(iTime, uv.y * 0.2)) - 0.5) * 0.05 * bandIntensity;
    uv.x += tear;

    float wobble = sin(iTime * 4.0 + uv.y * 10.0) * 0.0012;
    uv.x += wobble;

    float headArea = 0.08;
    float headMask = smoothstep(headArea, 0.0, uv.y);
    float headSwirl = sin(uv.y * 40.0 - iTime * 6.0) * 0.015 * headMask;
    uv.x += headSwirl;
    float headNoise = (rand(vec2(iTime * 1.0, uv.y)) - 0.5) * 0.04 * headMask;
    uv.x += headNoise;

    vec2 sampledUv = clamp(uv, 0.0, 1.0);
    float chroma = (0.003 + 0.001 * sin(iTime * 0.5)) / max(sourceWidth, 1.0) * 3.0;
    vec3 color;
    color.r = texture(Texture, sampledUv + vec2(chroma, 0.0)).r;
    color.g = texture(Texture, sampledUv).g;
    color.b = texture(Texture, sampledUv - vec2(chroma, 0.0)).b;

    float snow = (rand(sampledUv + iTime) - 0.5) * 0.09;
    float bandStatic = (rand(sampledUv * 0.5 + iTime) - 0.5) * 0.50 * bandIntensity;
    color += (snow + bandStatic) * (1.0 + headMask);

    color = ApplyFilmGrain(color, sampledUv, outputSize, iTime);
    color = ApplyVhsSignalNoise(color, sampledUv, outputSize, iTime, headMask, bandIntensity);

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.12);

    float postGrain = (rand(vTex * outputSize * 2.4 + iTime * 5.5) - 0.5) * 0.030;
    color += postGrain;

    vec2 vigCenter = vTex - 0.5;
    float vig = 1.0 - dot(vigCenter, vigCenter) * 0.4;
    color *= vig;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 0.9);
    color *= brightness;
    color *= tint.rgb;

    color = DrawVcrHud(color, vTex, outputSize, iTime);

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
