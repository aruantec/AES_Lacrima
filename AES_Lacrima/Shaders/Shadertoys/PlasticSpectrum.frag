precision highp float;

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float hash11(float n) {
    return fract(sin(n * 127.1) * 43758.5453);
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

vec3 waveformColor(float uvScreenX) {
    float d = abs(uvScreenX - 0.5) * 2.0;
    vec3 center = vec3(0.70, 1.10, 1.20);
    vec3 warm    = vec3(1.10, 0.52, 0.06);
    vec3 hot     = vec3(1.10, 0.12, 0.32);
    vec3 edge    = vec3(0.62, 0.04, 1.10);

    vec3 col = mix(center, warm, smoothstep(0.0, 0.26, d));
    col = mix(col, hot, smoothstep(0.20, 0.55, d));
    col = mix(col, edge, smoothstep(0.48, 1.0, d));
    return col;
}

float sampleBeat(float specU) {
    float u = clamp(specU, 0.0, 1.0);
    float h = texture(iChannel0, vec2(u, 0.5)).r;
    return pow(max(h, 0.001), 0.48);
}

float waveHeightAt(float specU, float amp) {
    return sampleBeat(specU) * amp;
}

float sharpWaveLine(float y, float waveY, float coreW, float glowK) {
    float dTop = abs(y - waveY);
    float dBot = abs(y + waveY);
    float dist = min(dTop, dBot);
    float core = smoothstep(coreW, 0.0, dist);
    float glow = exp(-dist * glowK) * 0.55;
    return core * 3.5 + glow;
}

float verticalSpike(float y, float waveY) {
    float top = step(0.0, y) * step(y, waveY) * smoothstep(waveY, waveY * 0.92, y);
    float bot = step(-waveY, y) * step(y, 0.0) * smoothstep(-waveY, -waveY * 0.92, y);
    float edge = smoothstep(0.003, 0.0, min(abs(y - waveY), abs(y + waveY)));
    return max(max(top, bot) * 0.35, edge * 1.2);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 res = iResolution.xy;
    float aspect = res.x / res.y;
    vec2 uv = (fragCoord - 0.5 * res) / res.y;
    vec2 uvScreen = fragCoord / res;
    float t = iTime;

    float bass   = texture(iChannel0, vec2(0.04, 0.5)).r;
    float mid    = texture(iChannel0, vec2(0.22, 0.5)).r;
    float treble = texture(iChannel0, vec2(0.72, 0.5)).r;
    float bassHit = pow(max(bass, 0.001), 0.45);
    float beat    = pow(bassHit, 1.3);
    float vol     = bassHit + mid * 0.7 + treble * 0.3;
    float energy  = 0.78 + beat * 1.2 + vol * 0.45;

    float specU = uvScreen.x;
    vec3 beamCol = waveformColor(uvScreen.x);
    vec3 col = vec3(0.0);

    float baseAmp = 0.30 * energy;
    float wY = waveHeightAt(specU, baseAmp);

    for (int i = 0; i < 5; i++) {
        float fI = float(i);
        float layerU = clamp(specU + (fI - 2.0) * 0.0018, 0.0, 1.0);
        float h = waveHeightAt(layerU, baseAmp);
        float n = noise(vec2(layerU * 45.0 + t * 3.0, fI * 1.3));
        h += (n * 2.0 - 1.0) * 0.018 * h;

        float line = sharpWaveLine(uv.y, h, mix(0.0028, 0.0014, fI / 4.0), mix(95.0, 140.0, fI / 4.0));
        float spike = verticalSpike(uv.y, h);
        col += beamCol * (line + spike * 0.45) * mix(0.22, 0.50, fI / 4.0) * (0.4 + vol * 0.85);
    }

    float trace = sharpWaveLine(uv.y, wY, 0.0012, 180.0);
    col += beamCol * trace * 0.65 * (0.5 + beat * 0.5);

    float axis = exp(-abs(uv.y) * 220.0) * 2.8 + exp(-abs(uv.y) * 80.0) * 0.18;
    col += beamCol * axis * (0.5 + beat * 0.4);

    vec2 pGrid = vec2(uvScreen.x * 72.0, uv.y * 42.0 + 21.0);
    vec2 pCell = floor(pGrid);

    for (int pj = -2; pj <= 2; pj++) {
        for (int pi = -2; pi <= 2; pi++) {
            vec2 c = pCell + vec2(float(pi), float(pj));
            float seed  = hash21(c);
            float seed2 = hash21(c + 41.7);
            if (seed < 0.40) continue;

            float px = clamp((c.x + seed2) / 72.0, 0.0, 1.0);
            float surfH = waveHeightAt(px, baseAmp);
            float surfHl = waveHeightAt(px - 2.0 / res.x, baseAmp);
            float surfHr = waveHeightAt(px + 2.0 / res.x, baseAmp);
            float waveForce = surfH * (1.4 + beat * 1.2) + abs(surfHr - surfHl) * 3.5;

            float dir = hash11(seed * 97.0) > 0.5 ? 1.0 : -1.0;
            float surfaceY = dir * surfH;

            float spd = 1.8 + seed2 * 2.5;
            float tau = fract(t * spd + seed * 6.283);
            float vel = waveForce * (0.55 + seed * 0.65);
            float grav = 1.8 + seed * 1.2;
            float py = surfaceY + dir * vel * tau - grav * tau * tau * 0.45;

            float pUx = (px - 0.5) * aspect;
            float pd = length(uv - vec2(pUx, py));

            float pSize = 0.002 + seed * 0.002;
            float particle = smoothstep(pSize, 0.0, pd);
            float pGlow = exp(-pd * mix(120.0, 200.0, seed)) * 0.7;

            col += waveformColor(px) * (particle * 2.2 + pGlow) * waveForce * (0.35 + vol * 0.5);
        }
    }

    float dust = noise(vec2(specU * 120.0 + t * 5.0, uv.y * 60.0));
    float nearPeak = exp(-min(abs(uv.y - wY), abs(uv.y + wY)) * 35.0);
    col += beamCol * step(0.82, dust) * nearPeak * wY * 2.5 * (0.3 + beat);

    col *= 1.0 - 0.20 * length(uvScreen - 0.5);

    fragColor = vec4(col * u_fade, 1.0);
}
