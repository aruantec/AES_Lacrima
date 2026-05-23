#!/usr/bin/env python3
"""Convert AES_Lacrima HLSL post-process shaders to RetroArch-style GLSL."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HLSL_DIR = ROOT / "AES_Lacrima" / "Shaders" / "hlsl"
OUT_DIRS = [
    ROOT / "AES_Lacrima" / "Shaders" / "glsl",
    ROOT / "AES_Emulation" / "Windows" / "shaders" / "glsl",
]

VERTEX = """#ifdef VERTEX

layout(location = 0) in vec2 VertexCoord;
layout(location = 1) in vec2 TexCoord;
out vec2 vTex;

void main()
{
    vTex = TexCoord;
    gl_Position = vec4(VertexCoord, 0.0, 1.0);
}

#endif

#ifdef FRAGMENT

uniform sampler2D Texture;
uniform float FrameCount;
uniform float FrameDirection;
uniform vec2 TextureSize;
uniform vec2 InputSize;
uniform vec2 OutputSize;
uniform float uBrightness;
uniform float uSaturation;
uniform vec4 uColorTint;

in vec2 vTex;
out vec4 fragColor;

#define timeSeconds (FrameCount / 60.0)
#define brightness uBrightness
#define saturation uSaturation
#define tint uColorTint
#define sourceWidth TextureSize.x
#define sourceHeight TextureSize.y
#define outputWidth OutputSize.x
#define outputHeight OutputSize.y
#define sourceIsSrgb 1.0

"""

FOOTER = """
#endif
"""


def strip_header(source: str) -> str:
    lines = source.splitlines()
    out: list[str] = []
    skip = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("cbuffer"):
            skip = True
            continue
        if skip:
            if stripped.startswith("};"):
                skip = False
            continue
        if stripped.startswith("Texture2D") or stripped.startswith("SamplerState"):
            continue
        if stripped.startswith("struct PSIn"):
            skip = True
            continue
        if skip and stripped.startswith("};"):
            skip = False
            continue
        if skip:
            continue
        out.append(line)
    return "\n".join(out)


def convert_body(source: str) -> str:
    source = re.sub(
        r"float4\s+main\s*\(\s*PSIn\s+input\s*\)\s*:\s*SV_TARGET\s*\{",
        "void main()\n{",
        source,
        flags=re.IGNORECASE,
    )
    source = source.replace("src.Sample(samp,", "texture(Texture,")
    source = source.replace("input.uv", "vTex")
    source = source.replace("input.pos.y", "gl_FragCoord.y")
    source = source.replace("input.pos.x", "gl_FragCoord.x")
    source = re.sub(r"\blerp\s*\(", "mix(", source)
    source = re.sub(r"\bfrac\s*\(", "fract(", source)
    source = re.sub(r"\bfmod\s*\(", "mod(", source)
    source = re.sub(r"\bfloat2\b", "vec2", source)
    source = re.sub(r"\bfloat3\b", "vec3", source)
    source = re.sub(r"\bfloat4\b", "vec4", source)
    source = re.sub(r"\bint2\b", "ivec2", source)
    source = re.sub(r"\bint\b", "int", source)

    def saturate_repl(match: re.Match[str]) -> str:
        expr = match.group(1)
        return f"clamp({expr}, 0.0, 1.0)"

    source = re.sub(r"\bsaturate\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)", saturate_repl, source)

    source = re.sub(
        r"return\s+vec4\s*\(\s*clamp\(([^;]+),\s*0\.0,\s*1\.0\)\s*,\s*tint\.a\s*\)\s*;",
        r"fragColor = vec4(clamp(\1, 0.0, 1.0), tint.a);",
        source,
    )
    source = re.sub(
        r"return\s+vec4\s*\(([^;]+),\s*tint\.a\s*\)\s*;",
        r"fragColor = vec4(\1, tint.a);",
        source,
    )
    source = re.sub(
        r"return\s+vec4\s*\(([^;]+)\)\s*;",
        r"fragColor = vec4(\1);",
        source,
    )
    return source.strip() + "\n"


def convert_file(hlsl_path: Path) -> str:
    body = convert_body(strip_header(hlsl_path.read_text(encoding="utf-8")))
    return VERTEX + body + FOOTER


def main() -> None:
    hlsl_files = sorted(HLSL_DIR.glob("*.hlsl"))
    if not hlsl_files:
        raise SystemExit(f"No HLSL files found in {HLSL_DIR}")

    for out_dir in OUT_DIRS:
        out_dir.mkdir(parents=True, exist_ok=True)
        for old in out_dir.glob("*.glsl"):
            old.unlink()

    for hlsl_path in hlsl_files:
        glsl = convert_file(hlsl_path)
        for out_dir in OUT_DIRS:
            target = out_dir / (hlsl_path.stem + ".glsl")
            target.write_text(glsl, encoding="utf-8", newline="\n")
            print(f"Wrote {target.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
