// LiveOrb.frag — music-reactive orb of 3D spheres with neon glow on jump

#define PI  3.14159265359
#define TAU 6.28318530718
#define PHI 2.39996322972       // golden angle for fibonacci sphere

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

// Generate a random bright neon color per ball index
vec3 neonColor(float id) {
    float h = fract(id * 0.618033 + 0.33);   // golden-ratio hue spread
    // HSV-to-RGB with full saturation and brightness
    vec3 rgb = clamp(abs(mod(h * 6.0 + vec3(0.0, 4.0, 2.0), 6.0) - 3.0) - 1.0, 0.0, 1.0);
    // boost to neon: saturate and brighten
    return mix(vec3(1.0), rgb, 0.75) * 1.1;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2  res  = iResolution.xy;
    vec2  uv   = (fragCoord - 0.5 * res) / res.y;
    float t    = iTime;

    // ---- audio (smoothed) ---------------------------------------------
    float bass = texture(iChannel0, vec2(0.04,  0.25)).r;
    float mid  = texture(iChannel0, vec2(0.18,  0.25)).r;
    float treb = texture(iChannel0, vec2(0.78,  0.25)).r;

    // smooth beat envelope — avoids spiky jumps
    float beat = ease(bass * 0.8 + mid * 0.3);

    // ---- scene --------------------------------------------------------
    float mainR = 0.78;                          // main sphere radius
    float camD  = 2.85;                          // camera distance
    vec3  light = normalize(vec3(-0.5, 0.55, 0.85));

    // vivid neon blue for resting spheres
    vec3 neonBase = vec3(0.12, 0.45, 1.0);

    // rotation: steady base + smooth music-driven drift (never jumps)
    float energy = (bass + mid + treb) / 3.0;
    float eSmooth = smoothstep(0.02, 0.30, energy);
    // layered slow sines give an organic "speeding up" feel without discontinuity
    float drift = eSmooth * (0.35 * sin(t * 0.18) + 0.18 * sin(t * 0.31) + 0.10 * sin(t * 0.47));
    float rY = t * 0.10 + drift;
    float rX = 0.28 + 0.04 * sin(t * 0.12);

    // ---- per-pixel output ---------------------------------------------
    vec3  col   = vec3(0.0);
    float bestZ = 1e5;                            // z-buffer (smaller = closer)

    // ==== 220 child spheres on a fibonacci sphere ======================
    const int N = 220;

    for (int i = 0; i < N; i++)
    {
        float fi = float(i);
        float fN = float(N);

        // fibonacci sphere point
        float yy    = 1.0 - (2.0 * fi + 1.0) / fN;
        float sPhi  = sqrt(1.0 - yy * yy);
        float theta = PHI * fi;
        vec3  sn    = vec3(sPhi * cos(theta), yy, sPhi * sin(theta));

        // per-ball audio lookup (scattered across spectrum)
        float fU   = fract(fi * 0.618 + 0.07);
        float freq = texture(iChannel0, vec2(fU, 0.25)).r;

        // very smooth per-ball oscillation — slow sine, unique phase
        float ph     = hash(fi) * TAU;
        float wave   = 0.5 + 0.5 * sin(t * 0.6 + ph);   // slow oscillation
        float wSmooth = ease(wave);                        // cubic ease

        // softer power curve — preserves more of the signal
        float freqS = pow(freq, 1.2);
        // low gate so most orbs with any energy can jump
        float gate  = smoothstep(0.05, 0.20, freqS);

        // displacement — direct music coupling, higher range
        float disp = gate * (freqS * 0.32 + beat * 0.18) * wSmooth;

        // how much this ball is "active" (0 = resting, 1 = fully jumped)
        float activity = clamp(disp * 3.5, 0.0, 1.0);

        vec3 pos = sn * (mainR + disp);

        // rotate whole formation
        pos.xz = rot2(pos.xz, rY);
        pos.yz = rot2(pos.yz, rX);

        // perspective projection
        float ez  = pos.z + camD;
        float scl = 1.55 / ez;
        vec2  prj = pos.xy * scl;

        // child-sphere screen radius (grows gently with displacement)
        float bR = 0.068 + hash(fi + 3.0) * 0.010;
        float sr = bR * scl * (1.0 + disp * 0.6);

        float pd = length(uv - prj);

        // per-ball random neon color for jumping
        vec3 ballNeon = neonColor(fi);

        // neon glow halo around jumping spheres (rendered before depth test)
        if (activity > 0.05)
        {
            float glowR  = sr * (2.0 + activity * 1.5);
            float glow   = exp(-3.8 * pd / glowR) * activity;
            col += ballNeon * glow * 0.18;
        }

        if (pd > sr * 1.1) continue;                // early out

        // depth cull
        if (ez >= bestZ) continue;

        vec2  lc  = (uv - prj) / sr;
        float lrd = length(lc);
        if (lrd > 1.0) continue;

        float lz  = sqrt(1.0 - lrd * lrd);       // local sphere z
        vec3  n   = normalize(vec3(lc, lz));

        // lighting
        float diff = max(dot(n, light), 0.0);
        float spec = pow(max(dot(reflect(-light, n), vec3(0, 0, 1)), 0.0), 36.0);
        float rim  = pow(1.0 - lz, 2.6);
        float ao   = 0.35 + 0.65 * lz;

        // vivid neon blue base palette
        vec3 cBase = vec3(0.04, 0.12, 0.30);
        vec3 cRim  = vec3(0.10, 0.40, 0.90);
        vec3 cSpec = vec3(0.15, 0.50, 1.00);

        // bright neon-lit spheres even at rest
        vec3 bc = cBase * (0.50 + diff * 0.70) * ao;
        bc += neonBase * rim * 0.50;
        bc += cRim * rim * 0.25;
        bc += cSpec * spec * 1.00;

        // neon emissive glow at rest — keeps spheres visibly blue
        bc += neonBase * 0.09;

        // edge contour — individual sphere outlines
        float edge = smoothstep(0.84, 1.0, lrd);
        bc = mix(bc, cRim * 0.30, edge);

        // ---- RANDOM NEON GLOW on jumping spheres --------------------
        // Each ball gets its own vivid neon color when it jumps.
        vec3 neonRim  = ballNeon * rim * activity * 1.8;
        vec3 neonCore = ballNeon * diff * activity * 0.35;
        vec3 neonSpec = ballNeon * spec * activity * 0.9;
        bc += neonRim + neonCore + neonSpec;

        // Strong emissive bloom in the ball's neon color
        float emissive = activity * activity * 0.25;
        bc += ballNeon * emissive;

        float alpha = smoothstep(1.0, 0.93, lrd);
        bestZ = ez;
        col   = mix(col, bc, alpha);
    }

    col = min(col, vec3(1.4));                    // clamp over-bright

    // ---- subtle theme tint --------------------------------------------
    col = mix(col, col + u_primary.rgb * 0.08 * beat, 0.20);

    fragColor = vec4(col * u_fade, 1.0);
}
