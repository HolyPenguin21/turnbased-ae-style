using System;

namespace Game.UI
{
    // Normalized image coordinates, origin at top left. Matches the approved second preview.
    // Sampling absolute time avoids particle simulation, per-frame allocations and accumulated drift.
    public static class MainMenuWindMotion
    {
        public const int LayerCount = 6;

        public readonly struct Sample
        {
            public readonly float X, Y, Width, Height, Alpha, Angle;
            public readonly int Tile;
            public Sample(float x, float y, float width, float height, float alpha, int tile, float angle = 0f)
            { X = x; Y = y; Width = width; Height = height; Alpha = alpha; Tile = tile; Angle = angle; }
        }

        private readonly struct Layer
        {
            public readonly double Y, Speed, Width, Height, Alpha, Gust;
            public readonly int Count, Seed;
            public Layer(double y, double speed, double width, double height, int count, double alpha, double gust, int seed)
            { Y = y; Speed = speed; Width = width; Height = height; Count = count; Alpha = alpha; Gust = gust; Seed = seed; }
        }

        private static readonly Layer[] Layers =
        {
            new Layer(.573, .045, .46, .075, 3, .45, 0, 0),
            new Layer(.625, .055, .40, .085, 3, .52, 0, 3),
            new Layer(.695, .095, .38, .14, 3, .76, .22, 6),
            new Layer(.77, .12, .45, .14, 3, .60, .28, 9),
            new Layer(.866, .22, .48, .17, 2, .72, .26, 12),
            new Layer(.951, .27, .58, .13, 2, .48, .25, 15)
        };

        public static int PuffCount(int layer) => Layers[layer].Count;

        public static Sample SampleDust(double seconds, int layer, int puff, bool trail)
        {
            Layer l = Layers[layer];
            double phase = Repeat(seconds * l.Speed / 1.8 + (double)puff / l.Count + l.Seed * .137, 1);
            double local = seconds * 1.4 + puff * 2.31 + l.Seed;
            double gust = Math.Pow(.5 + .5 * Math.Sin(seconds * 1.05 - .7), 2);
            double life = Math.Pow(Math.Max(0, Math.Sin(Math.PI * phase)), .65);
            double width = l.Width * (.88 + .20 * Math.Sin(local * .53));
            double height = l.Height * (.9 + .18 * Math.Sin(local));
            double x = phase * 1.8 - .4;
            double y = l.Y + Math.Sin(local * .7) * l.Height * .13;
            double alpha = (l.Alpha + l.Gust * gust) * life;
            int tile = (l.Seed + puff) % 3;
            double stretchSpeed = 1.4;
            if (trail)
            {
                x -= l.Width * .24; y = l.Y + l.Height * .12;
                width = l.Width * .8; height = l.Height * .42; alpha *= .48;
                tile = (tile + 1) % 3; stretchSpeed = 2.1;
            }
            width *= 1 + .09 * Math.Sin(seconds * stretchSpeed + l.Seed + puff + (trail ? 1 : 0));
            return new Sample((float)x, (float)y, (float)width, (float)height, (float)alpha, tile);
        }

        public static Sample SampleDebris(double seconds)
        {
            double phase = Repeat(seconds + 1, 7) / 2.2;
            if (phase >= 1) return new Sample(0, 0, .014f, .012f, 0, 3);
            double flutter = .45 + .35 * Math.Cos(seconds * 12);
            return new Sample((float)(-.1 + phase * 1.23), (float)(.94 - .05 * Math.Sin(phase * 5)),
                .014f, (float)(.022 * flutter), (float)(.85 * Math.Sin(Math.PI * phase)), 3,
                (float)(phase * 9 + .4 * Math.Sin(seconds * 12)));
        }

        private static double Repeat(double value, double length) => value - Math.Floor(value / length) * length;
    }
}
