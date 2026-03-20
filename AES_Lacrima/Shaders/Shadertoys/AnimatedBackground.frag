precision highp float;

float hash21(vec2 p) {
    p = fract(p * vec2(213.897, 653.453));
    p += dot(p, p + 37.451);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);

    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));

    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.55;
    mat2 m = mat2(0.80, -0.60, 0.60, 0.80);

    for (int i = 0; i < 6; i++) {
        v += a * noise(p);
        p = m * p * 2.04 + vec2(9.0, 17.0);
        a *= 0.5;
    }
    return v;
}

float softBlob(vec2 p, vec2 c, float r, float k) {
    float d = length(p - c) / r;
    return exp(-pow(d, k));
}

vec3 sat(vec3 c, float s) {
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    return mix(vec3(l), c, s);
}

vec3 hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

vec3 randomAccent(float seed) {
    float h = fract(0.137 + 0.6180339 * hash21(vec2(seed, seed * 1.73)));
    float s = 0.55 + 0.18 * hash21(vec2(seed + 2.1, seed * 2.7));
    float v = 0.30 + 0.14 * hash21(vec2(seed + 4.8, seed * 3.1));
    return hsv2rgb(vec3(h, s, v));
}

vec3 hueShift(vec3 color, float amount) {
    const mat3 toYiq = mat3(
        0.299, 0.587, 0.114,
        0.596, -0.274, -0.322,
        0.211, -0.523, 0.312
    );
    const mat3 toRgb = mat3(
        1.0, 0.956, 0.621,
        1.0, -0.272, -0.647,
        1.0, -1.106, 1.703
    );

    vec3 yiq = toYiq * color;
    float hue = atan(yiq.z, yiq.y) + amount;
    float chroma = length(yiq.yz);
    yiq.y = chroma * cos(hue);
    yiq.z = chroma * sin(hue);
    return clamp(toRgb * yiq, 0.0, 1.0);
}

float bandMask(float y, float center, float width, float blur) {
    float d = abs(y - center) - width;
    return 1.0 - smoothstep(-blur, blur, d);
}

vec2 coverUvUniformToFill(vec2 uv, vec2 viewSize, vec2 texSize) {
    if (texSize.x <= 1.0 || texSize.y <= 1.0) return uv;

    float viewAspect = max(viewSize.x / max(viewSize.y, 1.0), 0.0001);
    float texAspect = max(texSize.x / max(texSize.y, 1.0), 0.0001);
    vec2 outUv = uv;

    if (texAspect > viewAspect) {
        float scaleX = viewAspect / texAspect;
        outUv.x = (uv.x - 0.5) * scaleX + 0.5;
    } else {
        float scaleY = texAspect / viewAspect;
        outUv.y = (uv.y - 0.5) * scaleY + 0.5;
    }

    return outUv;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 res = max(iResolution.xy, vec2(1.0));
    vec2 uv = fragCoord / res;
    vec2 p = uv - 0.5;
    p.x *= res.x / res.y;

    float t = iTime * 0.40;

    vec2 warp = vec2(
        fbm(p * 1.9 + vec2(t * 0.85, -t * 0.40)),
        fbm(p * 1.9 + vec2(-t * 0.50, t * 0.70))
    ) - 0.5;
    vec2 q = p + warp * 0.18;

    // Extra motion energy in the lower half so movement is visible near the bottom controls.
    float bottomMotion = smoothstep(-0.15, -0.95, q.y);
    q += vec2(
        sin((q.y + 0.8) * 11.0 + t * 2.9),
        cos((q.x - 0.1) * 10.0 - t * 2.5)
    ) * (0.040 * bottomMotion);

    float wave1 = 0.34 + 0.05 * sin(q.x * 3.2 + t * 0.9) + 0.03 * fbm(q * 3.4 + 1.3);
    float wave2 = 0.10 + 0.06 * sin(q.x * 2.8 - t * 0.7) + 0.03 * fbm(q * 3.0 + 7.1);
    float wave3 = -0.13 + 0.07 * sin(q.x * 2.2 + t * 0.5) + 0.025 * fbm(q * 2.5 + 11.7);
    float wave4 = -0.36 + 0.05 * sin(q.x * 1.9 - t * 0.45) + 0.02 * fbm(q * 2.1 + 17.9);

    vec2 cp0 = coverUvUniformToFill(vec2(0.22, 0.30), res, iChannel1Size);
    vec2 cp1 = coverUvUniformToFill(vec2(0.78, 0.34), res, iChannel1Size);
    vec2 cp2 = coverUvUniformToFill(vec2(0.45, 0.70), res, iChannel1Size);
    vec3 coverAvg = (
        texture(iChannel1, clamp(cp0, 0.0, 1.0)).rgb +
        texture(iChannel1, clamp(cp1, 0.0, 1.0)).rgb +
        texture(iChannel1, clamp(cp2, 0.0, 1.0)).rgb
    ) / 3.0;

    // Strongly anchor palette to cover colors.
    vec3 coverPaletteA = sat(mix(u_primary, coverAvg, 0.30), 1.10);
    vec3 coverPaletteB = sat(mix(u_secondary, coverAvg.bgr, 0.26), 1.06);
    vec3 warmBand = sat(mix(coverPaletteA, hueShift(coverPaletteA, 0.35), 0.48), 1.08);
    vec3 coolBand = sat(mix(coverPaletteB, hueShift(coverPaletteB, -0.35), 0.50), 1.04);
    vec3 lightBand = sat(mix(max(coverPaletteA, coverPaletteB), vec3(0.95), 0.48), 1.02);
    vec3 deepBand = sat(mix(min(coverPaletteA, coverPaletteB) * 0.42, vec3(0.015, 0.02, 0.035), 0.58), 0.95);

    float stepIdx = floor(iTime / 20.0);
    float stepLerp = smoothstep(0.0, 1.0, clamp((fract(iTime / 20.0) - 0.82) / 0.18, 0.0, 1.0));
    vec3 accentA = randomAccent(stepIdx + 31.0);
    vec3 accentB = randomAccent(stepIdx + 32.0);
    vec3 dynamicAccent = sat(mix(mix(accentA, accentB, stepLerp), mix(warmBand, coolBand, 0.5), 0.72), 1.02);

    vec3 color = mix(deepBand, coolBand, smoothstep(0.05, 0.85, uv.x));
    color = mix(color, lightBand,       bandMask(q.y, wave1, 0.26, 0.12) * 0.84);
    color = mix(color, warmBand,        bandMask(q.y, wave2, 0.21, 0.11) * 0.96);
    color = mix(color, coverPaletteA,   bandMask(q.y, wave3, 0.30, 0.13) * 0.96);
    color = mix(color, dynamicAccent,   bandMask(q.y, wave4, 0.34, 0.16) * 0.72);
    color = mix(color, deepBand, smoothstep(-0.42, -0.96, q.y) * 0.96);

    float floorDrift = fbm(vec2(q.x * 3.8 + t * 1.7, q.y * 5.2 - t * 1.5));
    vec3 floorTone = mix(warmBand, coolBand, floorDrift);
    color = mix(color, floorTone, bottomMotion * 0.34);

    // Smooth drifting smoke from layered FBM, similar to the first version.
    float smoke1 = fbm(q * 3.2 + vec2(t * 0.85, -t * 0.55));
    float smoke2 = fbm(q * 4.9 + vec2(-t * 0.72, t * 0.78));
    float smoke = smoothstep(0.34, 0.86, mix(smoke1, smoke2, 0.5));
    vec3 smokeTint = vec3(0.10, 0.06, 0.08);
    color = mix(color, color + smokeTint, smoke * 0.46);

    float highlight = smoothstep(0.38, 0.90, fbm(q * 4.0 + vec2(-t * 0.7, t * 0.4)));
    color += vec3(0.045, 0.03, 0.022) * highlight;

    float horizonGlow = smoothstep(0.18, -0.26, abs(q.y + 0.02));
    color += vec3(0.05, 0.03, 0.022) * horizonGlow * 0.36;

    float grain = noise(fragCoord * 0.85 + vec2(t * 20.0, -t * 14.0)) - 0.5;
    color += grain * 0.006;

    // Cover art blend sampled as UniformToFill.
    // Keep it recognizable but prevent it from flattening the procedural animation.
    vec2 coverUv = coverUvUniformToFill(uv, res, iChannel1Size);
    vec2 coverUvClamped = clamp(coverUv, 0.0, 1.0);
    vec3 coverTex = texture(iChannel1, coverUvClamped).rgb;
    vec3 coverTinted = sat(coverTex * 0.80, 0.94);
    float coverInside = step(0.0, coverUv.x) * step(0.0, coverUv.y) * step(coverUv.x, 1.0) * step(coverUv.y, 1.0);
    float coverLuma = dot(coverTex, vec3(0.2126, 0.7152, 0.0722));
    float detailBoost = smoothstep(0.20, 0.85, abs(coverLuma - 0.5));
    float motionMask = 0.5 + 0.5 * sin((q.x + q.y) * 10.0 + t * 4.2);
    float coverBlend = (0.088 + detailBoost * 0.03) * coverInside * (0.72 + 0.28 * motionMask);
    color = mix(color, mix(color, coverTinted, 0.56), coverBlend);

    // Add moving structure on top so motion remains obvious even with strong covers.
    float streamA = fbm(q * 6.0 + vec2(t * 1.8, -t * 1.4));
    float streamB = fbm(q.yx * 7.2 + vec2(-t * 1.5, t * 1.9));
    float stream = smoothstep(0.42, 0.90, mix(streamA, streamB, 0.5));
    vec3 streamTint = mix(coolBand, warmBand, 0.5);
    color += streamTint * (0.12 * stream);

    float vignette = smoothstep(1.35, 0.20, length(p));
    color *= mix(0.70, 0.96, vignette);

    color = sat(color, 1.07);
    color = pow(max(color, 0.0), vec3(1.10));
    color = clamp(color, 0.0, 1.0);

    fragColor = vec4(color * u_fade, 1.0);
}
