// Ripple's water. A real height field, not canned rings:
//
//   State texture  red = surface height, green = how fast it is moving
//   PS_Inject      something touches the water (the cursor, a click, a droplet)
//   PS_Step        one step of the wave equation; waves spread, disperse, bounce, fade
//   PS_Surface     turns the height into slopes and curvature for drawing
//   PS_Draw        looks through the water at the wallpaper
//
// When the water is flat PS_Draw returns the wallpaper exactly, so a calm screen
// is pixel-for-pixel the picture underneath.

Texture2D<float2> State : register(t0);
Texture2D<float4> Surface : register(t0);
Texture2D<float4> Wallpaper : register(t1);
SamplerState SurfaceSampler : register(s0);
SamplerState WallpaperSampler : register(s1);

cbuffer StepConstants : register(b0)
{
    float WaveSpeedSquared;   // how stiff the water is (wave speed in cells per step, squared)
    float VelocityKeep;       // energy kept each step; the "damping" setting
    float Viscosity;          // fine ripples die faster than long swells, as in real water
    float HeightKeep;         // the level settles back to zero
    float Dispersion;         // long waves outrun short ones
    int MaxX;
    int MaxY;
    float StepPad;
};

struct Stamp
{
    float4 Segment;           // from x, y to x, y, in cells
    float4 Shape;             // radius in cells, push on height, push on speed, unused
};

cbuffer InjectConstants : register(b0)
{
    int StampCount;
    float3 InjectPad;
    Stamp Stamps[16];
};

cbuffer DrawConstants : register(b0)
{
    float2 ScreenSize;        // physical pixels
    float2 SimSize;           // cells
    float Depth;              // how far the wallpaper sits under the surface, in pixels
    float ColourSplit;        // how differently the colours bend
    float Light;              // overall strength of highlights
    float CausticStrength;    // bright lines of focused light on the "floor"
};

float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Pushes are zero-mean: the water pushed down in the middle rises around it, so a
// touch adds no volume and the surface always has somewhere level to return to.
float2 PS_Inject(float4 pos : SV_Position) : SV_Target
{
    float2 s = State.Load(int3(int2(pos.xy), 0));
    float2 p = pos.xy;
    for (int i = 0; i < StampCount; i++)
    {
        float2 a = Stamps[i].Segment.xy;
        float2 b = Stamps[i].Segment.zw;
        float2 ab = b - a;
        float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 0.0001));
        float2 d = (p - (a + ab * t)) / Stamps[i].Shape.x;
        float x2 = dot(d, d);
        if (x2 < 9.0)
        {
            float f = (1.0 - x2) * exp(-x2);
            s.x += Stamps[i].Shape.y * f;
            s.y += Stamps[i].Shape.z * f;
        }
    }
    return s;
}

// Reading past the edge returns the edge's own value. That is exactly a solid wall
// to the wave equation, so waves bounce off all four sides.
float2 Cell(int2 p)
{
    return State.Load(int3(clamp(p, int2(0, 0), int2(MaxX, MaxY)), 0));
}

float2 PS_Step(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float2 c = Cell(p);
    float2 l = Cell(p + int2(-1, 0));
    float2 r = Cell(p + int2(1, 0));
    float2 u = Cell(p + int2(0, -1));
    float2 d = Cell(p + int2(0, 1));

    float diagonals = Cell(p + int2(-1, -1)).x + Cell(p + int2(1, -1)).x
                    + Cell(p + int2(-1, 1)).x + Cell(p + int2(1, 1)).x;

    // The nine-point form keeps circles round instead of drifting into diamonds.
    float nearCurve = (4.0 * (l.x + r.x + u.x + d.x) + diagonals - 20.0 * c.x) / 6.0;

    // The wide form sees long waves the same but short ones as flatter, so short
    // waves travel slower. Real water does this too: a train of fine ripples
    // trails behind the first long swell. That is the "dispersing" look.
    float wideCurve = (Cell(p + int2(-2, 0)).x + Cell(p + int2(2, 0)).x
                     + Cell(p + int2(0, -2)).x + Cell(p + int2(0, 2)).x - 4.0 * c.x) / 4.0;

    float curve = lerp(nearCurve, wideCurve, Dispersion);
    float viscous = l.y + r.y + u.y + d.y - 4.0 * c.y;

    float speed = (c.y + WaveSpeedSquared * curve + Viscosity * viscous) * VelocityKeep;
    float height = (c.x + speed) * HeightKeep;
    return float2(height, speed);
}

float4 PS_Surface(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float c = Cell(p).x;
    float l = Cell(p + int2(-1, 0)).x;
    float r = Cell(p + int2(1, 0)).x;
    float u = Cell(p + int2(0, -1)).x;
    float d = Cell(p + int2(0, 1)).x;
    return float4((r - l) * 0.5, (d - u) * 0.5, c, l + r + u + d - 4.0 * c);
}

// A smooth four-tap read of the coarse surface: without this the water shows its
// grid as faint diamonds in the highlights.
float4 SampleSmooth(float2 uv)
{
    float2 st = uv * SimSize - 0.5;
    float2 i = floor(st);
    float2 f = st - i;
    float2 f2 = f * f;
    float2 f3 = f2 * f;
    float2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
    float2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
    float2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
    float2 w3 = f3 / 6.0;
    float2 g0 = w0 + w1;
    float2 g1 = w2 + w3;
    float2 p0 = (i - 0.5 + w1 / g0 + 0.5) / SimSize;
    float2 p1 = (i + 1.5 + w3 / g1 + 0.5) / SimSize;
    return g0.y * (g0.x * Surface.SampleLevel(SurfaceSampler, float2(p0.x, p0.y), 0)
                 + g1.x * Surface.SampleLevel(SurfaceSampler, float2(p1.x, p0.y), 0))
         + g1.y * (g0.x * Surface.SampleLevel(SurfaceSampler, float2(p0.x, p1.y), 0)
                 + g1.x * Surface.SampleLevel(SurfaceSampler, float2(p1.x, p1.y), 0));
}

float4 PS_Draw(float4 pos : SV_Position) : SV_Target
{
    float2 uv = pos.xy / ScreenSize;
    float4 s = SampleSmooth(uv);
    float2 slope = s.xy;
    float curve = s.w;

    // Refraction: each crest is a little lens over the picture. Blue bends slightly
    // more than red, as it does in real water, which fringes only the steep parts.
    float2 bend = slope * Depth / ScreenSize;
    float3 colour;
    colour.r = Wallpaper.SampleLevel(WallpaperSampler, uv + bend * (1.0 - ColourSplit), 0).r;
    colour.g = Wallpaper.SampleLevel(WallpaperSampler, uv + bend, 0).g;
    colour.b = Wallpaper.SampleLevel(WallpaperSampler, uv + bend * (1.0 + ColourSplit), 0).b;

    // Caustics: light focused by the surface, bright under troughs and dim under crests.
    float focus = clamp(curve * CausticStrength, -0.22, 0.75);
    colour *= 1.0 + focus;

    // The surface facing a soft light from the top left, then facing away from it.
    float3 normal = normalize(float3(-slope * 3.2, 1.0));
    float3 sun = normalize(float3(-0.42, -0.62, 0.66));
    float facing = dot(normal.xy, sun.xy);
    colour *= 1.0 + facing * 0.55 * Light;

    // Sky glancing off steep faces, and the sharp glint of the sun on the crests.
    // Everything here is measured against flat water so calm water adds nothing.
    float3 halfway = normalize(sun + float3(0.0, 0.0, 1.0));
    float along = saturate(dot(normal, halfway));
    float sheen = max(pow(along, 26.0) - pow(halfway.z, 26.0), 0.0);
    float glint = max(pow(along, 340.0) - pow(halfway.z, 340.0), 0.0);
    float fresnel = pow(1.0 - saturate(normal.z), 3.0);
    float3 sky = float3(0.78, 0.88, 1.0);
    colour += sky * (sheen * 0.26 + fresnel * 1.5) * Light;
    colour += float3(1.0, 0.98, 0.94) * glint * 1.15 * Light;

    return float4(saturate(colour), 1.0);
}
