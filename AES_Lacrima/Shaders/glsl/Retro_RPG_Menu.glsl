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

vec3 SampleColor(vec2 uv)
{
    return texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
}

float GetLuma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

float GetSaturation(vec3 color)
{
    float mx = max(max(color.r, color.g), color.b);
    float mn = min(min(color.r, color.g), color.b);
    return mx - mn;
}

vec2 PixelateUv(vec2 uv, float blocks)
{
    vec2 blockUv = floor(uv * blocks) + 0.5;
    return blockUv / blocks;
}

vec3 GradeRetroMenu(vec3 color)
{
    float luma = GetLuma(color);
    float sat = GetSaturation(color);

    vec3 shadowCol = vec3(0.04, 0.02, 0.07);
    vec3 midCol = vec3(0.22, 0.1, 0.34);
    vec3 panelCol = vec3(0.42, 0.24, 0.58);
    vec3 textCol = vec3(0.72, 0.62, 0.86);
    vec3 goldCol = vec3(0.95, 0.78, 0.2);

    vec3 graded = mix(shadowCol, midCol, smoothstep(0.0, 0.32, luma));
    graded = mix(graded, panelCol, smoothstep(0.22, 0.58, luma));
    graded = mix(graded, textCol, smoothstep(0.45, 0.78, luma));
    graded = mix(graded, goldCol, smoothstep(0.72, 0.98, luma));

    float coloredText = smoothstep(0.1, 0.42, sat) * smoothstep(0.18, 0.95, luma);
    vec3 preserved = color;
    preserved.r = mix(preserved.r, preserved.r * 1.05, step(preserved.g, preserved.r));
    preserved.g = mix(preserved.g, preserved.g * 1.08, step(max(preserved.r, preserved.b), preserved.g));
    preserved.b = mix(preserved.b, preserved.b * 1.08, step(preserved.r, preserved.b));

    vec3 result = mix(graded, preserved, coloredText * 0.72);

    const float levels = 5.0;
    float ql = floor(luma * levels + 0.0001) / (levels - 1.0);
    result = mix(result, result * (ql / max(luma, 0.001)), 0.5);

    return result;
}

void main()
{
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float blocks = min(outputSize.x, outputSize.y) / 2.4;
    blocks = clamp(blocks, 64.0, 320.0);

    vec2 uv = PixelateUv(vTex, blocks);
    vec2 texel = 1.0 / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    vec3 color = SampleColor(uv);
    float luma = GetLuma(color);

    color = GradeRetroMenu(color);

    float edge =
        abs(luma - GetLuma(SampleColor(uv + vec2(texel.x, 0.0)))) +
        abs(luma - GetLuma(SampleColor(uv - vec2(texel.x, 0.0)))) +
        abs(luma - GetLuma(SampleColor(uv + vec2(0.0, texel.y)))) +
        abs(luma - GetLuma(SampleColor(uv - vec2(0.0, texel.y))));
    float border = smoothstep(0.12, 0.42, edge);
    color = mix(color, vec3(0.03, 0.01, 0.06), border * 0.35);
    color += vec3(0.75, 0.58, 0.12) * smoothstep(0.55, 0.95, border) * smoothstep(0.45, 0.95, luma) * 0.18;

    vec2 cell = fract(vTex * blocks);
    float grid = smoothstep(0.94, 1.0, max(cell.x, cell.y));
    color *= 1.0 - grid * 0.06;

    float lumaOut = GetLuma(color);
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 1.08);
    color = pow(clamp(color, 0.0, 1.0), 0.97);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
