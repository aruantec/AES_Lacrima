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
    vec2 center = uv - 0.5;
    float edgeGuard = EdgeSampleGuard(uv);

    float breathe = 0.018 * sin(t * 0.7) + 0.012 * sin(t * 1.3 + 1.0);
    float swirl = 0.06 * sin(t * 0.45);
    float c = cos(swirl);
    float s = sin(swirl);
    vec2 warped = vec2(c * center.x - s * center.y, s * center.x + c * center.y);
    warped *= 1.0 + breathe;
    vec2 wuv = mix(uv, warped + 0.5, edgeGuard);

    float chroma = 0.012 + 0.008 * sin(t * 1.1);
    vec3 color;
    color.r = SampleRgb(wuv, vec2(chroma * sin(t * 0.9), chroma * cos(t * 0.6))).r;
    color.g = SampleRgb(wuv, vec2(chroma * cos(t * 1.2), -chroma * sin(t * 0.8))).g;
    color.b = SampleRgb(wuv, vec2(-chroma, chroma * sin(t * 1.4))).b;

    float pulse = 0.5 + 0.5 * sin(t * 1.6);
    vec3 tintA = vec3(1.2, 0.5, 1.4);
    vec3 tintB = vec3(0.4, 1.3, 0.9);
    vec3 tintC = vec3(1.3, 0.9, 0.3);
    color *= mix(tintA, mix(tintB, tintC, pulse), 0.5 + 0.5 * sin(t * 0.55 + 2.0));

    float edge = length(center);
    float rainbow = sin(edge * 14.0 - t * 2.5) * 0.5 + 0.5;
    color += vec3(rainbow, 1.0 - rainbow, 0.5 + 0.5 * sin(t * 2.0)) * 0.12 * smoothstep(0.65, 0.15, edge) * edgeGuard;

    float glow = dot(SampleRgb(wuv), vec3(0.299, 0.587, 0.114));
    color += vec3(0.5, 0.2, 0.8) * glow * 0.2 * (0.6 + 0.4 * sin(t * 3.0));

    float soft = dot(SampleRgb(wuv, center * 0.02), vec3(0.333, 0.333, 0.333));
    color = mix(color, color * (1.0 + soft * 0.35), 0.35);

    vec2 vigCenter = uv - 0.5;
    color *= clamp(1.0 - dot(vigCenter, vigCenter) * 0.25, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 1.45);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
