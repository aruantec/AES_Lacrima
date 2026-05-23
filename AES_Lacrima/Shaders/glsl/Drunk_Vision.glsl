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

float EdgeSampleGuard(vec2 uv)
{
    vec2 c = abs(uv - 0.5) * 2.0;
    float d = max(c.x, c.y);
    return 1.0 - smoothstep(0.90, 1.0, d);
}

vec3 SampleRgb(vec2 uv, vec2 offset)
{
    float guard = EdgeSampleGuard(uv);
    return texture(Texture, clamp(uv + offset * guard, 0.0, 1.0)).rgb;
}

vec3 SampleRgb(vec2 uv)
{
    return SampleRgb(uv, vec2(0.0, 0.0));
}

void main()
{
    float t = timeSeconds;
    vec2 uv = vTex;
    float edgeGuard = EdgeSampleGuard(uv);

    float swayX = 0.022 * sin(t * 0.85) + 0.014 * sin(t * 1.7 + 2.0);
    float swayY = 0.018 * cos(t * 0.65 + 1.0) + 0.010 * sin(t * 2.1);
    float roll = 0.025 * sin(t * 0.5);
    float c = cos(roll);
    float s = sin(roll);
    vec2 centered = uv - 0.5;
    vec2 rocked = vec2(c * centered.x - s * centered.y, s * centered.x + c * centered.y);
    vec2 baseUv = mix(uv, rocked + vec2(swayX, swayY) + 0.5, edgeGuard);

    float blurAmt = 0.004 + 0.002 * sin(t * 0.9);
    vec2 texel = blurAmt / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    vec3 sharp = SampleRgb(baseUv);
    vec3 blurred =
        SampleRgb(baseUv, vec2(texel.x, 0.0)) +
        SampleRgb(baseUv, vec2(-texel.x, 0.0)) +
        SampleRgb(baseUv, vec2(0.0, texel.y)) +
        SampleRgb(baseUv, vec2(0.0, -texel.y)) +
        SampleRgb(baseUv, texel) +
        SampleRgb(baseUv, -texel);
    blurred /= 6.0;

    vec3 color = mix(sharp, blurred, 0.62);

    float doubleStrength = 0.35 + 0.12 * sin(t * 1.2);
    vec2 ghostOffset = vec2(0.018 * sin(t * 0.7), 0.012 * cos(t * 1.1)) * doubleStrength;
    vec3 ghost = SampleRgb(baseUv, ghostOffset);
    color = mix(color, ghost, 0.42);

    vec2 secondGhost = vec2(-0.014 * cos(t * 0.9), 0.016 * sin(t * 0.6)) * doubleStrength;
    color = mix(color, SampleRgb(baseUv, secondGhost), 0.22);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 0.72);

    float warmShift = 0.04 * sin(t * 0.4);
    color.r += warmShift;
    color.b -= warmShift * 0.6;
    color = clamp(color * vec3(1.02, 0.98, 0.94), 0.0, 1.0);

    float tunnel = 1.0 - dot(centered, centered) * 0.55;
    color *= clamp(tunnel, 0.0, 1.0);

    float wobbleLine = sin(uv.y * 40.0 + t * 3.0) * 0.015 * edgeGuard;
    color *= 1.0 + wobbleLine;

    float nausea = 0.5 + 0.5 * sin(t * 0.35);
    color.g *= 0.96 + nausea * 0.04;
    color.r *= 1.0 + nausea * 0.03;

    float lumaOut = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation * 0.75);
    color *= brightness * 0.96;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
