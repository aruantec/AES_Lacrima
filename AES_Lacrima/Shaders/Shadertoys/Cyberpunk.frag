precision highp float;

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbmLow(vec2 p) {
    float f = 0.0;
    f += 0.5000 * noise(p); p = p * 2.02;
    f += 0.2500 * noise(p);
    return f / 0.75;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 res = iResolution.xy + 0.001;
    vec2 uvScreen = fragCoord / res;
    vec2 p = (fragCoord - 0.5 * res) / res.y;
    float t = iTime;

    float bass = texture(iChannel0, vec2(0.04, 0.25)).r;
    float mid  = texture(iChannel0, vec2(0.18, 0.25)).r;
    float treb = texture(iChannel0, vec2(0.78, 0.25)).r;
    float bassL = pow(bass, 0.6);
    float vol = bassL + mid * 0.7 + treb * 0.3;

    // Scrolling perspective grid (lower half)
    float perspective = 1.0 / (abs(p.y + 0.8) + 0.01);
    vec2 gridUv = vec2(p.x * perspective, perspective + iTime * 2.0);
    vec2 grid = abs(fract(gridUv - 0.5) - 0.5) / (fwidth(gridUv) + 0.01);
    float lines = 1.0 - min(grid.x, grid.y);

    vec3 neonMagenta = vec3(0.55, 0.0, 1.0);
    vec3 neonCyan    = vec3(0.0, 1.0, 1.0);
    vec3 neonRed     = vec3(1.0, 0.0, 0.04);
    vec3 neonWhite   = vec3(1.0, 0.95, 0.95);

    vec3 color = vec3(0.0);
    if (p.y < 0.0) {
        float fade = smoothstep(0.0, -0.8, p.y);
        color = mix(neonMagenta, neonCyan, uvScreen.x) * lines * fade;
    }

    // Synthwave palette: magenta (back) -> cyan (front), red peaks, white core
    vec3 waveCol = vec3(0.0);
    {
        const int numLayers = 6;
        for (int i = 0; i < numLayers; i++) {
            float fI = float(i);
            float depthT = fI / 5.0;

            float parallaxScale = mix(1.52, 1.06, depthT);
            float layerDepth = mix(1.42, 1.0, depthT);
            float scrollSpeed = mix(0.011, 0.048, depthT);
            float layerTime = t * mix(0.42, 0.78, depthT) - fI * 0.035;

            float pX = p.x * parallaxScale + (fI - 2.5) * 0.042 * sin(t * 0.20 + fI * 0.62);
            float pUvScreenX = pX * (res.y / res.x) + 0.5;

            float scrollX = pUvScreenX - layerTime * scrollSpeed;
            float bin = fract(scrollX);
            float prevBin = texture(iChannel0, vec2(fract(bin - 1.0 / 160.0), 0.5)).r;
            float nextBin = texture(iChannel0, vec2(fract(bin + 1.0 / 160.0), 0.5)).r;
            float binHeight = (texture(iChannel0, vec2(bin, 0.5)).r + prevBin + nextBin) / 3.0;
            binHeight = pow(binHeight, 0.78);

            // Lift waves into the sky band above the horizon (p.y = 0)
            float baseY = mix(0.12, 0.42, depthT);
            float waveOffset = (binHeight - 0.42) * mix(0.14, 0.24, depthT);
            float jitter = (fbmLow(vec2(pX * mix(4.0, 7.5, depthT) + layerTime * 1.3, layerTime)) * 2.0 - 1.0);
            jitter *= mix(0.045, 0.075, depthT) * (0.18 + vol * 0.85);

            float currentY = baseY + waveOffset + jitter / layerDepth;
            float dist = abs(p.y - currentY);

            float colorMix = smoothstep(0.05, 0.85, pUvScreenX);
            vec3 layerCol = mix(neonMagenta, neonCyan, colorMix);
            layerCol = mix(layerCol, neonRed, binHeight * 0.40 * (0.6 + bassL * 0.5));

            float spark = noise(vec2(pX * mix(10.0, 16.0, depthT) + layerTime * 2.6, waveOffset * 4.0));
            float sparkIntensity = mix(0.30, 0.50, depthT) + 1.05 * spark;

            float coreWidth = mix(0.0055, 0.0032, depthT) + 0.003 * pow(bassL, 1.4);
            float core = smoothstep(coreWidth, 0.0, dist);
            float innerGlow = exp(-dist * mix(38.0, 58.0, depthT)) * (0.40 + 0.42 * pow(bassL, 1.2));
            float outerGlow = exp(-dist * mix(14.0, 20.0, depthT)) * mix(0.08, 0.14, depthT);
            float layerAlpha = mix(0.42, 0.92, depthT);

            vec3 glow = layerCol * (innerGlow + outerGlow) * sparkIntensity * layerAlpha;
            vec3 coreCol = mix(neonWhite, neonRed, 0.18 + bassL * 0.22);
            waveCol += (glow + coreCol * core * mix(2.6, 3.8, depthT)) * (0.32 + vol * 1.20);
        }
    }

    // Soft fade into the grid — no hard clip through the wave body
    float skyFade = smoothstep(-0.14, 0.04, p.y);
    color += waveCol * skyFade;

    // Horizon bloom where grid meets sky
    float horizonGlow = exp(-abs(p.y) * 22.0) * (0.06 + bassL * 0.10);
    color += mix(neonMagenta, neonCyan, uvScreen.x) * horizonGlow;

    fragColor = vec4(color * u_fade, 1.0);
}
