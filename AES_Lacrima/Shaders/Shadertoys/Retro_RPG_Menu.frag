precision highp float;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 res = max(iResolution.xy, vec2(1.0));
    vec2 uv = fragCoord / res;

    float blocks = min(res.x, res.y) / 2.4;
    blocks = clamp(blocks, 64.0, 320.0);
    vec2 blockUv = floor(uv * blocks) + 0.5;
    vec2 snapped = blockUv / blocks;

    vec3 color = vec3(0.0);
    if (iChannel1Size.x > 1.0)
        color = texture(iChannel1, snapped).rgb;
    else
        color = mix(vec3(0.05, 0.02, 0.08), u_primary, 0.35 + texture(iChannel0, vec2(snapped.x, 0.25)).r * 0.5);

    float luma = dot(color, vec3(0.299, 0.587, 0.114));
    float sat = max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);

    vec3 shadowCol = vec3(0.04, 0.02, 0.07);
    vec3 midCol = vec3(0.22, 0.1, 0.34);
    vec3 panelCol = vec3(0.42, 0.24, 0.58);
    vec3 textCol = vec3(0.72, 0.62, 0.86);
    vec3 goldCol = vec3(0.95, 0.78, 0.2);

    vec3 graded = mix(shadowCol, midCol, smoothstep(0.0, 0.32, luma));
    graded = mix(graded, panelCol, smoothstep(0.22, 0.58, luma));
    graded = mix(graded, textCol, smoothstep(0.45, 0.78, luma));
    graded = mix(graded, goldCol, smoothstep(0.72, 0.98, luma));

    float coloredText = smoothstep(0.1, 0.42, sat) * smoothstep(0.18, 0.95, luma);
    color = mix(graded, color, coloredText * 0.72);

    float ql = floor(luma * 5.0 + 0.0001) / 4.0;
    color = mix(color, color * (ql / max(luma, 0.001)), 0.5);

    vec2 cell = fract(uv * blocks);
    float grid = smoothstep(0.94, 1.0, max(cell.x, cell.y));
    color *= 1.0 - grid * 0.06;

    fragColor = vec4(color * u_fade, 1.0);
}
