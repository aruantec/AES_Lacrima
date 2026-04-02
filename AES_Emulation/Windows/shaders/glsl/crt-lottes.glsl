#version 150

#ifdef VERTEX
layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;
uniform mat4 MVPMatrix;

void main() {
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}
#endif

#ifdef FRAGMENT
uniform sampler2D Texture;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

// CRT-Lottes parameters
#define hardScan -8.0
#define hardPix -3.0
#define maskDark 0.5
#define maskLight 1.5

float ToLinear1(float c) { return (c <= 0.04045) ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
vec3 ToLinear(vec3 c) { return vec3(ToLinear1(c.r), ToLinear1(c.g), ToLinear1(c.b)); }
float ToSRGB1(float c) { return (c <= 0.0031308) ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055; }
vec3 ToSRGB(vec3 c) { return vec3(ToSRGB1(c.r), ToSRGB1(c.g), ToSRGB1(c.b)); }

vec3 Filter(vec2 pos) {
    vec2 res = InputSize;
    pos = pos * res;
    vec2 pix = floor(pos);
    vec2 f = pos - pix;
    return ToLinear(texture(Texture, (pix + 0.5) / res).rgb);
}

void main() {
    vec2 pos = vTex;
    
    // Scanline weight
    float dst = fract(pos.y * InputSize.y);
    float scan = exp2(hardScan * dst * (1.0 - dst));
    
    // Pixel weight
    float dpx = fract(pos.x * InputSize.x);
    float pxl = exp2(hardPix * dpx * (1.0 - dpx));
    
    vec3 color = Filter(pos);
    color *= scan * pxl * 2.5; // Intensity boost
    
    // Simple Shadow Mask
    float m = (fract(gl_FragCoord.x * 0.5) < 0.5) ? maskDark : maskLight;
    color *= m;
    
    color = ToSRGB(color);

    // Apply UI Global Controls
    color *= uBrightness;
    float gray = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(gray), color, uSaturation);
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}
#endif
