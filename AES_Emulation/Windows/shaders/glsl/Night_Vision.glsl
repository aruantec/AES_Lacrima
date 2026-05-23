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

void main()
{
    float t = timeSeconds;
    vec2 uv = vTex;
    vec2 texel = 1.0 / vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));

    vec2 center = vec2(0.5, 0.5);
    vec2 fromCenter = uv - center;
    float dist = length(fromCenter);

    float vignette = smoothstep(0.85, 0.25, dist);
    float scopeMask = smoothstep(1.05, 0.72, dist);

    float luma = dot(texture(Texture, uv).rgb, vec3(0.299, 0.587, 0.114));
    luma += dot(texture(Texture, uv + texel * 0.5).rgb, vec3(0.299, 0.587, 0.114)) * 0.15;
    luma = clamp(luma * 1.35 + 0.04, 0.0, 1.0);

    float noise = (rand(uv * vec2(640.0, 360.0) + t * 37.0) - 0.5) * 0.12;
    float staticBurst = step(0.992, rand(vec2(floor(t * 8.0), 0.0))) * 0.25;
    luma += noise + staticBurst;

    float scan = sin(gl_FragCoord.y * 3.14159265);
    scan = scan * scan;
    luma *= 1.0 - scan * 0.08;

    vec3 nvGreen = vec3(0.15, 1.0, 0.22);
    vec3 nvDim = vec3(0.02, 0.35, 0.06);
    vec3 color = mix(nvDim, nvGreen, pow(luma, 0.82));

    float hotSpot = smoothstep(0.55, 0.95, luma);
    color += vec3(0.35, 1.0, 0.4) * hotSpot * 0.45;

    float ring = smoothstep(0.02, 0.0, abs(dist - 0.68)) * 0.35;
    color += nvGreen * ring;

    color *= vignette;
    color *= scopeMask;

    float crosshair = 0.0;
    vec2 ch = abs(fromCenter);
    crosshair += smoothstep(0.004, 0.0, ch.x) * step(ch.y, 0.18) * 0.25;
    crosshair += smoothstep(0.004, 0.0, ch.y) * step(ch.x, 0.18) * 0.25;
    color += nvGreen * crosshair * scopeMask;

    float lumaOut = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(lumaOut, lumaOut, lumaOut), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
