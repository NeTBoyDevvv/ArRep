//UNITY_SHADER_NO_UPGRADE
/// Alternative to Hash_Tchou_2_2_float
float2 MyHash_2_2_float(uint2 p)
{
    // integer dot products with "random" large primes
    uint n = p.x * 0x27d4eb2d + p.y * 0x165667b1;

    // mix bits
    n = (n ^ (n >> 15)) * 0x85ebca6b;
    n = (n ^ (n >> 13)) * 0xc2b2ae35;
    n = n ^ (n >> 16);

    // expand into two components
    // use two different "bit scramblers" for decorrelation
    uint n1 = n * 0x27d4eb2d;
    uint n2 = n * 0x165667b1;

    // normalize to [0,1)
    return float2(
        (n1 & 0x00FFFFFF) / 16777216.0,
        (n2 & 0x00FFFFFF) / 16777216.0
    );
}

#ifndef MYHLSLINCLUDE_INCLUDED
#define MYHLSLINCLUDE_INCLUDED
float2 voronoi_noise_randomVector (float2 UV, float offset)
{
    UV = MyHash_2_2_float(UV);
    return float2(sin(UV.y * offset) , 
                  cos(UV.x * offset) * 0.5 + 0.5);
}

void Voronoi_float( float2 UV, float AngleOffset, float CellDensity,
                    out float Out, out float2 Cells, out float2 Pos, out float DistToEdge)
{
    float2 g = floor(UV * CellDensity);   // integer cell ID
    float2 f = frac(UV * CellDensity);    // local coordinate within cell
    float md = 8.0; // minimum distance
    float smd = 8.0; // second minimum distance

    Pos = 0; // init

    for(int y=-1; y<=1; y++)
    {
        for(int x=-1; x<=1; x++)
        {
            float2 lattice = float2(x,y);
            float2 offset = voronoi_noise_randomVector(lattice + g, AngleOffset);

            // candidate feature point position (in local cell space)
            float2 candidate = lattice + offset;

            float d = distance(candidate, f);
            if(d < md)
            {
                md = d;
                Out = d;
                Cells = offset;
                // return the actual centroid position (cell-space, 0..1 range)
                Pos = (g + candidate) / CellDensity;
            }
            if (d < smd && d > md)
            {
                smd = d;
            }
        }
    }
    DistToEdge = smd - md;
}

#endif //MYHLSLINCLUDE_INCLUDED


