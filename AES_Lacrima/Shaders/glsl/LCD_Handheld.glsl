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
    vec2 texSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 pixel = vTex * texSize;
    vec2 cell = fract(pixel);

    vec3 color = texture(Texture, vTex).rgb;

    float rowMask = mix(0.88, 1.04, smoothstep(0.10, 0.55, sin(cell.y * 3.14159)));
    float columnMask = 0.92 + 0.08 * sin(cell.x * 3.14159 * 3.0);
    color *= rowMask * columnMask;

    vec3 lcdTint = vec3(0.90, 1.02, 0.86);
    color *= lcdTint;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 0.92);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(color, tint.a);
}

#endif
