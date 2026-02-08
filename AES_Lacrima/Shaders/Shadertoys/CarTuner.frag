void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord.xy / iResolution.xy;
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    
    // 1. BACKGROUND GRADIENT
    vec3 col = mix(vec3(0.01, 0.01, 0.04), vec3(0.0, 0.05, 0.1), uv.y);
    float ground = smoothstep(0.15, 0.0, abs(p.y + 0.35));
    col += ground * vec3(0.0, 0.4, 0.8) * 0.3;

    // 2. THE INTEGRATED SPECTRUM (only remaining element)
    float bodySlant = p.x + p.y * 0.4;
    vec2 specUv = vec2((bodySlant + 0.45) * 12.0, (p.y + 0.22) * 35.0);
    vec2 id = floor(specUv);
    vec2 gv = fract(specUv);

    float isInsideBarArea = step(0.0, id.x) * step(id.x, 9.0) * step(0.0, id.y) * step(id.y, 14.0);
    float freq = texture(iChannel0, vec2(id.x / 10.0, 0.5)).r;
    
    float barActive = step(id.y / 15.0, freq) * isInsideBarArea;
    float ledGrid = step(0.1, gv.x) * step(gv.x, 0.9) * step(0.2, gv.y) * step(gv.y, 0.8);
    
    vec3 ledCol = mix(vec3(0.0, 1.0, 0.5), vec3(1.0, 0.0, 0.2), id.y / 15.0);
    col += ledCol * (barActive * ledGrid * 2.0);

    // Additional bars at the start and end edges
    float leftFreq = texture(iChannel0, vec2(0.0, 0.5)).r;
    float rightFreq = texture(iChannel0, vec2(1.0, 0.5)).r;
    float leftEdge = smoothstep(0.05, 0.0, abs(p.x + 0.92));
    float rightEdge = smoothstep(0.05, 0.0, abs(p.x - 0.92));
    float leftHeight = smoothstep(leftFreq * 0.9, leftFreq * 0.3 + 0.05, abs(p.y));
    float rightHeight = smoothstep(rightFreq * 0.9, rightFreq * 0.3 + 0.05, abs(p.y));
    vec3 edgeCol = vec3(1.0, 0.4, 0.1);
    col += edgeCol * leftEdge * (1.0 - leftHeight);
    col += edgeCol * rightEdge * (1.0 - rightHeight);

    fragColor = vec4(col, 1.0);
}