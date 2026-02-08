float sdRoundRect(vec2 p, vec2 b, float r) {
    vec2 d = abs(p) - b + r;
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}

mat2 rotate2d(float angle) {
    return mat2(cos(angle), -sin(angle), sin(angle), cos(angle));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = (fragCoord * 2.0 - iResolution.xy) / iResolution.y;
    
    // Start with fully transparent
    vec4 res = vec4(0.0, 0.0, 0.0, 0.0);

    // 1. MAIN BODY (Black shell)
    float body = sdRoundRect(uv, vec2(0.85, 0.55), 0.05);
    if (body < 0.0) {
        res = vec4(0.08, 0.08, 0.08, 1.0);
    }

    // 2. LABEL AREA (Beige)
    float label = sdRoundRect(uv - vec2(0.0, 0.1), vec2(0.75, 0.35), 0.02);
    if (body < 0.0 && label < 0.0) {
        res.rgb = vec3(0.85, 0.82, 0.75);
    }

    // 3. ORANGE STRIPE
    if (body < 0.0 && label < 0.0 && uv.y < 0.15 && uv.y > -0.15) {
        res.rgb = vec3(0.85, 0.35, 0.15);
    }

    // 4. THE CENTER WINDOW (Black cutout)
    vec2 windowPos = uv - vec2(0.0, -0.01);
    float window = sdRoundRect(windowPos, vec2(0.45, 0.15), 0.1);
    if (body < 0.0 && window < 0.0) {
        res.rgb = vec3(0.05, 0.05, 0.05);
    }

    // 5. SPINNING REELS (White Teeth + Clipping)
    float reelDist = 0.28;
    float rot = iTime * 2.0;
    
    if (body < 0.0) {
        for(int i=0; i<2; i++) {
            float side = (i == 0) ? -1.0 : 1.0;
            vec2 rUv = uv - vec2(reelDist * side, -0.01);
            float d = length(rUv);
            
            // Outer white ring
            if (d < 0.12) {
                res.rgb = vec3(0.9);
                res.a = 1.0;
            }
            
            // THE CLIP: Force alpha to 0.0 inside the ring
            if (d < 0.10) {
                res = vec4(0.0, 0.0, 0.0, 0.0); 
            }
            
            // Draw Teeth back in (NOW WHITE)
            vec2 rotUv = rUv * rotate2d(rot);
            float angle = atan(rotUv.y, rotUv.x);
            float teeth = cos(angle * 6.0); 
            if (d < 0.10 && d > 0.07 && teeth > 0.0) {
                res = vec4(0.95, 0.95, 0.95, 1.0); // Opaque white teeth
            }
            
            // Final center hole clip
            if (d < 0.04) {
                res = vec4(0.0, 0.0, 0.0, 0.0); 
            }
        }
    }

    // 6. BOTTOM CUTOUT
    float bottom = sdRoundRect(uv - vec2(0.0, -0.5), vec2(0.4, 0.15), 0.02);
    if (body < 0.0 && bottom < 0.0 && uv.y < -0.38) {
        res.rgb = vec3(0.15);
        res.a = 1.0;
    }

    fragColor = res;
}