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

#define FXAA_REDUCE_MIN   (1.0/128.0)
#define FXAA_REDUCE_MUL   (1.0/8.0)
#define FXAA_SPAN_MAX     8.0

void main() {
    vec2 res = 1.0 / TextureSize;
    vec3 rgbNW = texture(Texture, vTex + (vec2(-1.0, -1.0) * res)).xyz;
    vec3 rgbNE = texture(Texture, vTex + (vec2(1.0, -1.0) * res)).xyz;
    vec3 rgbSW = texture(Texture, vTex + (vec2(-1.0, 1.0) * res)).xyz;
    vec3 rgbSE = texture(Texture, vTex + (vec2(1.0, 1.0) * res)).xyz;
    vec3 rgbM  = texture(Texture, vTex).xyz;

    vec3 luma = vec3(0.299, 0.587, 0.114);
    float lumaNW = dot(rgbNW, luma);
    float lumaNE = dot(rgbNE, luma);
    float lumaSW = dot(rgbSW, luma);
    float lumaSE = dot(rgbSE, luma);
    float lumaM  = dot(rgbM,  luma);

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * FXAA_REDUCE_MUL), FXAA_REDUCE_MIN);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);

    dir = min(vec2(FXAA_SPAN_MAX, FXAA_SPAN_MAX), 
          max(vec2(-FXAA_SPAN_MAX, -FXAA_SPAN_MAX), 
          dir * rcpDirMin)) * res;

    vec3 rgbA = 0.5 * (
        texture(Texture, vTex + dir * (1.0/3.0 - 0.5)).xyz +
        texture(Texture, vTex + dir * (2.0/3.0 - 0.5)).xyz);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(Texture, vTex + dir * -0.5).xyz +
        texture(Texture, vTex + dir * 0.5).xyz);

    float lumaB = dot(rgbB, luma);
    vec3 color = ((lumaB < lumaMin) || (lumaB > lumaMax)) ? rgbA : rgbB;

    // Apply UI Global Controls
    color *= uBrightness;
    float gray = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(gray), color, uSaturation);
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}
#endif
