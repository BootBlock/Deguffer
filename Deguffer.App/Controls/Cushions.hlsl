// The cushions of a treemap, shaded on the GPU: one instance of a four-cornered strip per quad, in the
// order the quads are drawn, so a later quad covers an earlier one where they overlap. The GPU keeps
// the order of the primitives of one draw, which is what makes this the same picture as the
// rasteriser's.
//
// The light is CushionShading's, and has to stay it: ambient plus Lambertian diffuse from van Wijk's
// light, the same constants, the same flat shapes. A sheen is GradientShading's, in its whole numbers.
// The finish is ShapeFinish's, decided pixel by pixel by the rule TileRasteriser decides it by, with
// its colours mixed in the same whole numbers out of 256. CushionRenderer's test compares this picture
// with the rasteriser's, pixel by pixel. The pointer's light (PointerLight) is added to it, and adds
// nothing past its reach.

cbuffer Canvas : register(b0)
{
    // The canvas's size in pixels, which is where the viewport puts it.
    float2 size;

    // The canvas point under the pointer's light.
    float2 lightAt;

    // The part of the canvas shaded: left, top, right, bottom. Nothing outside it is touched.
    float4 region;

    // How far the pointer's light reaches, in pixels; how much it adds facing it straight on, zero for
    // no light; and how high above the canvas it is, in pixels.
    float reach;
    float strength;
    float height;

    // How many pixels wide the line inside each quad's sides is drawn; zero for none.
    float lineWidth;

    // The line's tone: red, green and blue from 0 to 255; and the shading, as MapShading numbers it: 0
    // for cushions, 1 for a bevel, 2 for flat, 3 for a sheen.
    float4 tone;

    // How far a block's line and a folder's are mixed towards the tone, out of 256; how many pixels
    // wide the bevel is; and how far its lit side is mixed towards white, out of 256.
    float4 finish;

    // How far the bevel's shaded side is mixed towards black, out of 256; how far a sheen's light falls
    // from a quad's top row to its middle, out of 256 (GradientShading.SlopeAt); and two values nothing
    // reads.
    float4 bevel;
};

struct Quad
{
    int4 edges : EDGES;      // left, top, right, bottom: the columns and rows covered, the far ones excluded
    float ridge : RIDGE;     // zero for a flat shape
    uint4 colour : COLOUR;   // red, green, blue, and one for a folder drawn round what it holds
    uint corner : SV_VertexID;
};

struct Pixel
{
    float4 position : SV_Position;
    nointerpolation float4 edges : EDGES;
    nointerpolation float4 shade : SHADE;  // ridge, red, green, blue
    nointerpolation uint framed : FRAMED;
};

Pixel vs(Quad quad)
{
    Pixel pixel;
    float4 edges = float4(quad.edges);
    // Cut to the region. The edges stay whole, so the cushion is still measured across the whole quad.
    float2 at = clamp(lerp(edges.xy, edges.zw, float2(quad.corner & 1, quad.corner >> 1)), region.xy, region.zw);

    pixel.position = float4((at.x / size.x * 2) - 1, 1 - (at.y / size.y * 2), 0, 1);
    pixel.edges = edges;
    pixel.shade = float4(quad.ridge, float3(quad.colour.rgb));
    pixel.framed = quad.colour.a;
    return pixel;
}

// ShapeFinish.Mix: each channel mixed amount parts in 256 towards the other, cut to a whole level.
uint3 mix256(uint3 colour, uint3 toward, uint amount)
{
    return ((colour * (256 - amount)) + (toward * amount)) >> 8;
}

// The pointer's light on a point whose surface tilts by nx and ny: a point above the canvas, so a slope
// facing it catches more of it, fading to nothing at its reach.
float pointer(float2 at, float nx, float ny)
{
    if (strength <= 0)
    {
        return 0;
    }

    float3 normal = normalize(float3(nx, ny, 1));
    float2 apart = lightAt - (at + 0.5);
    float fade = 1 - saturate(dot(apart, apart) / (reach * reach));

    return strength * saturate(dot(normal, normalize(float3(apart, height)))) * fade * fade;
}

float4 ps(Pixel pixel) : SV_Target
{
    // The pixel's own column and row, as the rasteriser counts them: the position is its centre.
    float2 at = floor(pixel.position.xy);
    uint3 colour = uint3(pixel.shade.yzw);

    // How far in from each side, in whole pixels. A side off the canvas is far away, so nothing is
    // drawn along it.
    float fromLeft = at.x - pixel.edges.x;
    float fromTop = at.y - pixel.edges.y;
    float fromRight = pixel.edges.z - 1 - at.x;
    float fromBottom = pixel.edges.w - 1 - at.y;
    float nearest = min(min(fromLeft, fromRight), min(fromTop, fromBottom));

    uint amount = pixel.framed != 0 ? (uint)finish.y : (uint)finish.x;
    float drawn = amount > 0 ? lineWidth : 0;
    bool bevelled = tone.w == 1 && pixel.shade.x > 0;
    float band = drawn + (bevelled ? finish.z : 0);

    // The line, flat and unlit: nothing of the light or the pointer's light.
    if (nearest < drawn)
    {
        return float4(float3(mix256(colour, uint3(tone.rgb), amount)) / 255, 1);
    }

    // Flat inside, or the bevel along the edges: the lit side is the top or the left only where it is
    // nearer than both the bottom and the right, as TileRasteriser decides it.
    if (tone.w == 1 || tone.w == 2)
    {
        uint3 flat = colour;

        if (nearest < band)
        {
            flat = min(fromLeft, fromTop) < min(fromRight, fromBottom)
                ? mix256(colour, uint3(255, 255, 255), (uint)finish.w)
                : mix256(colour, uint3(0, 0, 0), (uint)bevel.x);
        }

        return float4(floor(clamp(float3(flat) * (1 + pointer(at, 0, 0)), 0, 255)) / 255, 1);
    }

    float spanX = pixel.edges.z - pixel.edges.x;
    float spanY = pixel.edges.w - pixel.edges.y;
    float u = spanX <= 1 ? 0.5 : (at.x - pixel.edges.x) / (spanX - 1);
    float v = spanY <= 1 ? 0.5 : (at.y - pixel.edges.y) / (spanY - 1);
    float ridge = pixel.shade.x;

    // A sheen: GradientShading.LightAt down the quad, in whole numbers out of 256, on a surface that
    // faces straight up, so the pointer's light falls on it as it does on a flat one. A quad that does
    // not rise has none. Without the pointer's light, the product is whole and the division by 256 is
    // exact, so the floor is GradientShading.Lit's shift.
    if (tone.w == 3)
    {
        uint slope = ridge > 0 ? (uint)bevel.y : 0;
        uint length = spanY <= 1 ? 0 : (uint)(spanY - 1);
        uint sheen = length == 0 ? 256 : 256 - slope + ((2 * slope * (length - (uint)fromTop)) / length);

        return float4(floor(clamp(float3(colour) * ((sheen / 256.0) + pointer(at, 0, 0)), 0, 255)) / 255, 1);
    }

    float nx = ridge * ((2 * u) - 1);
    float ny = ridge * ((2 * v) - 1);

    // CushionShading.LightAt, with its light above and to the left.
    float cosine = ((nx * -0.09759) + (ny * -0.19518) + 0.9759) / sqrt((nx * nx) + (ny * ny) + 1);
    float light = 0.13 + (0.87 * saturate(cosine)) + pointer(at, nx, ny);

    // Truncated to a byte as CushionShading.Write truncates, so the two agree but for rounding at the
    // last bit of a float against a double.
    return float4(floor(clamp(pixel.shade.yzw * light, 0, 255)) / 255, 1);
}
