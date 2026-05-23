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

void main()
{
    vec2 outputSize = vec2(max(outputWidth, 1.0), max(outputHeight, 1.0));
    float blocks = min(outputSize.x, outputSize.y) / 3.5;
    blocks = clamp(blocks, 96.0, 420.0);

    vec2 blockUv = floor(vTex * blocks) + 0.5;
    vec2 snappedUv = blockUv / blocks;

    vec3 color = texture(Texture, snappedUv).rgb;

    float row = mod(blockUv.y, 2.0);
    color *= row < 1.0 ? 1.0 : 0.9;

    vec2 cell = fract(vTex * blocks);
    float grid = smoothstep(0.92, 1.0, max(cell.x, cell.y)) + smoothstep(0.08, 0.0, min(cell.x, cell.y));
    color *= 1.0 - grid * 0.08;

    color = pow(clamp(color, 0.0, 1.0), 0.96);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
