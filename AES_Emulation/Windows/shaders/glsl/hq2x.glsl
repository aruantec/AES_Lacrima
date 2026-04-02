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
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

void main() {
    vec2 dg1 = vec2( 0.5, 0.5) / TextureSize;
    vec2 dg2 = vec2(-0.5, 0.5) / TextureSize;
    
    vec2 vTexNW = vTex - dg1;
    vec2 vTexNE = vTex - dg2;
    vec2 vTexSW = vTex + dg2;
    vec2 vTexSE = vTex + dg1;

    vec4 c  = texture(Texture, vTex);
    vec4 cNW = texture(Texture, vTexNW);
    vec4 cNE = texture(Texture, vTexNE);
    vec4 cSW = texture(Texture, vTexSW);
    vec4 cSE = texture(Texture, vTexSE);

    float mdthreshold = 0.15;
    float w1 = dot(abs(cNW-cSE), vec4(1.0));
    float w2 = dot(abs(cNE-cSW), vec4(1.0));
    
    vec4 res = c;
    if (w1 < w2 && w1 < mdthreshold) res = mix(c, mix(cNW, cSE, 0.5), 0.5);
    else if (w2 < w1 && w2 < mdthreshold) res = mix(c, mix(cNE, cSW, 0.5), 0.5);

    // Apply UI Global Controls
    res.rgb *= uBrightness;
    float gray = dot(res.rgb, vec3(0.299, 0.587, 0.114));
    res.rgb = mix(vec3(gray), res.rgb, uSaturation);
    res.rgb *= uColorTint.rgb;

    fragColor = vec4(res.rgb, uColorTint.a);
}
#endif
