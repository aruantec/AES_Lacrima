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

void main()
{
    vec2 sourceSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 uv = vTex;
    float chroma = 1.5 / sourceSize.x;

    vec3 color;
    color.r = texture(Texture, uv + vec2(chroma, 0.0)).r;
    color.g = texture(Texture, uv).g;
    color.b = texture(Texture, uv - vec2(chroma, 0.0)).b;

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.24);

    float slot = 0.94 + 0.06 * sin(gl_FragCoord.x * 3.14159265);
    color *= slot;

    vec2 dist = uv - 0.5;
    float vignette = clamp(1.0 - dot(dist, dist) * 0.35, 0.0, 1.0);
    color *= vignette;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 1.08);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(color, tint.a);
}

#endif
