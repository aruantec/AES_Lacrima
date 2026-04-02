#version 150

#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;

out vec2 vTex;
uniform mat4 MVPMatrix;

void main()
{
    vTex = TexCoord;
    gl_Position = MVPMatrix * vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;

// UI Controls
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

// Contrast Adaptive Sharpening (CAS)
// Simplified implementation for single-pass GLSL

void main()
{
    vec2 texel = 1.0 / TextureSize;
    
    // Fetch neighbors
    vec3 a = texture(Texture, vTex + vec2(-texel.x, -texel.y)).rgb;
    vec3 b = texture(Texture, vTex + vec2(0.0, -texel.y)).rgb;
    vec3 c = texture(Texture, vTex + vec2(texel.x, -texel.y)).rgb;
    vec3 d = texture(Texture, vTex + vec2(-texel.x, 0.0)).rgb;
    vec3 e = texture(Texture, vTex).rgb;
    vec3 f = texture(Texture, vTex + vec2(texel.x, 0.0)).rgb;
    vec3 g = texture(Texture, vTex + vec2(-texel.x, texel.y)).rgb;
    vec3 h = texture(Texture, vTex + vec2(0.0, texel.y)).rgb;
    vec3 i = texture(Texture, vTex + vec2(texel.x, texel.y)).rgb;
    
    // Soften neighbors
    vec3 min_rgb = min(min(min(d, e), f), min(b, h));
    vec3 max_rgb = max(max(max(d, e), f), max(b, h));
    
    // Contrast base
    vec3 contrast = max_rgb - min_rgb;
    vec3 weight = contrast * (1.0 / (max_rgb + 0.001));
    weight = clamp(weight, 0.0, 1.0);
    
    // Sharpening strength
    float sharp = -0.125; // range roughly -0.1 to -0.2
    
    vec3 color = e + ( (b + d + f + h) - 4.0 * e) * (weight * sharp);
    color = clamp(color, 0.0, 1.0);

    // Apply UI Global Controls
    color *= uBrightness;
    
    // Saturation
    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(luma), color, uSaturation);
    
    // Tint
    color *= uColorTint.rgb;

    fragColor = vec4(color, uColorTint.a);
}

#endif
