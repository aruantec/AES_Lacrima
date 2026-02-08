vec3 getDynamicColor(vec3 base, float t) {
    float seed = fract(sin(dot(base.rgb, vec3(12.9898, 78.233, 45.164))) * 43758.5453);
    
    // 20-second cycle (0.05) with the random seed offset
    float angle = (t * 0.05) + (seed * 6.283); 
    
    vec3 s = vec3(0.57735);
    vec3 cosVec = vec3(cos(angle));
    vec3 sinVec = vec3(sin(angle));
    return base * cosVec + cross(s, base) * sinVec + s * dot(s, base) * (1.0 - cosVec);
}

vec3 computeSilk(vec2 st, float t, float f, float a, float h, vec3 col) {
    float x_warp = st.x * f + t;
    float y_target = sin(x_warp) * a + h;
    float sdf = st.y - y_target;
    
    float sheet = smoothstep(0.0, -0.005, sdf);
    float rim = 0.022 / (abs(sdf) + 0.012);
    rim = pow(rim, 1.6);
    float innerLight = exp(-abs(sdf) * 8.0);
    float depth = smoothstep(-0.6, 0.0, sdf);
    
    return ((col * depth) + (col * innerLight * 0.4) + (vec3(1.0) * rim * 0.6)) * sheet;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;

    // Dynamic Color and Pulse
    vec3 shiftingColor = getDynamicColor(u_primary, iTime);
    float pulse = (sin(iTime * 0.314) * 0.1) + 0.4; 
    vec3 masterColor = shiftingColor * pulse;

    // Stronger Background Glow
    // Vertical gradient logic inspired by PS3_MenuColor.frag
    vec3 bgBase = masterColor * (1.1 - uv.y);
    
    // Ambient Center Glow
    float centerGlow = 1.0 - distance(uv, vec2(0.5, 0.45));
    centerGlow = pow(max(0.0, centerGlow), 1.5);
    vec3 background = bgBase + (masterColor * centerGlow * 0.8);

    // Three Glowing Silk Layers
    vec3 scene = background;
    scene += computeSilk(uv, iTime * 0.4, 1.1, 0.15, 0.35, masterColor * 0.4);
    scene += computeSilk(uv, -iTime * 0.25, 1.4, 0.20, 0.48, masterColor * 0.7);
    scene += computeSilk(uv, iTime * 0.15, 1.8, 0.25, 0.62, masterColor * 1.0);

    fragColor = vec4(scene * u_fade, 1.0);
}