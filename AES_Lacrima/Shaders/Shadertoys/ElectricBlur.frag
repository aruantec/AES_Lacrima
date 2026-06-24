precision highp float;

const float kScroll = 1.0;
const float kMirrorSpread = 0.58; // upper/lower ribbon gap (lower = closer)

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float hash11(float n) {
    return fract(sin(n * 43758.5453 + n) * 43758.5453);
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

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * noise(p);
        p = p * 2.03 + 17.1;
        a *= 0.5;
    }
    return v;
}

vec3 coverAccent(float uvScreenX, float barHeight) {
    float colorMix = smoothstep(0.05, 0.85, uvScreenX);
    vec3 base = mix(u_primary.rgb, u_secondary.rgb, colorMix);
    return mix(base, u_tertiary.rgb, clamp(barHeight, 0.0, 1.0) * 0.40);
}

float scrolledSpecU(float specU, float layerTime, float scrollSpeed) {
    return specU - layerTime * scrollSpeed * kScroll;
}

float specTap(float u) {
    return texture(iChannel0, vec2(fract(u), 0.5)).r;
}

float specKernel(float u, float tap) {
    return (specTap(u - tap) + specTap(u) + specTap(u + tap)) / 3.0;
}

// Wrap-aware spectrum sample: keeps the normal look but bridges the scroll seam.
float sampleSpec(float specU) {
    float u = fract(specU);
    float tap = 1.0 / 180.0;
    float center = specKernel(u, tap);

    float seamW = 0.11;
    float wLo = 1.0 - smoothstep(0.0, seamW, u);
    float wHi = smoothstep(1.0 - seamW, 1.0, u);

    float fromHigh = specKernel(u + 1.0 - seamW, tap);
    float fromLow = specKernel(u - 1.0 + seamW, tap);
    float lo = mix(center, fromHigh, wLo);
    float blended = mix(lo, fromLow, wHi);

    float edge = min(u, 1.0 - u);
    float seamFade = smoothstep(0.0, seamW, edge);
    float wide = 0.0;
    for (int i = -3; i <= 3; i++) {
        wide += specTap(u + float(i) * tap);
    }
    wide /= 7.0;
    blended = mix(wide, blended, seamFade);

    return pow(max(blended, 0.001), 0.52);
}

float waveAt(float specU, float amp) {
    return sampleSpec(specU) * amp;
}

float ribbonLine(float y, float waveY, float coreW, float glowK) {
    float dTop = abs(y - waveY);
    float dBot = abs(y + waveY);
    float dist = min(dTop, dBot);
    float core = smoothstep(coreW, 0.0, dist);
    float glow = exp(-dist * glowK) * 0.65;
    return core * 3.2 + glow;
}

vec2 discCenterUv() {
    return (u_disc.xy - 0.5 * iResolution.xy) / iResolution.y;
}

float discRadiusUv() {
    return u_disc.z / iResolution.y;
}

bool discOccluderOn() {
    return u_disc.w > 0.5 && u_disc.z > 0.5;
}

// Soft disc contour with rounded left/right poles (no sharp bottleneck pinch).
float softDiscLift(float dx, float r, float apron, float waveH) {
    float reach = r + apron;
    float q = clamp(dx / reach, 0.0, 1.0);
    float lift = r * (1.0 - pow(q, 2.35));

    // Smoothly close side poles into the open horizontal wave instead of pinching to zero.
    float poleClose = smoothstep(r * 0.66, reach * 0.94, dx);
    poleClose = poleClose * poleClose * (3.0 - 2.0 * poleClose);
    return mix(lift, waveH, poleClose);
}

// Wrap mirrored ribbons around the disc; stick controls how tightly layers hug the edge.
float adaptWaveHeight(float waveH, vec2 uv, float animTime, float stick) {
    if (!discOccluderOn()) return waveH;
    vec2 dc = discCenterUv();
    float r = discRadiusUv() * mix(0.992, 1.018, 1.0 - stick);
    float dx = abs(uv.x - dc.x);
    float apron = r * mix(0.38, 0.58, stick);
    float reach = r + apron;

    // Begin bending before the disc silhouette so horizontal waves curve in smoothly.
    float columnBlend = 1.0 - smoothstep(r * 0.58, reach, dx);
    float dist = length(uv - dc);
    float cornerEase = 1.0 - smoothstep(reach * 1.02, r * 0.50, dist);
    float bend = clamp(max(columnBlend, cornerEase), 0.0, 1.0);
    bend = bend * bend * (3.0 - 2.0 * bend);
    if (bend <= 0.0) return waveH;

    float lift = softDiscLift(dx, r, apron, waveH);

    // Extra 2D soften where upper/lower arcs meet the left/right poles.
    float sideAngle = abs(atan(uv.y - dc.y, max(dx, 0.0008)));
    float hornBlend = smoothstep(0.42, 0.95, sideAngle) * smoothstep(r * 0.80, r + apron * 0.45, dx);
    hornBlend = hornBlend * hornBlend * (3.0 - 2.0 * hornBlend);
    lift = mix(lift, waveH, hornBlend * 0.55);

    float angle = atan(uv.y - dc.y, uv.x - dc.x);
    float arcU = fract(angle / 6.2831853 - animTime * 0.22 * kScroll);
    float arcWave = waveAt(arcU, waveH / max(kMirrorSpread, 0.001)) * kMirrorSpread;
    float reactive = max(waveH, arcWave * 1.12);

    float pulse = 1.0 + clamp(reactive / max(lift, 0.002) - 1.0, -0.18, 0.22) * mix(0.14, 0.32, stick);
    float hugged = lift * pulse;
    float loose = lift + reactive * mix(0.55, 0.18, stick);
    float adapted = mix(loose, hugged, stick);

    // Seamless handoff back to the open horizontal wave past the apron.
    float openMerge = smoothstep(reach * 0.78, reach * 1.02, dx);
    openMerge = openMerge * openMerge * (3.0 - 2.0 * openMerge);
    float wrapped = mix(adapted, waveH, openMerge);
    return mix(waveH, wrapped, bend);
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
    float beat    = pow(bassHit, 1.25);
    float vol     = bassHit + mid * 0.72 + treble * 0.35;
    float energy  = 0.80 + beat * 1.15 + vol * 0.42;

    float specU = uvScreen.x;
    float waveAxisY = discOccluderOn() ? discCenterUv().y : 0.0;
    float symY = abs(uv.y - waveAxisY);
    float baseAmp = 0.28 * energy;

    float mainScroll = t * 0.72;
    float mainU = scrolledSpecU(specU, mainScroll, 0.042);
    float wY = waveAt(mainU, baseAmp) * kMirrorSpread;
    wY = adaptWaveHeight(wY, uv, mainScroll, 1.0);
    float normH = clamp(wY / max(baseAmp * kMirrorSpread, 0.001), 0.0, 1.0);
    vec3 beamCol = coverAccent(uvScreen.x, normH);

    vec3 col = vec3(0.0);

    for (int i = 0; i < 6; i++) {
        float fI = float(i);
        float depthT = fI / 5.0;
        float zDepth = mix(1.55, 1.0, depthT);
        float scrollSpeed = mix(0.014, 0.055, depthT);
        float layerTime = t * mix(0.46, 0.84, depthT) - fI * 0.028;
        float layerU = scrolledSpecU(specU + (fI - 2.5) * 0.003 * zDepth, layerTime, scrollSpeed);

        float h = waveAt(layerU, baseAmp / zDepth) * kMirrorSpread;
        h = adaptWaveHeight(h, uv, layerTime, mix(0.45, 1.0, depthT));
        float n = noise(vec2(layerU * 22.0 + t * 1.8, fI * 1.1));
        h += (n * 2.0 - 1.0) * 0.022 * h;

        float line = ribbonLine(symY / zDepth, h, mix(0.0032, 0.0016, depthT), mix(55.0, 95.0, depthT));
        vec3 layerCol = coverAccent(uvScreen.x, clamp(h / max(baseAmp * kMirrorSpread, 0.001), 0.0, 1.0));
        col += layerCol * line * mix(0.14, 0.42, depthT) * (0.35 + vol * 0.75);
    }

    float trace = ribbonLine(symY, wY, 0.0014, 130.0);
    col += beamCol * trace * (0.55 + beat * 0.55);

    float horizon = exp(-symY * 45.0) * exp(-abs(uv.x) * 0.6);
    col += mix(u_primary.rgb, u_secondary.rgb, 0.45) * horizon * (0.12 + beat * 0.22);

    float peakGate = smoothstep(0.12, 0.35, wY);
    float abovePeak = smoothstep(wY, wY + 0.008, symY);
    float rayFalloff = exp(-(symY - wY) * mix(5.5, 9.0, treble));
    col += beamCol * abovePeak * rayFalloff * peakGate * (0.25 + beat * 0.45);

    float rayCol = exp(-abs(symY - wY) * 18.0) * smoothstep(wY * 0.7, wY, symY);
    col += beamCol * rayCol * peakGate * 0.18;

    vec2 fogUv = vec2(uvScreen.x * 95.0 - t * 12.0 * kScroll, symY * 55.0 + t * 8.0);
    float fog = fbm(fogUv * 0.35);
    float waveMask = exp(-abs(symY - wY) * 10.0) + exp(-symY * 6.0) * 0.35;
    col += beamCol * fog * fog * waveMask * (0.18 + vol * 0.35);

    vec2 pCell = floor(vec2(uvScreen.x * 88.0, symY * 48.0 + 24.0));
    for (int pj = -2; pj <= 2; pj++) {
        for (int pi = -2; pi <= 2; pi++) {
            vec2 c = pCell + vec2(float(pi), float(pj));
            float seed  = hash21(c);
            float seed2 = hash21(c + 19.3);
            if (seed < 0.32) continue;

            float px = clamp((c.x + seed2) / 88.0, 0.0, 1.0);
            float pxU = scrolledSpecU(px, mainScroll + seed * 0.4, 0.042);
            float surfH = waveAt(pxU, baseAmp) * kMirrorSpread;
            surfH = adaptWaveHeight(surfH, vec2((px - 0.5) * aspect, symY), mainScroll + seed * 0.4, 0.85);
            float force = surfH * (1.3 + beat * 1.1);

            float spd = 1.5 + seed * 2.2;
            float tau = fract(t * spd + seed2 * 6.28);
            float py = surfH + force * tau - (1.6 + seed) * tau * tau * 0.5;
            py = abs(py);

            float pUx = (px - 0.5) * aspect;
            float pd = length(vec2(uv.x - pUx, symY - py));
            float bokeh = exp(-pd * pd / (0.00004 + seed * 0.00008));

            col += coverAccent(px, clamp(force / max(baseAmp * kMirrorSpread, 0.001), 0.0, 1.0))
                * bokeh * force * (0.25 + vol * 0.45);
        }
    }

    vec2 flarePos = vec2(0.12, 0.14);
    float flare = exp(-length(uv - flarePos) * 7.5) * (0.35 + beat * 0.55);
    float flareRing = exp(-abs(length(uv - flarePos) - 0.06) * 55.0) * 0.12;
    col += mix(u_secondary.rgb, u_tertiary.rgb, 0.35) * flare;
    col += u_primary.rgb * flareRing * beat;

    float vig = 1.0 - 0.32 * length(uvScreen - 0.5);
    col *= vig;

    fragColor = vec4(col * u_fade, u_fade);
}
