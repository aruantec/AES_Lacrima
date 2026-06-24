#define PI  3.14159265359
#define TAU 6.28318530718

vec2 rot2(vec2 p, float a) {
    float c = cos(a), s = sin(a);
    return mat2(c, -s, s, c) * p;
}

float hash(float n) {
    return fract(sin(n * 127.1 + 311.7) * 43758.5453);
}

// Smooth cubic easing — keeps motion buttery
float ease(float x) {
    x = clamp(x, 0.0, 1.0);
    return x * x * (3.0 - 2.0 * x);
}

// simple hashing for noise
float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    // Quintic interpolation
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

//--- ElectricGalaxy-specific noise / fbm (slightly different style) ----
float eg_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n = i.x + i.y * 57.0;
    return mix(mix(hash(n), hash(n + 1.0), f.x),
               mix(hash(n + 57.0), hash(n + 58.0), f.x), f.y);
}

float eg_fbm(vec2 p) {
    float f = 0.0;
    f += 0.5000 * eg_noise(p); p = p * 2.02;
    f += 0.2500 * eg_noise(p); p = p * 2.03;
    f += 0.1250 * eg_noise(p); p = p * 2.01;
    f += 0.0625 * eg_noise(p);
    return f / 0.9375;
}

vec3 coverGradientBackground(vec2 uvScreen) {
    float blendMid = smoothstep(0.0, 0.55, uvScreen.y);
    float blendTop = smoothstep(0.45, 1.0, uvScreen.y);
    return mix(mix(u_primary.rgb, u_secondary.rgb, blendMid), u_tertiary.rgb, blendTop);
}

vec3 coverAccent(float normAngle, float barHeight) {
    vec3 base = mix(u_primary.rgb, u_secondary.rgb, normAngle);
    return mix(base, u_tertiary.rgb, barHeight * 0.40);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = iResolution.xy;
    vec2 uv  = (fragCoord - 0.5 * res) / res.y;
    vec2 uvScreen = fragCoord / res;
    float t  = iTime;

    // audio bands
    float bass = texture(iChannel0, vec2(0.04, 0.25)).r;
    float mid  = texture(iChannel0, vec2(0.18, 0.25)).r;
    float treb = texture(iChannel0, vec2(0.78, 0.25)).r;
    // soften bass peaks to avoid over-brightness
    float bassL = pow(bass, 0.6);
    // combined volume for blur arcs uses softened bass
    float vol = bassL + mid * 0.7 + treb * 0.3;

    vec3 lightingCol = vec3(0.0);
    {
        float zDepth = 1.3;
        vec2 luv = uv * zDepth;
        float raySpin = -t * 0.09;
        vec2 ruv = rot2(luv, raySpin);
        float a = atan(ruv.y, ruv.x);
        float normAngle = (a + PI) / TAU;
        float barHeight = texture(iChannel0, vec2(normAngle, 0.5)).r;
        float d2 = length(ruv) + 0.001;

        float pPulse = 1.0 + bassL * 0.22;
        vec2 p = pPulse * vec2(cos(a * 1.4), sin(a * 1.4)) * 0.12;
        float d1 = length(ruv - p) + 0.001;

        float logDist = log(d2) * 0.25 - raySpin * 0.35;
        vec2 uv2 = 2.0 * cos(logDist + log(vec2(d1, d2) / (d1 + d2)));
        float c = cos(10.0 * length(uv2) + raySpin * 3.2);
        float rayPattern = abs(cos(9.0 * a) * ruv.x + sin(9.0 * a) * ruv.y);
        float intensity = exp(-8.5 * (rayPattern + 0.1 * c - barHeight * 0.42));

        vec3 baseColor = coverAccent(normAngle, barHeight) * (0.82 + barHeight * 0.48 + bassL * 0.16);
        lightingCol = (0.5 + 0.5 * c) * baseColor * intensity;
        lightingCol += (pow(bassL, 0.7) * 0.05) * u_primary.rgb / d2;
        lightingCol *= 0.36;
    }

    // --- Electric Galaxy background layer --------------------------
    vec3 galaxyCol = vec3(0.0);
    {
        float gDepth = 0.9; // slightly closer than lighting
        vec2 guv = uv * gDepth;
        float a = atan(guv.y, guv.x);
        float d = length(guv);
        float normAngle = pow(abs(cos(a + 0.78539)), 0.7);
        float rawHeight = texture(iChannel0, vec2(normAngle, 0.5)).r;
        float barHeight = pow(rawHeight, 0.8) * (0.6 + 0.4 * normAngle);
        float bassG = bass;

        for(int i = 0; i < 3; i++) {
            float it = float(i);
            float tt = t * (0.55 + it * 0.12);
            float noiseVal = eg_fbm(vec2(a * 3.0 + it, tt));
            float radius = 0.2 + 0.28 * barHeight + 0.1 * noiseVal;
            float arcDist = abs(d - radius);
            float intensity = 0.002 / (arcDist + 0.005);
            intensity *= smoothstep(0.4, 0.0, arcDist);
            float flicker = step(0.5, eg_noise(vec2(tt * 10.0, it)));
            galaxyCol += u_primary.rgb * intensity * (0.5 + 0.5 * flicker) * (barHeight + 0.5);
        }

        float spikes = eg_fbm(vec2(a * 10.0, t * 4.0));
        float spikeIntensity = smoothstep(0.7 - barHeight * 0.3, 1.0, spikes);
        galaxyCol += u_primary.rgb * spikeIntensity * (0.2 / (d + 0.1)) * (barHeight + 0.2);
        galaxyCol += u_primary.rgb * (0.015 / (d + 0.01)) * (bassG + 0.18);
        float sparks = hash(dot(guv, vec2(12.9898, 78.233)) + t);
        if (sparks > 0.99 && d < 0.5 * barHeight + 0.2) {
            galaxyCol += vec3(1.0) * bassG;
        }
        galaxyCol *= 0.5; // dim
    }

    vec3 colE = vec3(0.0);
    float mainR = 0.78;

    // ==== GLOWING ATOM =================================================
    {
        float d = length(uv);
        float orbMask = smoothstep(mainR * 0.42, 0.0, d);
        // disable music-driven reaction for the atom
        float atomE = 0.0; // no jumping
        // nucleus
        float nucR   = 0.08 + atomE * 0.04;
        float nuc    = exp(-6.0 * d / nucR);
        vec3  nucCol = mix(u_primary.rgb, u_secondary.rgb, 0.35) * (1.0 + atomE * 2.0);
        colE += nucCol * nuc * 0.85 * orbMask;

        // orbital rings + electrons
        for (int o = 0; o < 3; o++) {
            float fo = float(o);
            float tilt = fo * PI / 3.0 + t * (0.07 + fo * 0.03);
            float oRad = 0.14 + fo * 0.06 + atomE * 0.03;
            oRad *= 1.3; // enlarge rings by 30%

            vec2 ouv = rot2(uv, tilt);
            float eccen = 0.38 + fo * 0.06;
            vec2 euv = vec2(ouv.x, ouv.y / eccen);
            float eDist = abs(length(euv) - oRad);

            float ring = exp(-50.0 * eDist) * (0.50 + atomE * 1.20);
            // ring color scaled from themeColor
            vec3 rCol = mix(u_primary.rgb, u_tertiary.rgb, fo / 2.0) * (0.8 + atomE * 1.4);

            // electric string effect
            float ang = atan(euv.y, euv.x);
            float pulse = pow(abs(sin(ang * 60.0 + t * 12.0)), 24.0);
            float stringGlow = pulse * (0.4 + atomE * 0.8);
            vec3  sCol = mix(u_secondary.rgb, u_primary.rgb, 0.55);
            colE += sCol * ring * stringGlow * orbMask;

            colE += rCol * ring * 0.80 * orbMask;

            // electron
            float eAngle = t * (0.35 + fo * 0.12) + fo * TAU / 3.0;
            vec2  ePos = vec2(cos(eAngle) * oRad, sin(eAngle) * oRad * eccen);
            ePos = rot2(ePos, -tilt);

            float eDot  = length(uv - ePos);
            float eSize = 0.014 + atomE * 0.008;
            float eGlow = exp(-5.0 * eDot / eSize);
            vec3  eCol  = mix(u_secondary.rgb, u_primary.rgb, 0.4) * (1.1 + atomE * 1.6);
            colE += eCol * eGlow * 1.00 * orbMask;

        }

        // soft inner breath glow for atom
        float innerGlow = exp(-3.5 * d / (0.25 + atomE * 0.10));
        colE += mix(u_primary.rgb, u_tertiary.rgb, 0.25) * innerGlow * (0.30 + atomE * 0.50) * orbMask;
    }

    // --- Glowing spectrum waves (parallax depth + spectrum bounce) -----------
    vec3 waveCol = vec3(0.0);
    {
        const int numLayers = 6;
        for (int i = 0; i < numLayers; i++) {
            float fI = float(i);
            float depthT = fI / 5.0;

            // Back layers: wider X, slower scroll, softer. Front: tighter, faster, brighter.
            float parallaxScale = mix(1.52, 1.06, depthT);
            float layerDepth = mix(1.42, 1.0, depthT);
            float scrollSpeed = mix(0.011, 0.048, depthT);
            float layerTime = t * mix(0.42, 0.78, depthT) - fI * 0.035;

            float pX = uv.x * parallaxScale + (fI - 2.5) * 0.042 * sin(t * 0.20 + fI * 0.62);
            float pUvScreenX = pX * (res.y / res.x) + 0.5;

            float scrollX = pUvScreenX - layerTime * scrollSpeed;
            float bin = fract(scrollX);
            float prevBin = texture(iChannel0, vec2(fract(bin - 1.0 / 160.0), 0.5)).r;
            float nextBin = texture(iChannel0, vec2(fract(bin + 1.0 / 160.0), 0.5)).r;
            float binHeight = (texture(iChannel0, vec2(bin, 0.5)).r + prevBin + nextBin) / 3.0;
            binHeight = pow(binHeight, 0.78);

            float waveOffset = (binHeight - 0.42) * mix(0.09, 0.15, depthT);
            float jitter = (fbmLow(vec2(pX * mix(4.0, 7.5, depthT) + layerTime * 1.3, layerTime)) * 2.0 - 1.0);
            jitter *= mix(0.045, 0.075, depthT) * (0.18 + vol * 0.85);

            float layerY = uv.y / layerDepth;
            float currentY = waveOffset + jitter / layerDepth;
            float dist = abs(layerY - currentY);

            float colorMix = smoothstep(0.05, 0.85, pUvScreenX);
            vec3 layerCol = mix(u_primary.rgb, u_secondary.rgb, colorMix);
            layerCol = mix(layerCol, u_tertiary.rgb, binHeight * 0.35);

            float spark = noise(vec2(pX * mix(10.0, 16.0, depthT) + layerTime * 2.6, waveOffset * 4.0));
            float sparkIntensity = mix(0.30, 0.50, depthT) + 1.05 * spark;

            float coreWidth = mix(0.0055, 0.0032, depthT) + 0.003 * pow(bassL, 1.4);
            float core = smoothstep(coreWidth, 0.0, dist);
            float innerGlow = exp(-dist * mix(38.0, 58.0, depthT)) * (0.34 + 0.36 * pow(bassL, 1.2));
            float outerGlow = exp(-dist * mix(14.0, 20.0, depthT)) * mix(0.05, 0.09, depthT);
            float layerAlpha = mix(0.42, 0.92, depthT);

            waveCol += layerCol * (core * mix(2.4, 3.4, depthT) + innerGlow + outerGlow)
                * sparkIntensity * layerAlpha * (0.28 + vol * 1.15);
        }
    }

    colE = min(colE, vec3(1.4));
    colE = mix(colE, colE + u_primary.rgb * 0.08 * ease(bass * 0.8 + mid * 0.3), 0.20);

    vec3 finalCol = coverGradientBackground(uvScreen) * 0.32;
    finalCol += lightingCol + galaxyCol + colE + waveCol;
    finalCol *= 0.72;
    fragColor = vec4(finalCol * u_fade, 1.0);
}
