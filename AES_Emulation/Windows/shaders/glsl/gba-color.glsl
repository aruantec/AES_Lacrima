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
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;
in vec2 vTex;
out vec4 fragColor;

void main() {
    vec4 color = texture(Texture, vTex);
    
    // GBA Color Correction (simulating the non-backlit screen)
    // Often involve boosting gamma and shifting colors
    vec3 res = color.rgb;
    res = pow(res, vec3(1.5)); // Darken and increase contrast
    res = mat3(0.84, 0.16, 0.0,
               0.08, 0.77, 0.15,
               0.15, 0.0,  0.81) * res;
    res = pow(res, vec3(1.0/1.2)); // Slight gamma adjustment back

    // Apply UI Global Controls
    res *= uBrightness;
    float gray = dot(res, vec3(0.299, 0.587, 0.114));
    res = mix(vec3(gray), res, uSaturation);
    res *= uColorTint.rgb;

    fragColor = vec4(res, uColorTint.a);
}
#endif
