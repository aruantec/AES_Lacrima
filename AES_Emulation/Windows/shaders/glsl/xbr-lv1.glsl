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

// Simplified xBR-like smoothing for single pass
// Based on Hyllian's work but simplified for this pipeline

void main()
{
    vec2 pos = vTex * TextureSize;
    vec2 fp = fract(pos);
    vec2 tc = (floor(pos) + 0.5) / TextureSize;
    
    vec4 c = texture(Texture, tc);
    
    // Sample neighbors
    vec2 otr = 1.0 / TextureSize;
    vec4 n = texture(Texture, tc + vec2(0, -otr.y));
    vec4 s = texture(Texture, tc + vec2(0, otr.y));
    vec4 w = texture(Texture, tc + vec2(-otr.x, 0));
    vec4 e = texture(Texture, tc + vec2(otr.x, 0));
    
    // Very basic weight-based smoothing
    vec4 color = c;
    if (length(c - n) > 0.1 || length(c - s) > 0.1 || length(c - w) > 0.1 || length(c - e) > 0.1) {
        // Simple blend based on fractional position
        vec4 h = mix(w, e, fp.x);
        vec4 v = mix(n, s, fp.y);
        color = mix(c, mix(h, v, 0.5), 0.3);
    }

    // Apply UI Global Controls
    color.rgb *= uBrightness;
    
    // Saturation
    float luma = dot(color.rgb, vec3(0.299, 0.587, 0.114));
    color.rgb = mix(vec3(luma), color.rgb, uSaturation);
    
    // Tint
    color.rgb *= uColorTint.rgb;

    fragColor = vec4(color.rgb, uColorTint.a);
}

#endif
