vec3 getDynamicColor(vec3 base, float t) {
    // Generate random seed from u_primary properties
    float seed = fract(sin(dot(base.rgb, vec3(12.9898, 78.233, 45.164))) * 43758.5453);
    
    // 20-second rotation (0.05 speed) with randomized start phase
    float theta = (t * 0.05) + (seed * 6.2831); 
    
    vec3 axis = vec3(0.57735);
    float cosTheta = cos(theta);
    return base * cosTheta + cross(axis, base) * sin(theta) + axis * dot(axis, base) * (1.0 - cosTheta);
}

vec3 computeSilk(vec2 uv, float time, float freq, float amp, float height, vec3 color) {
    // Wave calculation using domain-shifted coordinates
    float wavePos = sin(uv.x * freq + time) * amp + height;
    float dist = uv.y - wavePos;
    
    float silkMask = 1.0 - smoothstep(-0.002, 0.0, dist);
    
    float edgeRim = 0.015 / (abs(dist) + 0.018);
    edgeRim = pow(edgeRim, 1.9); // Tightened rim for a cleaner profile
    
    float lightLeak = exp(-abs(dist) * 12.0);
    float verticalFade = smoothstep(-0.55, 0.0, dist);
    vec3 layerColor = (color * verticalFade) + (color * lightLeak * 0.22) + (vec3(1.0) * edgeRim * 0.38);
    
    return layerColor * silkMask;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec3 dynamicHue = getDynamicColor(u_primary, iTime);
    float breathing = (sin(iTime * 0.3141) * 0.12) + 0.42; 
    vec3 masterColor = dynamicHue * breathing;

    vec2 glowCenter = vec2(0.5, 0.45);
    float distToGlow = length((uv - glowCenter) * vec2(1.0, 0.8));
    float ambientGlow = pow(max(0.0, 1.0 - distToGlow), 1.6);
    
    vec3 background = (masterColor * (1.1 - uv.y)) + (masterColor * ambientGlow * 0.75);
    vec3 finalOutput = background;
    
    // Back Layer
    finalOutput += computeSilk(uv, iTime * 0.42, 1.15, 0.14, 0.36, masterColor * 0.4);
    
    // Middle Layer
    finalOutput += computeSilk(uv, iTime * -0.28, 1.45, 0.18, 0.49, masterColor * 0.7);
    
    // Front Layer
    finalOutput += computeSilk(uv, iTime * 0.16, 1.85, 0.24, 0.63, masterColor * 1.0);

    fragColor = vec4(finalOutput * u_fade, 1.0);
}