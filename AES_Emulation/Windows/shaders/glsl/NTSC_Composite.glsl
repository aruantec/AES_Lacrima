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
    vec2 uv = vTex;
    vec2 texel = 1.0 / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    vec3 c0 = texture(Texture, uv).rgb;
    vec3 blur =
        texture(Texture, uv + vec2(texel.x * 2.0, 0.0)).rgb +
        texture(Texture, uv - vec2(texel.x * 2.0, 0.0)).rgb +
        texture(Texture, uv + vec2(0.0, texel.y)).rgb +
        texture(Texture, uv - vec2(0.0, texel.y)).rgb;
    blur *= 0.25;

    vec3 color = mix(c0, blur, 0.42);

    float bleedR = texture(Texture, uv + vec2(texel.x * 3.0, 0.0)).r;
    float bleedB = texture(Texture, uv - vec2(texel.x * 3.0, 0.0)).b;
    color.r = mix(color.r, bleedR, 0.28);
    color.b = mix(color.b, bleedB, 0.28);

    color = ApplyDisplayScanlines(color, gl_FragCoord.y, 0.16);

    vec2 dist = uv - 0.5;
    color *= clamp(1.0 - dot(dist, dist) * 0.3, 0.0, 1.0);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 0.95);
    color = pow(clamp(color, 0.0, 1.0), 0.98);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(color, tint.a);
}

#endif
