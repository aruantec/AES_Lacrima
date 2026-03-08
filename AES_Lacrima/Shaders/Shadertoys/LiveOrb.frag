// LiveOrb.frag — music-reactive orb of 3D spheres with neon glow on jump

#define PI  3.14159265359
#define TAU 6.28318530718
#define PHI 2.39996322972       // golden angle for fibonacci sphere

float hash01(float x) {
    x = fract(x * 0.1031);
    x *= x + 33.33;
    x *= x + x;
    return fract(x);
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

    // smooth beat envelope — weighted but not overwhelming
    float beat = ease(bass * 1.0 + mid * 0.35);
    float sparkle = ease(treb * 1.2);
    float pulse = ease(0.75 * beat + 0.25 * bass);

    // ---- scene --------------------------------------------------------
    // Internal-pressure feel: the whole orb gently inflates with the beat.
    float orbInflate = 0.010 + 0.050 * pulse + 0.012 * sin(t * 0.80 + pulse * 1.7);
    float mainR = 0.78 + orbInflate;            // main sphere radius
    float camD  = 2.85;                          // camera distance
    vec3  light = normalize(vec3(-0.5, 0.55, 0.85));

    // vivid neon blue for resting spheres
    vec3 neonBase = vec3(0.12, 0.45, 1.0);

    // rotation: constant Y spin is the primary driver, but the orb also slowly
    // tumbles on X and Z with incommensurate periods so it never loops predictably.
    // All values are pure time integrals (t * k or integrated sines) — no audio coupling
    // in the angle, so there are zero sudden jumps.
    float rY = t * 0.26;                                        // steady Y spin
    float rX = 0.18 * sin(t * 0.09)                            // slow forward/back tilt
             + 0.09 * sin(t * 0.17 + 1.1)                     // secondary wobble
             + 0.04 * sin(t * 0.31 + 2.5);                    // fine texture
    float rZ = 0.14 * sin(t * 0.07 + 0.8)                     // slow roll
             + 0.06 * sin(t * 0.19 + 1.9)                     // secondary roll
             + 0.03 * sin(t * 0.41 + 0.4);                    // fine texture

    // Precompute rotation trig once per pixel, not once per sphere.
    float cY = cos(rY), sY = sin(rY);
    float cX = cos(rX), sX = sin(rX);
    mat3 r_y = mat3(cos(rY), 0.0, sin(rY), 0.0, 1.0, 0.0, -sin(rY), 0.0, cos(rY));
    mat3 r_x = mat3(1.0, 0.0, 0.0, 0.0, cos(rX), -sin(rX), 0.0, sin(rX), cos(rX));
    mat3 r_z = mat3(cos(rZ), -sin(rZ), 0.0, sin(rZ), cos(rZ), 0.0, 0.0, 0.0, 1.0);
    mat3 rot = r_x * r_y; rot = r_z * rot;


    // ---- per-pixel output ---------------------------------------------
    vec3  col   = vec3(0.0);
    float bestZ = 1e5;                            // z-buffer (smaller = closer)

    // ==== 220 child spheres on a fibonacci sphere ======================
    const int N = 220;
    float invN = 1.0 / float(N);
    float cPhi = cos(PHI);
    float sPhi = sin(PHI);
    float cTh = 1.0;
    float sTh = 0.0;

    for (int i = 0; i < N; i++)
    {
        float fi = float(i);
        // fibonacci sphere point
        float yy   = 1.0 - (2.0 * fi + 1.0) * invN;
        float latR = sqrt(1.0 - yy * yy);
        vec3  sn   = vec3(latR * cTh, yy, latR * sTh);
        // fibonacci-angle recurrence: avoids per-iteration sin/cos(theta)
        float prevC = cTh;
        cTh = prevC * cPhi - sTh * sPhi;
        sTh = prevC * sPhi + sTh * cPhi;

        // per-ball audio lookup (scattered across spectrum)
        vec3 dir = rot * sn;
        vec3 base_pos = dir * mainR;
        float base_ez = base_pos.z + camD;
        float base_scl = 1.55 / base_ez;
        vec2 base_prj = base_pos.xy * base_scl;
        vec2 dv_base = uv - base_prj;
        float max_rc = 0.55 * base_scl;
        if (dot(dv_base, dv_base) > max_rc * max_rc) continue;

        float fU   = fract(fi * 0.618 + 0.07);
        float freq = texture(iChannel0, vec2(fU, 0.25)).r;

        // slow per-ball oscillation — organic breathing feel, unique phase per ball
        float ph      = hash01(fi) * TAU;
        float wave    = 0.5 + 0.5 * sin(t * 0.90 + ph); // local oscillation (slower)
        float wSmooth = ease(wave);
        float flow    = 0.5 + 0.5 * sin(t * 0.55 + ph + dot(sn, vec3(1.2, -0.9, 1.0)) * 2.1);
        float motion  = 0.78 * wSmooth + 0.22 * ease(flow);

        // moderate power curve — realistic falloff
        float freqS = freq * freq * (3.0 - 2.0 * freq);
        // wider gate — only genuinely active bands trigger movement
        float gate  = smoothstep(0.06, 0.24, freqS);

        // gentle displacement — orbs breathe with music, not thrash
        float disp = gate * (freqS * 0.22 + beat * 0.07) * motion;

        // how much this ball is "active" (0 = resting, 1 = fully jumped)
        float activity = clamp(disp * 4.0 + sparkle * gate * 0.03, 0.0, 1.0);
        float jumpHeight = clamp(disp * 6.5, 0.0, 1.0); // normalized actual jump amount

        vec3 pos = sn * (mainR + disp);

        // rotate whole formation using precomputed trig
        pos = dir * (mainR + disp);


        // perspective projection
        float ez  = pos.z + camD;
        float scl = 1.55 / ez;
        vec2  prj = pos.xy * scl;

        // child-sphere screen radius (grows gently with displacement)
        float bR = 0.068 + hash01(fi + 3.0) * 0.010;
        float sr = bR * scl * (1.0 + disp * 0.6 + orbInflate * 0.9);
        vec2  dv  = uv - prj;
        float pd2 = dot(dv, dv);
        vec3  ballNeon = vec3(0.0);
        bool  neonReady = false;

        // neon glow halo around jumping spheres (rendered before depth test)
        if (activity > 0.05)
        {
            float glowR  = sr * (2.0 + activity * 1.5 + jumpHeight * 0.9);
            float glowR2 = glowR * glowR;
            if (pd2 < glowR2 * 16.0) {
                ballNeon = neonColor(fi);
                neonReady = true;
                float pd = sqrt(pd2);
                float jumpGlow = activity * (0.45 + 1.25 * jumpHeight);
                float glow = exp2(-5.48 * pd / glowR) * jumpGlow;
                col += ballNeon * glow * 0.30;
            }
        }

        float srOut = sr * 1.1;
        if (pd2 > srOut * srOut) continue;                // early out

        // depth cull
        if (ez >= bestZ) continue;

        vec2  lc  = dv / sr;
        float lrd2 = dot(lc, lc);
        if (lrd2 > 1.0) continue;
        float lrd = sqrt(lrd2);
        float lz  = sqrt(1.0 - lrd2);       // local sphere z
        vec3  n   = normalize(vec3(lc, lz));

        // lighting
        float diff = max(dot(n, light), 0.0);
        float s = max(dot(reflect(-light, n), vec3(0, 0, 1)), 0.0);
        float s2 = s * s;
        float s4 = s2 * s2;
        float s8 = s4 * s4;
        float s16 = s8 * s8;
        float s32 = s16 * s16;
        float spec = s32 * s4; // s^36
        float rim  = pow(1.0 - lz, 2.6);
        float ao   = 0.35 + 0.65 * lz;

        // blue-dominant base palette + subtle green accent
        vec3 cBase = vec3(0.04, 0.12, 0.30);
        vec3 cRim  = vec3(0.10, 0.40, 0.90);
        vec3 cSpec = vec3(0.15, 0.50, 1.00);
        vec3 cAccentE = vec3(0.10, 0.95, 0.35); // green

        // Sparse green accent mask so only a subset of spheres get a tint.
        float greenPick = hash01(fi * 3.17 + 9.2);
        float greenMask = smoothstep(0.84, 0.98, greenPick);
        float greenDrift = 0.70 + 0.30 * (0.5 + 0.5 * sin(t * 0.33 + fi * 0.11));
        vec3 accentCol = cAccentE * greenMask * greenDrift;

        // bright neon-lit spheres even at rest
        vec3 bc = cBase * (0.50 + diff * 0.70) * ao;
        bc += cRim * rim * 0.45;
        bc += cSpec * spec * 1.10;

        float accentAmt = (0.05 + 0.07 * beat) * (0.25 + 0.75 * rim);
        bc += accentCol * accentAmt;

        // rest emissive bias keeps accents alive even off-beat
        bc += accentCol * 0.025 + neonBase * 0.025;

        // edge contour — individual sphere outlines
        float edge = smoothstep(0.84, 1.0, lrd);
        bc = mix(bc, cRim * 0.30, edge);

        // ---- RANDOM NEON GLOW on jumping spheres --------------------
        // Each ball gets its own vivid neon color when it jumps.
        if (!neonReady) {
            ballNeon = neonColor(fi);
        }
        float jumpInt = activity * (0.40 + 1.40 * jumpHeight);
        vec3 neonRim  = ballNeon * rim * jumpInt * 2.2;
        vec3 neonCore = ballNeon * diff * jumpInt * 0.50;
        vec3 neonSpec = ballNeon * spec * jumpInt * 1.30;
        bc += neonRim + neonCore + neonSpec;

        // Strong emissive bloom in the ball's neon color
        float emissive = activity * activity * (0.20 + 0.80 * jumpHeight);
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
