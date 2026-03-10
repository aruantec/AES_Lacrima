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

// 2D noise for electricity with tiling-friendly coordinates
float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    // Use a simpler hashing that is better for angular wrapping
    float a = hash01(mod(i.x, 360.0) + i.y * 57.0);
    float b = hash01(mod(i.x + 1.0, 360.0) + i.y * 57.0);
    float c = hash01(mod(i.x, 360.0) + (i.y + 1.0) * 57.0);
    float d = hash01(mod(i.x + 1.0, 360.0) + (i.y + 1.0) * 57.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * noise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

// Electric arc function following a circular path
float electricArc(vec2 uv, vec2 center, float radius, float t, float seed) {
    vec2 p = uv - center;
    float d = length(p);
    float angle = atan(p.y, p.x);
    
    // Use circular noise coordinates to avoid 180-degree seams
    vec2 circ = vec2(cos(angle), sin(angle)) * 2.5;
    float distortion = fbm(circ + vec2(seed, t * 1.5)) * 0.18;
    float arcPath = abs(d - (radius + distortion));
    
    // Core of the bolt
    float glow = 0.0015 / (arcPath + 0.001);
    // Outer fuzzy glow
    glow += 0.02 * exp(-arcPath * 25.0);
    
    return glow;
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

    // smooth beat envelope — more reactive mix
    float beat = ease(bass * 1.5 + mid * 0.6);
    float sparkle = ease(treb * 1.8);
    float pulse = ease(0.75 * beat + 0.5 * bass);

    // ---- scene --------------------------------------------------------
    // Internal-pressure feel: the whole orb inflates actively with the beat.
    float orbInflate = 0.010 + 0.120 * pulse + 0.025 * sin(t * 0.80 + pulse * 1.7);
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

    // ==== INTERNAL ELECTRICITY (Big Sphere) ============================
    // Core plasma effect inside the big orb that reacts to volume
    float corePulse = beat * 1.5 + sparkle * 0.5;
    if (corePulse > 0.01) {
        float d = length(uv);
        // Distort coordinates for winding arcs
        // Use cos/sin of angle for noise input to ensure perfect wrapping
        float angle = atan(uv.y, uv.x);
        vec2 circularUV = vec2(cos(angle), sin(angle)) * 1.5;
        float coreDistort = fbm(circularUV + vec2(0.0, t * 1.5)) * 0.25;
        float coreRadius = (mainR * 0.8) + coreDistort;
        
        // Multiple internal arc layers
        float arcA = 0.002 / (abs(d - coreRadius * 0.95) + 0.002);
        float arcB = 0.002 / (abs(d - coreRadius * 0.65) + 0.002);
        float arcC = 0.0015 / (abs(d - coreRadius * 1.15) + 0.0015);
        
        // Central core glow
        float coreGlow = exp(-d * 4.0) * corePulse * 0.45;
        
        float flicker = hash01(t * 30.0) > 0.1 ? 1.0 : 0.3;
        float totalCoreArc = (arcA + arcB * 0.7 + arcC * 0.5) * corePulse * flicker;
        
        // Use a mix of primary neon and white core for the lightning
        vec3 coreNeon = mix(neonBase, u_primary.rgb, 0.5);
        vec3 coreArcCol = mix(coreNeon, vec3(0.9, 0.95, 1.0), 0.75);
        
        col += coreArcCol * totalCoreArc * 1.2;
        col += coreNeon * coreGlow;
    }

    // ==== 140 child spheres on a fibonacci sphere ======================
    // Optimized for performance while maintaining the "Big Orb" density.
    const int N = 140;
    float invN = 1.0 / float(N);
    float cPhi = cos(PHI);
    float sPhi = sin(PHI);
    float cTh = 1.0;
    float sTh = 0.0;

    for (int i = 0; i < N; i++)
    {
        float fi = float(i);
        // Correct fibonacci sphere point calculation for density
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
        
        // Sphere screen radius approximation for early out (approx 0.1 * scl)
        // Increased multiplier to 4.5 to allow jumping spheres to render correctly
        float bR_approx = 0.12; 
        float max_rc = bR_approx * base_scl * 4.5; 
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
        // wider gate — softer sounds trigger movement
        float gate  = smoothstep(0.02, 0.15, freqS);

        // highly reactive displacement
        float disp = gate * (freqS * 0.65 + beat * 0.25) * motion;

        // how much this ball is "active" (0 = resting, 1 = fully jumped)
        float activity = clamp(disp * 3.5 + sparkle * gate * 0.05, 0.0, 1.0);
        float jumpHeight = clamp(disp * 4.5, 0.0, 1.0); // normalized actual jump amount

        vec3 pos = sn * (mainR + disp);

        // rotate whole formation using precomputed trig
        pos = dir * (mainR + disp);


        // perspective projection
        float ez  = pos.z + camD;
        float scl = 1.55 / ez;
        vec2  prj = pos.xy * scl;

        // child-sphere screen radius (grows gently with displacement)
        float bR = 0.088 + hash01(fi + 3.0) * 0.012;
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
            // Early exit for distant glow pixels
            if (pd2 < glowR2 * 10.0) {
                ballNeon = neonColor(fi);
                neonReady = true;
                float pd = sqrt(pd2);
                float jumpGlow = activity * (0.45 + 1.15 * jumpHeight);
                float glow = exp2(-4.85 * pd / glowR) * jumpGlow;
                col += ballNeon * glow * 0.28;
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

        // dark gray/almost black base palette + subtle green accent
        vec3 cBase = vec3(0.012, 0.012, 0.015);
        vec3 cRim  = vec3(0.10, 0.40, 0.90);
        vec3 cSpec = vec3(0.15, 0.50, 1.00);
        
        // Sparse green accent mask so only a subset of spheres get a tint.
        float greenPick = hash01(fi * 3.17 + 9.2);
        float greenMask = smoothstep(0.85, 0.99, greenPick);
        float greenDrift = 1.0; // static 1.0 for performance if needed, or simple sin
        if (greenMask > 0.0) greenDrift = 0.70 + 0.30 * (0.5 + 0.5 * sin(t * 0.33 + fi * 0.11));
        vec3 cAccentE = vec3(0.10, 0.95, 0.35); // green
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

        // ---- ELECTRIC STRINGS on active spheres ----------------------
        if (activity > 0.15) {
            vec3 pOffset = sn * (mainR + 0.1);
            vec2 pCenter = (rot * pOffset).xy * (1.55 / (pOffset.z + camD));
            
            // Multiple arcs at different radii to simulate the "plasma ball" effect
            float a1 = electricArc(uv, pCenter, sr * 1.35, t * 1.2, fi);
            float a2 = electricArc(uv, pCenter, sr * 0.95, t * 0.8, fi * 1.5);
            float a3 = electricArc(uv, pCenter, sr * 1.85, t * 1.5, fi * 2.0);
            
            // Interaction with distance
            float dist = length(uv - pCenter);
            float mask = smoothstep(sr * 3.5, sr * 0.5, dist);
            
            // Electric flicker logic
            float flicker = hash01(t * 24.0 + fi) > 0.2 ? 1.0 : 0.4;
            float totalArc = (a1 + a2 * 0.8 + a3 * 0.6) * activity * flicker * mask;
            
            // Combine colors: White-ish core with neon glow
            vec3 arcCol = mix(ballNeon, vec3(0.9, 0.95, 1.0), 0.7);
            bc += arcCol * totalArc * 1.5;
        }

        float alpha = smoothstep(1.0, 0.93, lrd);
        bestZ = ez;
        col   = mix(col, bc, alpha);

    }

    col = min(col, vec3(1.4));                    // clamp over-bright

    // ---- subtle theme tint --------------------------------------------
    col = mix(col, col + u_primary.rgb * 0.08 * beat, 0.20);

    fragColor = vec4(col * u_fade, 1.0);
}
