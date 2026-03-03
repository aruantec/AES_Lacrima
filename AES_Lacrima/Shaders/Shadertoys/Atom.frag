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

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = iResolution.xy;
    vec2 uv  = (fragCoord - 0.5 * res) / res.y;
    float t  = iTime;

    // dynamic color that cycles to a new random hue every 6 seconds
    float period = 6.0;
    float phase = floor(t / period);
    float u = smoothstep(0.0, 1.0, mod(t, period) / period);
    vec3 c1 = vec3(hash(phase + 0.1), hash(phase + 0.2), hash(phase + 0.3));
    vec3 c2 = vec3(hash(phase + 1.1), hash(phase + 1.2), hash(phase + 1.3));
    vec3 themeColor = mix(c1, c2, u);
    // boost saturation/brightness for neon feel
    themeColor = mix(vec3(0.5), themeColor, 0.5) * 1.5;

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
        float zDepth = 1.3;            // scale factor to push effect "back"
        vec2 luv = uv * zDepth;
        float a = atan(luv.y, luv.x);
        float normAngle = (a + 3.14159) / (2.0 * 3.14159);
        float barHeight = texture(iChannel0, vec2(normAngle, 0.5)).r;
        float pPulse = 1.0 + (bassL * 0.3);
        vec2 p = pPulse * cos(a + t) * vec2(cos(0.5 * t), sin(0.3 * t));
        float d1 = length(luv - p) + 0.001;
        float d2 = length(luv) + 0.001;
        float logDist = log(d2) * 0.25 - 0.5 * t;
        vec2 uv2 = 2. * cos(logDist + log(vec2(d1, d2) / (d1 + d2)));
        float c = cos(10. * length(uv2) + 4. * t);
        float rayPattern = abs(cos(9. * a + t) * luv.x + sin(9. * a + t) * luv.y);
        // lower barHeight coefficient to prevent extreme brighten
        float intensity = exp(-8.0 * (rayPattern + 0.1 * c - (barHeight * 0.4)));
        // use theme color palette for background lighting
        vec3 baseColor = themeColor * (1.0 + barHeight);
        lightingCol = (0.5 + 0.5 * c) * baseColor * intensity;
        lightingCol += (pow(bassL,0.7) * 0.08) * themeColor / d2;
        // dim a bit to keep atom in front
        lightingCol *= 0.5;
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
            float tt = t * (1.0 + it * 0.2);
            float noiseVal = eg_fbm(vec2(a * 3.0 + it, tt));
            float radius = 0.2 + 0.3 * barHeight + 0.1 * noiseVal;
            float arcDist = abs(d - radius);
            float intensity = 0.002 / (arcDist + 0.005);
            intensity *= smoothstep(0.4, 0.0, arcDist);
            float flicker = step(0.5, eg_noise(vec2(tt * 10.0, it)));
            galaxyCol += themeColor * intensity * (0.5 + 0.5 * flicker) * (barHeight + 0.5);
        }

        float spikes = eg_fbm(vec2(a * 10.0, t * 5.0));
        float spikeIntensity = smoothstep(0.7 - barHeight * 0.3, 1.0, spikes);
        galaxyCol += themeColor * spikeIntensity * (0.2 / (d + 0.1)) * (barHeight + 0.2);
        galaxyCol += themeColor * (0.02 / (d + 0.01)) * (bassG + 0.2);
        float sparks = hash(dot(guv, vec2(12.9898, 78.233)) + t);
        if (sparks > 0.99 && d < 0.5 * barHeight + 0.2) {
            galaxyCol += vec3(1.0) * bassG;
        }
        galaxyCol *= 0.5; // dim
    }

    vec3 colE = vec3(0.0); // start with black
    float mainR = 0.78; // used for orb mask, matches LiveOrb

    // ==== GLOWING ATOM =================================================
    {
        float d = length(uv);
        float orbMask = smoothstep(mainR * 0.42, 0.0, d);
        // disable music-driven reaction for the atom
        float atomE = 0.0; // no jumping
        // nucleus
        float nucR   = 0.08 + atomE * 0.04;
        float nuc    = exp(-6.0 * d / nucR);
        vec3  nucCol = vec3(0.35, 0.80, 1.2) * (1.0 + atomE * 2.0);
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
            vec3 rCol = themeColor * (0.8 + atomE * 1.4);

            // electric string effect
            float ang = atan(euv.y, euv.x);
            float pulse = pow(abs(sin(ang * 60.0 + t * 12.0)), 24.0);
            float stringGlow = pulse * (0.4 + atomE * 0.8);
            vec3  sCol = themeColor * 1.2; // brighter string variant
            colE += sCol * ring * stringGlow * orbMask;

            colE += rCol * ring * 0.80 * orbMask;

            // electron
            float eAngle = t * (0.35 + fo * 0.12) + fo * TAU / 3.0;
            vec2  ePos = vec2(cos(eAngle) * oRad, sin(eAngle) * oRad * eccen);
            ePos = rot2(ePos, -tilt);

            float eDot  = length(uv - ePos);
            float eSize = 0.014 + atomE * 0.008;
            float eGlow = exp(-5.0 * eDot / eSize);
            vec3  eCol  = vec3(0.50, 0.90, 1.2) * (1.1 + atomE * 1.6);
            colE += eCol * eGlow * 1.00 * orbMask;

        }

        // soft inner breath glow for atom
        float innerGlow = exp(-3.5 * d / (0.25 + atomE * 0.10));
        colE += vec3(0.12, 0.32, 0.88) * innerGlow * (0.30 + atomE * 0.50) * orbMask;
    }
    // --- electric blur-style arc strings (blue) across whole screen --------
    {
        const int numLayers = 6;
        for(int i = 0; i < numLayers; i++) {
            float fI = float(i);
            float layerTime = t - fI * 0.03;

            float pX = uv.x + (fI - 3.0) * 0.03 * sin(t * 0.35);
            float pUvScreenX = pX * (res.y / res.x) + 0.5;

            float yPath = 0.1 * sin(pX * 2.0 + layerTime * 0.5) * (1.0 + bass * 1.2);
            float jitter = (fbmLow(vec2(pX * 6.0 + layerTime * 3.0, layerTime * 2.5)) * 2.0 - 1.0) * 0.11;
            jitter *= (0.15 + vol * 1.8);

            float currentY = yPath + jitter;
            float dist = abs(uv.y - currentY);

            // constant theme color palette with slight variation from screen pos
            float colorMix = smoothstep(0.1, 0.7, pUvScreenX);
            vec3 blueBase = themeColor; // now dynamic
            vec3 layerCol = blueBase * mix(0.8, 1.2, colorMix);

            // simple spark modulation
            float spark = noise(vec2(pX * 15.0 + layerTime * 5.0, yPath * 5.0));
            float sparkIntensity = 0.3 + 1.4 * spark;

            float coreWidth = (0.005 + 0.004 * pow(bass,1.5));
            float core = smoothstep(coreWidth, 0.0, dist);
            float innerGlow = exp(-dist * (60.0 - 15.0 * pow(bass,1.5))) * (0.5 + 0.4 * pow(bass,1.5));
            float outerGlow = exp(-dist * 20.0) * 0.08;
            float layerAlpha = pow(1.0 - (fI / float(numLayers)), 2.0);

            colE += layerCol * (core * 4.0 + innerGlow + outerGlow) * sparkIntensity * layerAlpha * (0.4 + vol * 1.8);
        }
    }

    colE = min(colE, vec3(1.4));
    colE = mix(colE, colE + u_primary.rgb * 0.08 * ease(bass * 0.8 + mid * 0.3), 0.20);

    vec3 finalCol = lightingCol + galaxyCol + colE;
    // overall dimming to prevent excessive brightness
    finalCol *= 0.8;
    fragColor = vec4(finalCol * u_fade, 1.0);
}
