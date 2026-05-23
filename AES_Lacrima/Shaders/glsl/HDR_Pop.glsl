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

vec3 AcesTonemap(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

vec3 ApplyVibrance(vec3 color, float amount)
{
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
    float boost = amount * (1.0 - sat);
    return mix(vec3(luma, luma, luma), color, 1.0 + boost);
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 uv = vTex;

    vec3 center = SampleColor(uv);
    vec3 blur =
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));
    blur *= 0.25;

    vec3 highlights = max(center - 0.38, 0.0);
    vec3 glow = max(blur - 0.30, 0.0);
    vec3 color = center + highlights * 0.85 + glow * 0.55;

    color = AcesTonemap(color * 1.12);

    vec3 graded;
    graded.r = dot(color, vec3(1.08, 0.06, -0.02));
    graded.g = dot(color, vec3(-0.02, 1.04, 0.04));
    graded.b = dot(color, vec3(0.02, 0.06, 1.10));
    color = mix(color, graded, 0.72);

    float shadowLift = smoothstep(0.0, 0.35, dot(color, vec3(0.333, 0.333, 0.333)));
    color += vec3(0.02, 0.04, 0.06) * (1.0 - shadowLift);
    color += vec3(0.06, 0.03, 0.0) * smoothstep(0.55, 1.0, dot(color, vec3(0.299, 0.587, 0.114)));

    color = ApplyVibrance(color, 0.55 * saturation);
    color = pow(clamp(color, 0.0, 1.0), 0.92);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 1.18);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
