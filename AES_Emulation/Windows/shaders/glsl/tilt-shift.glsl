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
    float blur = 0.0;
    float dist = abs(vTex.y - 0.5);
    
    // Tilt shift parameters
    float offset = 0.2;
    float spread = 0.3;
    
    if (dist > offset) blur = (dist - offset) / spread;
    blur = clamp(blur, 0.0, 1.0);
    
    vec4 color = vec4(0.0);
    float total = 0.0;
    float radius = blur * 5.0;
    
    // Simple box blur
    for (float x = -2.0; x <= 2.0; x++) {
        for (float y = -2.0; y <= 2.0; y++) {
            color += texture(Texture, vTex + vec2(x, y) * radius / TextureSize);
            total += 1.0;
        }
    }
    color /= total;

    // Apply UI Global Controls
    color.rgb *= uBrightness;
    float gray = dot(color.rgb, vec3(0.299, 0.587, 0.114));
    color.rgb = mix(vec3(gray), color.rgb, uSaturation);
    color.rgb *= uColorTint.rgb;

    fragColor = vec4(color.rgb, uColorTint.a);
}
#endif
