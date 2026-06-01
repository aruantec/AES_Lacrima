vec3 sat(vec3 c) {
    return clamp(c, 0.0, 1.0);
}

vec3 tamePalette(vec3 c) {
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    return clamp(mix(vec3(luma), c, 0.88) * 0.58 + vec3(0.028), 0.0, 1.0);
}

vec3 compressHighlights(vec3 c) {
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    float knee = smoothstep(0.34, 0.66, luma);
    float target = mix(luma, 0.52, knee);
    return sat(c * (target / max(luma, 0.001)));
}

vec3 pulseColor(vec3 c, float drift) {
    return tamePalette(c * (0.81 + 0.10 * drift));
}

// dist = uv.y - wavePos; only evaluated when dist is inside the ribbon band.
vec3 silkFromDist(float dist, vec3 color) {
    float verticalFade = smoothstep(-0.55, 0.0, dist);
    float adist = abs(dist);

    float edgeRim = 0.015 / (adist + 0.022);
    edgeRim *= edgeRim;

    float lightLeak = exp(-adist * 14.0);
    float silkMask = 1.0 - smoothstep(-0.002, 0.0, dist);

    vec3 layerColor = color * verticalFade;
    layerColor += color * lightLeak * 0.14;
    layerColor += mix(color, vec3(1.0), 0.18) * edgeRim * 0.10;

    return layerColor * silkMask;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord.xy / iResolution.xy;
    float t = iTime;
    float y = uv.y;

    float driftBase = 0.5 + 0.5 * sin(t * 0.11);
    vec3 colorPrimary = pulseColor(u_primary, driftBase);
    vec3 colorSecondary = pulseColor(u_secondary, 0.5 + 0.5 * sin(t * 0.11 + 2.1));
    vec3 colorTertiary = pulseColor(u_tertiary, 0.5 + 0.5 * sin(t * 0.11 + 4.2));

    float blendMid = smoothstep(0.0, 0.55, y);
    float blendTop = smoothstep(0.45, 1.0, y);
    vec3 paletteBg = mix(mix(colorPrimary, colorSecondary, blendMid), colorTertiary, blendTop);

    vec2 glowDelta = (uv - vec2(0.5, 0.45)) * vec2(1.0, 0.8);
    float g = max(0.0, 1.0 - length(glowDelta));
    float ambientGlow = g * g * sqrt(g);

    vec3 glowTint = mix(colorPrimary, colorTertiary, 0.5 + 0.5 * sin(t * 0.15));
    vec3 finalOutput = paletteBg * (0.74 - y * 0.25) + glowTint * ambientGlow * 0.26;

    // Wave positions (three sins, once per pixel).
    float d0 = y - (sin(uv.x * 1.15 + t * 0.42) * 0.14 + 0.36);
    float d1 = y - (sin(uv.x * 1.45 - t * 0.28) * 0.18 + 0.49);
    float d2 = y - (sin(uv.x * 1.85 + t * 0.16) * 0.24 + 0.63);

    const float silkMin = -0.56;
    if (d0 <= 0.0 && d0 > silkMin) finalOutput += silkFromDist(d0, colorPrimary * 0.32);
    if (d1 <= 0.0 && d1 > silkMin) finalOutput += silkFromDist(d1, colorSecondary * 0.45);
    if (d2 <= 0.0 && d2 > silkMin) finalOutput += silkFromDist(d2, colorTertiary * 0.54);

    vec2 uiDelta = (uv - vec2(0.5, 0.52)) * vec2(1.05, 0.92);
    float uiZone = exp(-dot(uiDelta, uiDelta) * 3.2);
    finalOutput *= mix(1.0, 0.82, uiZone);

    finalOutput = compressHighlights(finalOutput);

    fragColor = vec4(sat(finalOutput) * u_fade, 1.0);
}
