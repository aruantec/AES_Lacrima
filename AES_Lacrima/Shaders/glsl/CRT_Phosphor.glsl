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

vec3 ApplyDisplayScanlines(vec3 color, float pixelY, float strength)
{
    float scan = sin(pixelY * 3.14159265);
    scan = scan * scan;
    return color * (1.0 - scan * strength);
}

vec2 BarrelUv(vec2 uv, float amount)
{
    vec2 centered = (uv - 0.5) * 2.0;
    float r2 = dot(centered, centered);
    vec2 warped = centered * (1.0 + amount * r2);
    return warped * 0.5 + 0.5;
}

vec3 SampleColor(vec2 uv)
{
    return texture(Texture, clamp(uv, 0.0, 1.0)).rgb;
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 texel = 1.0 / sourceSize;
    vec2 q = vTex;
    vec2 uv = BarrelUv(q, 0.08);

    float chroma = 1.8 / sourceSize.x;
    vec3 color;
    color.r = SampleColor(uv + vec2(chroma, 0.0)).r;
    color.g = SampleColor(uv).g;
    color.b = SampleColor(uv - vec2(chroma, 0.0)).b;

    vec3 blur =
        SampleColor(uv + vec2(texel.x, 0.0)) +
        SampleColor(uv - vec2(texel.x, 0.0)) +
        SampleColor(uv + vec2(0.0, texel.y)) +
        SampleColor(uv - vec2(0.0, texel.y));
    blur *= 0.25;

    vec3 glow = max(blur - 0.35, 0.0);
    color += glow * 0.18;

    float x = gl_FragCoord.x;
    vec3 mask = vec3(
        0.5 + 0.5 * sin(x * 3.14159265),
        0.5 + 0.5 * sin(x * 3.14159265 + 2.094),
        0.5 + 0.5 * sin(x * 3.14159265 + 4.189));
    mask = mix(vec3(1.0, 1.0, 1.0), mask, 0.28);
    color *= mask;

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.20);

    vec2 dist = q - 0.5;
    color *= clamp(1.0 - dot(dist, dist) * 0.42, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color = pow(clamp(color, 0.0, 1.0), vec3(0.96));
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
