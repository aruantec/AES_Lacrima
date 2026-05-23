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
    vec3 color = texture(Texture, vTex).rgb;

    color = pow(clamp(color, 0.0, 1.0), 1.5);

    vec3 matrixed;
    matrixed.r = dot(color, vec3(0.84, 0.16, 0.0));
    matrixed.g = dot(color, vec3(0.08, 0.77, 0.15));
    matrixed.b = dot(color, vec3(0.15, 0.0, 0.81));
    color = pow(clamp(matrixed, 0.0, 1.0), 1.0 / 1.2);

    vec2 texSize = vec2(max(sourceWidth, 1.0), max(sourceHeight, 1.0));
    vec2 cell = fract(vTex * texSize);
    float grid = 0.92 + 0.08 * sin(cell.x * 3.14159 * 2.0) * sin(cell.y * 3.14159);
    color *= grid;

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma, luma, luma), color, saturation * 0.88);
    color *= brightness;
    color *= tint.rgb;

    fragColor = vec4(clamp(color, 0.0, 1.0), tint.a);
}

#endif
