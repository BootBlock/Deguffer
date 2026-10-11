// The cushions of a treemap, shaded on the GPU: one instance of a four-cornered strip per quad, in the
// order the quads are drawn, so a later quad covers an earlier one where they overlap. The GPU keeps
// the order of the primitives of one draw, which is what makes this the same picture as the
// rasteriser's.
//
// The light is CushionShading's, and has to stay it: ambient plus Lambertian diffuse from van Wijk's
// light, the same constants, the same flat shapes. CushionRenderer's test compares this picture with
// the rasteriser's, pixel by pixel.

cbuffer Canvas : register(b0)
{
    // The canvas's size in pixels, which is where the viewport puts it.
    float2 size;
    float2 unused;
};

struct Quad
{
    int4 edges : EDGES;      // left, top, right, bottom: the columns and rows covered, the far ones excluded
    float ridge : RIDGE;     // zero for a flat shape
    uint4 colour : COLOUR;   // red, green, blue, and a byte nothing reads
    uint corner : SV_VertexID;
};

struct Pixel
{
    float4 position : SV_Position;
    nointerpolation float4 edges : EDGES;
    nointerpolation float4 shade : SHADE;  // ridge, red, green, blue
};

Pixel vs(Quad quad)
{
    Pixel pixel;
    float4 edges = float4(quad.edges);
    float2 at = lerp(edges.xy, edges.zw, float2(quad.corner & 1, quad.corner >> 1));

    pixel.position = float4((at.x / size.x * 2) - 1, 1 - (at.y / size.y * 2), 0, 1);
    pixel.edges = edges;
    pixel.shade = float4(quad.ridge, float3(quad.colour.rgb));
    return pixel;
}

float4 ps(Pixel pixel) : SV_Target
{
    // The pixel's own column and row, as the rasteriser counts them: the position is its centre.
    float2 at = floor(pixel.position.xy);
    float spanX = pixel.edges.z - pixel.edges.x;
    float spanY = pixel.edges.w - pixel.edges.y;
    float u = spanX <= 1 ? 0.5 : (at.x - pixel.edges.x) / (spanX - 1);
    float v = spanY <= 1 ? 0.5 : (at.y - pixel.edges.y) / (spanY - 1);
    float ridge = pixel.shade.x;
    float nx = ridge * ((2 * u) - 1);
    float ny = ridge * ((2 * v) - 1);

    // CushionShading.LightAt, with its light above and to the left.
    float cosine = ((nx * -0.09759) + (ny * -0.19518) + 0.9759) / sqrt((nx * nx) + (ny * ny) + 1);
    float light = 0.13 + (0.87 * saturate(cosine));

    // Truncated to a byte as CushionShading.Write truncates, so the two agree but for rounding at the
    // last bit of a float against a double.
    return float4(floor(clamp(pixel.shade.yzw * light, 0, 255)) / 255, 1);
}
