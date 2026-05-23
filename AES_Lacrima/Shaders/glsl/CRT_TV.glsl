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

vec2 crt(vec2 uv)
{
    uv = (uv - 0.5) * 2.0;
    uv *= 1.1;
    uv.x *= 1.0 + pow(abs(uv.y) / 5.0, 2.0);
    uv.y *= 1.0 + pow(abs(uv.x) / 4.0, 2.0);
    uv = (uv * 0.5) + 0.5;
    uv = uv * 0.92 + 0.04;
    return clamp(uv, 0.0, 1.0);
}

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 q = vTex;
    vec2 uv = crt(q);

    float chroma = 1.5 / sourceSize.x;
    vec3 color;
    color.r = texture(Texture, uv + vec2(chroma, 0.0)).r;
    color.g = texture(Texture, uv).g;
    color.b = texture(Texture, uv - vec2(chroma, 0.0)).b;

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.22);

    vec2 centerDist = q - 0.5;
    float vignette = 1.0 - dot(centerDist, centerDist) * 0.5;
    color *= clamp(vignette, 0.0, 1.0);

    vec2 p = -1.0 + 2.0 * q;
    float f = (1.0 - p.x * p.x) * (1.0 - p.y * p.y);
    float frameShape = 0.35;
    float frameLimit = 0.30;
    float frameSharpness = 1.10;
    float frame = saturate(frameSharpness * (pow(f, frameShape) - frameLimit));
    color *= frame;

    color *= brightness;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= tint.rgb;

    fragColor = vec4(color, tint.a);
}

#endif
