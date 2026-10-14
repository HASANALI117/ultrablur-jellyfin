namespace UltraBlur.Plugin.Algorithm
{
    /// <summary>
    /// Plex Media Server UltraBlur 4-corner color extraction, transliterated
    /// line-for-line from the bit-exact C++ port (pms/cpp-linux/src), which is
    /// itself reconstructed from the PMS 1.42.2 / 1.43.4 binary (identical
    /// UltraBlur code in both; see pms/RE/diff_1.43). Every float/double
    /// expression keeps the C++ operand types and evaluation order: changing
    /// either breaks bit-exactness against Plex.
    /// </summary>
    internal static class UltraBlurCore
    {
        private const int HistBins = 32 * 32 * 32;

        /// <summary>
        /// Extracts the four corner colors from an RGBA8888 image (alpha ignored;
        /// PMS decodes with IMREAD_COLOR so alpha is always 255).
        /// </summary>
        /// <returns>0xRRGGBB for TL, TR, BR, BL; null when a corner has no usable pixels.</returns>
        public static (uint Tl, uint Tr, uint Br, uint Bl)? Extract(RgbaImage img)
        {
            int w2 = img.Width / 2, h2 = img.Height / 2;

            // Per-corner reference chain (FUN_011ae916, verified via Frida):
            //   TL ref = zero cluster (ordinal 0 -> distance branch gated off)
            //   TR ref = BL ref = TL pick; BR ref = BL pick.
            // PMS leaks a non-zero stack ordinal into the ref; force 1.
            var zero = default(Cluster);
            var tl = ExtractQuadrant(img, 0, 0, w2, h2, zero, 0.2f);
            if (tl == null)
            {
                return null;
            }

            var tlRef = tl.Value.Picked with { Ordinal = 1 };
            var tr = ExtractQuadrant(img, w2, 0, w2, h2, tlRef, 0.4f);
            var bl = ExtractQuadrant(img, 0, h2, w2, h2, tlRef, 0.4f);
            if (bl == null)
            {
                return null;
            }

            var blRef = bl.Value.Picked with { Ordinal = 1 };
            var br = ExtractQuadrant(img, w2, h2, w2, h2, blRef, 0.4f);
            if (tr == null || br == null)
            {
                return null;
            }

            return (tl.Value.Rgb, tr.Value.Rgb, br.Value.Rgb, bl.Value.Rgb);
        }

        // ================================================================
        // Quadrant extractor (FUN_011aeeb7 / quadrant.cpp)
        // ================================================================

        private static (Cluster Picked, uint Rgb)? ExtractQuadrant(
            RgbaImage img, int x0, int y0, int w, int h, Cluster reference, float vCeil)
        {
            // Histogram: every 5th pixel of the quadrant in row-major order,
            // skipping near-white (all channels >= 0xfb). Alpha is always 255.
            var hist = new uint[HistBins];
            byte[] px = img.Pixels;
            int n = w * h;
            bool anyKept = false;
            int min0 = 31, min1 = 31, min2 = 31, max0 = 0, max1 = 0, max2 = 0;
            for (int i = 0; i < n; i += 5)
            {
                int p = (((y0 + (i / w)) * img.Width) + x0 + (i % w)) * 4;
                byte r = px[p], g = px[p + 1], b = px[p + 2];
                if (r >= 0xfb && g >= 0xfb && b >= 0xfb)
                {
                    continue;
                }

                int br = r >> 3, bg = g >> 3, bb = b >> 3;
                hist[(br << 10) | (bg << 5) | bb] += 1;
                if (br < min0)
                {
                    min0 = br;
                }

                if (bg < min1)
                {
                    min1 = bg;
                }

                if (bb < min2)
                {
                    min2 = bb;
                }

                if (br > max0)
                {
                    max0 = br;
                }

                if (bg > max1)
                {
                    max1 = bg;
                }

                if (bb > max2)
                {
                    max2 = bb;
                }

                anyKept = true;
            }

            if (!anyKept)
            {
                return null;
            }

            var seed = new Box(min0, min1, min2, max0, max1, max2);
            ulong total = 0, unique = 0;
            for (int r = seed.Min0; r <= seed.Max0; ++r)
            {
                for (int g = seed.Min1; g <= seed.Max1; ++g)
                {
                    for (int b = seed.Min2; b <= seed.Max2; ++b)
                    {
                        uint c = hist[(r << 10) | (g << 5) | b];
                        if (c != 0)
                        {
                            total += c;
                            unique += 1;
                        }
                    }
                }
            }

            seed.Total = total;
            seed.Unique = unique;

            // Pass 1: median-cut to 8 by total weight (FUN_011b095c).
            var boxes = MedianCut(hist, seed, 8);

            // Pass 2: PMS calls FUN_011b095c again with comparator FUN_011b1e29
            // (box volume * weight), adding one more split -> 9 clusters.
            static ulong Vol(in Box b) =>
                (ulong)(b.Max0 - b.Min0 + 1) * (ulong)(b.Max1 - b.Min1 + 1) * (ulong)(b.Max2 - b.Min2 + 1);
            static bool ByVolTotal(Box a, Box b) => Vol(a) * a.Total < Vol(b) * b.Total;

            LibCxx.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes), ByVolTotal);
            var top = boxes[^1];
            boxes.RemoveAt(boxes.Count - 1);
            if (SplitBox(hist, top, out var left, out var right))
            {
                boxes.Add(left);
                boxes.Add(right);
            }
            else
            {
                boxes.Add(top);
            }

            LibCxx.Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(boxes), ByVolTotal);

            // Centroids, reversed (FUN_011aeeb7 011af910), promoted to clusters.
            var clusters = new Cluster[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
            {
                var c8 = Centroid(hist, boxes[boxes.Count - 1 - i]);
                clusters[i] = MakeCluster(c8.R, c8.G, c8.B, (ulong)i);
            }

            // Sort descending by rank vs the fixed reference; pick element[0].
            ulong liveCount = (ulong)clusters.Length;
            LibCxx.Sort<Cluster>(clusters, (a, b) =>
            {
                float rA = RankCandidate(a, a.Ordinal, liveCount, reference);
                float rB = RankCandidate(b, b.Ordinal, liveCount, reference);
                return rA > rB;
            });

            var picked = clusters[0];
            return (picked, RenderCluster(picked, vCeil));
        }

        private static Cluster MakeCluster(byte r8, byte g8, byte b8, ulong ordinal)
        {
            var lin = new Vec3(SrgbByteToLinear(r8), SrgbByteToLinear(g8), SrgbByteToLinear(b8));
            var lab = LinearSrgbToOklab(lin);
            float chroma = MathF.Sqrt((lab.Y * lab.Y) + (lab.Z * lab.Z));

            float aU = 0.0f, bU = 0.0f;
            if (chroma > 0.0f)
            {
                aU = lab.Y / chroma;
                bU = lab.Z / chroma;
            }

            // PMS hue = atan2(-b, -a) / (2*pi) + 0.5.
            float hueRad = MuslMath.Atan2f(-lab.Z, -lab.Y);
            float hNorm = (hueRad / (2.0f * 3.14159265358979323846f)) + 0.5f;

            var cusp = GamutHelper(lab.X, aU, bU);
            float pmsS = ScorePms(chroma, cusp);
            float pmsV = Toe(lab.X);

            var distLab = GammaSrgbToOklab(r8, g8, b8);
            return new Cluster
            {
                R = r8,
                G = g8,
                B = b8,
                Hue = hNorm,
                PmsS = pmsS,
                PmsV = pmsV,
                LabL = distLab.X,
                LabA = distLab.Y,
                LabB = distLab.Z,
                Ordinal = ordinal,
            };
        }

        // Forward score curve (FUN_011aeeb7 011afce7..011afd6a).
        private static float ScorePms(float c, Cusp cusp)
        {
            if (cusp.C <= 0.0f || c <= 0.0f)
            {
                return 0.0f;
            }

            const float mul = 0.8f;
            if (cusp.C <= c)
            {
                float kh = 0.3125f * cusp.C * cusp.C / cusp.L;
                float diff = cusp.CMax - cusp.C;
                if (MathF.Abs(diff) < 1e-12f)
                {
                    return 1.0f;
                }

                return ((c - cusp.C) / (((1.0f - (kh / diff)) * (c - cusp.C)) + kh) * 0.2f) + mul;
            }

            float k = cusp.L * mul;
            return c / (((1.0f - (k / cusp.C)) * c) + k) * mul;
        }

        private static Vec3 GammaSrgbToOklab(byte r8, byte g8, byte b8)
        {
            float r = r8 / 255.0f;
            float g = g8 / 255.0f;
            float b = b8 / 255.0f;
            return LmsToOklab(
                (0.4122214708f * r) + (0.5363325363f * g) + (0.0514459929f * b),
                (0.2119034982f * r) + (0.6806995451f * g) + (0.1073969566f * b),
                (0.0883024619f * r) + (0.2817188376f * g) + (0.6299787005f * b));
        }

        // ================================================================
        // OKLab / toe / gamut (oklab.cpp; FUN_011b3738 = Björn get_Cs)
        // ================================================================

        private static float SrgbByteToLinear(byte v)
        {
            float x = v / 255.0f;
            if (x <= 0.04045f)
            {
                return x / 12.92f;
            }

            return MuslMath.Powf((x + 0.055f) / 1.055f, 2.4f);
        }

        private static Vec3 LinearSrgbToOklab(Vec3 lin) => LmsToOklab(
            (0.4122214708f * lin.X) + (0.5363325363f * lin.Y) + (0.0514459929f * lin.Z),
            (0.2119034982f * lin.X) + (0.6806995451f * lin.Y) + (0.1073969566f * lin.Z),
            (0.0883024619f * lin.X) + (0.2817188376f * lin.Y) + (0.6299787005f * lin.Z));

        private static Vec3 LmsToOklab(float l0, float m0, float s0)
        {
            float l = MuslMath.Cbrtf(l0);
            float m = MuslMath.Cbrtf(m0);
            float s = MuslMath.Cbrtf(s0);
            return new Vec3(
                (0.2104542553f * l) + (0.7936177850f * m) - (0.0040720468f * s),
                (1.9779984951f * l) - (2.4285922050f * m) + (0.4505937099f * s),
                (0.0259040371f * l) + (0.7827717662f * m) - (0.8086757660f * s));
        }

        private static Vec3 OklabToLinearSrgb(float labL, float labA, float labB)
        {
            float l0 = labL + (0.3963377774f * labA) + (0.2158037573f * labB);
            float m0 = labL - (0.1055613458f * labA) - (0.0638541728f * labB);
            float s0 = labL - (0.0894841775f * labA) - (1.2914855480f * labB);
            float l = l0 * l0 * l0;
            float m = m0 * m0 * m0;
            float s = s0 * s0 * s0;
            return new Vec3(
                (+4.0767416621f * l) - (3.3077115913f * m) + (0.2309699292f * s),
                (-1.2684380046f * l) + (2.6097574011f * m) - (0.3413193965f * s),
                (-0.0041960863f * l) - (0.7034186147f * m) + (1.7076147010f * s));
        }

        private const float ToeK1 = 0.206f;
        private const float ToeK2 = 0.03f;
        private const float ToeK3 = (1.0f + ToeK1) / (1.0f + ToeK2);

        private static float Toe(float l)
        {
            float t = (ToeK3 * l) - ToeK1;
            return 0.5f * (t + MathF.Sqrt((t * t) + (4.0f * ToeK2 * ToeK3 * l)));
        }

        private static float ToeInv(float x) => ((x * x) + (ToeK1 * x)) / (ToeK3 * (x + ToeK2));

        private static float ComputeMaxSaturation(float a, float b)
        {
            float k0, k1, k2, k3, k4, wl, wm, ws;
            if ((-1.88170328f * a) - (0.80936493f * b) > 1.0f)
            {
                k0 = +1.19086277f; k1 = +1.76576728f; k2 = +0.59662641f; k3 = +0.75515197f; k4 = +0.56771245f;
                wl = +4.0767416621f; wm = -3.3077115913f; ws = +0.2309699292f;
            }
            else if ((1.81444104f * a) - (1.19445276f * b) > 1.0f)
            {
                k0 = +0.73956515f; k1 = -0.45954404f; k2 = +0.08285427f; k3 = +0.12541070f; k4 = +0.14503204f;
                wl = -1.2684380046f; wm = +2.6097574011f; ws = -0.3413193965f;
            }
            else
            {
                k0 = +1.35733652f; k1 = -0.00915799f; k2 = -1.15130210f; k3 = -0.50559606f; k4 = +0.00692167f;
                wl = -0.0041960863f; wm = -0.7034186147f; ws = +1.7076147010f;
            }

            float sat = k0 + (k1 * a) + (k2 * b) + (k3 * a * a) + (k4 * a * b);

            float kl = (+0.3963377774f * a) + (0.2158037573f * b);
            float km = (-0.1055613458f * a) - (0.0638541728f * b);
            float ks = (-0.0894841775f * a) - (1.2914855480f * b);

            float l0 = 1.0f + (sat * kl);
            float m0 = 1.0f + (sat * km);
            float s0 = 1.0f + (sat * ks);
            float l = l0 * l0 * l0;
            float m = m0 * m0 * m0;
            float s = s0 * s0 * s0;
            float ldS = 3.0f * kl * l0 * l0;
            float mdS = 3.0f * km * m0 * m0;
            float sdS = 3.0f * ks * s0 * s0;
            float ldS2 = 6.0f * kl * kl * l0;
            float mdS2 = 6.0f * km * km * m0;
            float sdS2 = 6.0f * ks * ks * s0;
            float f = (wl * l) + (wm * m) + (ws * s);
            float f1 = (wl * ldS) + (wm * mdS) + (ws * sdS);
            float f2 = (wl * ldS2) + (wm * mdS2) + (ws * sdS2);
            sat = sat - (f * f1 / ((f1 * f1) - (0.5f * f * f2)));
            return sat;
        }

        private static Cusp FindCusp(float aUnit, float bUnit)
        {
            float sat = ComputeMaxSaturation(aUnit, bUnit);
            var rgb = OklabToLinearSrgb(1.0f, sat * aUnit, sat * bUnit);
            float l = MuslMath.Cbrtf(1.0f / Max(Max(rgb.X, rgb.Y), rgb.Z));
            float c = l * sat;
            return new Cusp(l, c, c);
        }

        // Björn find_gamut_intersection with (L1 = L, C1 = 1, L0 = L): the
        // disasm's section-5 SIMPLE/COMPLEX branch; result is XMM11 = C_max.
        private static float GamutIntersectionSelf(float a, float b, float l, Cusp cusp)
        {
            if (l <= cusp.L)
            {
                return cusp.C * l / cusp.L;
            }

            float t = cusp.C * (l - 1.0f) / (cusp.L - 1.0f);

            float kl = (+0.3963377774f * a) + (0.2158037573f * b);
            float km = (-0.1055613458f * a) - (0.0638541728f * b);
            float ks = (-0.0894841775f * a) - (1.2914855480f * b);
            float l0 = l + (t * kl);
            float m0 = l + (t * km);
            float s0 = l + (t * ks);
            float ll = l0 * l0 * l0, mm = m0 * m0 * m0, ss = s0 * s0 * s0;
            float ldt = 3.0f * kl * l0 * l0;
            float mdt = 3.0f * km * m0 * m0;
            float sdt = 3.0f * ks * s0 * s0;
            float ldt2 = 6.0f * kl * kl * l0;
            float mdt2 = 6.0f * km * km * m0;
            float sdt2 = 6.0f * ks * ks * s0;

            float r = (4.0767416621f * ll) - (3.3077115913f * mm) + (0.2309699292f * ss) - 1.0f;
            float r1 = (4.0767416621f * ldt) - (3.3077115913f * mdt) + (0.2309699292f * sdt);
            float r2 = (4.0767416621f * ldt2) - (3.3077115913f * mdt2) + (0.2309699292f * sdt2);
            float ur = r1 / ((r1 * r1) - (0.5f * r * r2));
            float tr = -r * ur;

            float g = (-1.2684380046f * ll) + (2.6097574011f * mm) - (0.3413193965f * ss) - 1.0f;
            float g1 = (-1.2684380046f * ldt) + (2.6097574011f * mdt) - (0.3413193965f * sdt);
            float g2 = (-1.2684380046f * ldt2) + (2.6097574011f * mdt2) - (0.3413193965f * sdt2);
            float ug = g1 / ((g1 * g1) - (0.5f * g * g2));
            float tg = -g * ug;

            float bb = (-0.0041960863f * ll) - (0.7034186147f * mm) + (1.7076147010f * ss) - 1.0f;
            float b1 = (-0.0041960863f * ldt) - (0.7034186147f * mdt) + (1.7076147010f * sdt);
            float b2 = (-0.0041960863f * ldt2) - (0.7034186147f * mdt2) + (1.7076147010f * sdt2);
            float ub = b1 / ((b1 * b1) - (0.5f * bb * b2));
            float tb = -bb * ub;

            const float fmax = 3.402823466e+38f;
            tr = ur >= 0.0f ? tr : fmax;
            tg = ug >= 0.0f ? tg : fmax;
            tb = ub >= 0.0f ? tb : fmax;
            t += Min(tr, Min(tg, tb));
            return t;
        }

        private static Cusp GamutHelper(float lInput, float a, float b)
        {
            var cusp = FindCusp(a, b);
            if (cusp.C <= 0.0f)
            {
                return new Cusp(lInput, 0.0f, 0.0f);
            }

            float sMax = cusp.C / cusp.L;
            float tMax = cusp.C / (1.0f - cusp.L);
            float ramp = Min(sMax * lInput, tMax * (1.0f - lInput));

            // Section 6: two 4th-order Horner polys in (a, b) (get_ST_mid).
            float c0 = (4.159012318f * b) + 7.447789669f;
            float c1 = (1.751984000f * b) + -2.195573568f;
            float c2 = (-10.023010254f * b) + -2.137049437f;
            float c3 = (5.387708187f * b) + -4.248945713f;
            float poly1 = (((((((4.698910236f * a) + c3) * a) + c2) * a) + c1) * a) + c0;
            float tPoly = (1.0f / poly1) + 0.115169927f;

            float d0 = (-0.681243777f * b) + 1.613203168f;
            float d1 = (0.901481211f * b) + 0.403706133f;
            float d2 = (0.612239897f * b) + -0.270879418f;
            float d3 = (-0.453995675f * b) + 0.002992150f;
            float poly2 = (((((((-0.146618724f * a) + d3) * a) + d2) * a) + d1) * a) + d0;
            float sPoly = (1.0f / poly2) + 0.112396419f;

            // Section 7: 4-norm merge for C_cusp_at_L.
            float x = tPoly * lInput;
            float y = sPoly * (1.0f - lInput);
            float x4 = x * x;
            x4 *= x4;
            float y4 = y * y;
            y4 *= y4;
            float invSum = 1.0f / ((1.0f / x4) + (1.0f / y4));
            float fourNorm = MathF.Sqrt(MathF.Sqrt(invSum));
            const float c189c = 0.899999976f;
            float cMax = GamutIntersectionSelf(a, b, lInput, cusp);
            float k = cMax / ramp;
            float cCuspAtL = fourNorm * k * c189c;

            float lL = 0.4f * lInput;
            float lT = 0.8f * (1.0f - lInput);
            float lCuspAtL = MathF.Sqrt(1.0f / ((1.0f / (lL * lL)) + (1.0f / (lT * lT))));

            return new Cusp(lCuspAtL, cCuspAtL, cMax);
        }

        // ================================================================
        // Median cut (FUN_011b095c, FUN_011b2464, FUN_011b1eee / median_cut.cpp)
        // ================================================================

        private static bool HeapLess(Box a, Box b) => a.Total < b.Total;

        private static List<Box> MedianCut(uint[] hist, Box seed, int k)
        {
            var queue = new List<Box>(k * 2) { seed };
            LibCxx.MakeHeap(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(queue), HeapLess);

            int splits = 1;
            for (int iter = 0; iter < 1000 && splits < k; ++iter)
            {
                LibCxx.PopHeap(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(queue), HeapLess);
                var top = queue[^1];
                queue.RemoveAt(queue.Count - 1);

                if (top.Total == 0 && top.Unique == 0)
                {
                    MakeBox(hist, ref top);
                }

                if (top.Total == 0)
                {
                    Push(queue, top);
                    break;
                }

                if (top.Total == 1 || top.Unique < 2)
                {
                    Push(queue, top);
                    bool anySplittable = false;
                    foreach (var b in queue)
                    {
                        if (b.Unique >= 2)
                        {
                            anySplittable = true;
                            break;
                        }
                    }

                    if (!anySplittable)
                    {
                        break;
                    }

                    continue;
                }

                if (!SplitBox(hist, top, out var l, out var r))
                {
                    Push(queue, top);
                    break;
                }

                Push(queue, l);
                Push(queue, r);
                splits += 1;
            }

            return queue;

            static void Push(List<Box> q, Box b)
            {
                q.Add(b);
                LibCxx.PushHeap(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(q), HeapLess);
            }
        }

        private static void MakeBox(uint[] hist, ref Box box)
        {
            ulong total = 0, unique = 0;
            if (box.Min0 <= box.Max0)
            {
                int gmin = Math.Min(box.Min1, box.Max1), gmax = Math.Max(box.Min1, box.Max1);
                int bmin = Math.Min(box.Min2, box.Max2), bmax = Math.Max(box.Min2, box.Max2);
                for (int r = box.Min0; r <= box.Max0; ++r)
                {
                    for (int g = gmin; g <= gmax; ++g)
                    {
                        for (int b = bmin; b <= bmax; ++b)
                        {
                            uint c = hist[(r << 10) | (g << 5) | b];
                            if (c != 0)
                            {
                                total += c;
                                unique += 1;
                            }
                        }
                    }
                }
            }

            box.Total = total;
            box.Unique = unique;
        }

        private static long[] BuildCumulative(uint[] hist, in Box b, int axis)
        {
            int lo = b.Lo(axis), hi = b.Hi(axis);
            var cum = new long[hi - lo + 1];
            long running = 0;
            for (int s = lo; s <= hi; ++s)
            {
                long slice = 0;
                int r0 = b.Min0, r1 = b.Max0, g0 = b.Min1, g1 = b.Max1, b0 = b.Min2, b1 = b.Max2;
                if (axis == 0)
                {
                    r0 = r1 = s;
                }
                else if (axis == 1)
                {
                    g0 = g1 = s;
                }
                else
                {
                    b0 = b1 = s;
                }

                for (int r = r0; r <= r1; ++r)
                {
                    for (int g = g0; g <= g1; ++g)
                    {
                        for (int bb = b0; bb <= b1; ++bb)
                        {
                            slice += hist[(r << 10) | (g << 5) | bb];
                        }
                    }
                }

                running += slice;
                cum[s - lo] = running;
            }

            return cum;
        }

        private static int PickAxis(in Box b)
        {
            int extR = b.Max0 - b.Min0 + 1;
            int extG = b.Max1 - b.Min1 + 1;
            int extB = b.Max2 - b.Min2 + 1;
            int maxGB = Math.Max(extG, extB);
            if (maxGB <= extR)
            {
                return 0;
            }

            int maxAll = Math.Max(extR, maxGB);
            return extG == maxAll ? 1 : 2;
        }

        private static int BiasCut(int rawCut, int lo, int hi)
        {
            int leftSpan = rawCut - lo;
            int rightSpan = hi - rawCut;
            int cut;
            if (rightSpan < leftSpan)
            {
                int biased = (int)((leftSpan * -0.5) + (double)(rawCut - 1));
                cut = lo;
                if (biased > lo)
                {
                    cut = biased;
                }
            }
            else
            {
                int biased = (int)((rightSpan * 0.5) + (double)rawCut);
                cut = hi - 1;
                if (biased < cut)
                {
                    cut = biased;
                }
            }

            if (cut < lo)
            {
                cut = lo;
            }

            if (cut > hi - 1)
            {
                cut = hi - 1;
            }

            return cut;
        }

        private static Box SubBox(in Box src, int axis, int from, int to)
        {
            var b = src;
            b.SetLo(axis, from);
            b.SetHi(axis, to);
            b.Total = 0;
            b.Unique = 0;
            return b;
        }

        private static bool SplitBox(uint[] hist, Box src, out Box left, out Box right)
        {
            left = default;
            right = default;
            if (src.Unique < 2)
            {
                return false;
            }

            int axis = PickAxis(src);
            int lo = src.Lo(axis);
            int hi = src.Hi(axis);
            if (hi <= lo)
            {
                return false;
            }

            var cum = BuildCumulative(hist, src, axis);
            long half = (long)src.Total / 2;

            int rawCut = lo;
            for (int s = lo; s <= hi; ++s)
            {
                if (cum[s - lo] > half)
                {
                    rawCut = s;
                    break;
                }

                rawCut = s;
            }

            int cut = BiasCut(rawCut, lo, hi);
            while (cut < hi && cut - lo >= 0 && cum[cut - lo] == 0)
            {
                ++cut;
            }

            if (cut > hi - 1)
            {
                cut = hi - 1;
            }

            left = SubBox(src, axis, src.Lo(axis), cut);
            right = SubBox(src, axis, cut + 1, src.Hi(axis));
            MakeBox(hist, ref left);
            MakeBox(hist, ref right);

            // PMS fallback: if either side is empty, sweep for any valid cut.
            if (left.Total == 0 || right.Total == 0)
            {
                for (int v = lo; v < hi; ++v)
                {
                    var l = SubBox(src, axis, src.Lo(axis), v);
                    var r = SubBox(src, axis, v + 1, src.Hi(axis));
                    MakeBox(hist, ref l);
                    MakeBox(hist, ref r);
                    if (l.Total > 0 && r.Total > 0)
                    {
                        left = l;
                        right = r;
                        break;
                    }
                }
            }

            return left.Total > 0 && right.Total > 0;
        }

        private static (byte R, byte G, byte B) Centroid(uint[] hist, in Box b)
        {
            ulong totalCount = 0;
            double sumR = 0, sumG = 0, sumB = 0;
            for (int r = b.Min0; r <= b.Max0; ++r)
            {
                for (int g = b.Min1; g <= b.Max1; ++g)
                {
                    for (int bb = b.Min2; bb <= b.Max2; ++bb)
                    {
                        uint c = hist[(r << 10) | (g << 5) | bb];
                        if (c == 0)
                        {
                            continue;
                        }

                        totalCount += c;
                        sumR += (double)c * ((double)r + 0.5) * 8.0;
                        sumG += (double)c * ((double)g + 0.5) * 8.0;
                        sumB += (double)c * ((double)bb + 0.5) * 8.0;
                    }
                }
            }

            if (totalCount == 0)
            {
                return (
                    (byte)((((b.Min0 + b.Max0) * 4) + 4) & 0xff),
                    (byte)((((b.Min1 + b.Max1) * 4) + 4) & 0xff),
                    (byte)((((b.Min2 + b.Max2) * 4) + 4) & 0xff));
            }

            return (
                (byte)((int)(sumR / totalCount) & 0xff),
                (byte)((int)(sumG / totalCount) & 0xff),
                (byte)((int)(sumB / totalCount) & 0xff));
        }

        // ================================================================
        // Rank (FUN_011b7169 / rank.cpp)
        // ================================================================

        private static float PositiveOrZero(float x) => x > 0.0f ? x : 0.0f;

        private static float WindowScore(float x, float lo, float hi, float width, float scale, float inWindow)
        {
            if (x >= lo && x <= hi)
            {
                return inWindow;
            }

            float d = (x < lo) ? (lo - x) : (x - hi);
            return PositiveOrZero((d / -width * 100.0f) + 100.0f) * scale;
        }

        private static float RankCandidate(in Cluster a, ulong ordinalA, ulong liveCount, in Cluster reference)
        {
            float hueDeg = a.Hue * 360.0f;
            float s = a.PmsS;
            float v = a.PmsV;

            float indexBonus;
            if (ordinalA == 0)
            {
                indexBonus = 100.0f;
            }
            else
            {
                float denom = (float)liveCount + -1.0f;
                indexBonus = denom == 0.0f ? 0.0f : PositiveOrZero(100.0f - ((float)ordinalA / denom * 100.0f));
            }

            indexBonus *= 1.0f;

            float valueBonus;
            if (v >= 0.0f && v <= 0.4f)
            {
                valueBonus = 150.0f;
            }
            else if (v < 0.0f)
            {
                valueBonus = 0.0f;
            }
            else
            {
                valueBonus = PositiveOrZero(((v - 0.4f) / -0.3f * 100.0f) + 100.0f) * 1.5f;
            }

            float sSafe = float.IsNaN(s) ? 0.0f : s;
            float saturationBonus;
            if (sSafe >= 0.6f && sSafe <= 1.0f)
            {
                saturationBonus = 300.0f;
            }
            else if (sSafe > 1.0f)
            {
                saturationBonus = 0.0f;
            }
            else
            {
                saturationBonus = PositiveOrZero(((0.6f - sSafe) / -0.4f * 100.0f) + 100.0f) * 3.0f;
            }

            float distanceBonus = 300.0f;
            if ((reference.Ordinal & 0xFF) != 0)
            {
                float dL = a.LabL - reference.LabL;
                float dA = a.LabA - reference.LabA;
                float dB = a.LabB - reference.LabB;
                float dist = MathF.Sqrt((dL * dL) + (dA * dA) + (dB * dB));
                float core;
                if (dist >= 0.05f && dist <= 0.1f)
                {
                    core = 100.0f;
                }
                else if (dist < 0.05f)
                {
                    core = 100.0f - ((0.05f - dist) / 0.05f * 100.0f);
                    core = PositiveOrZero(core);
                }
                else
                {
                    core = PositiveOrZero(((dist + -0.1f) / -0.9f * 100.0f) + 100.0f);
                }

                distanceBonus = core * 3.0f;
            }

            float greenPenalty = WindowScore(hueDeg, 80.0f, 120.0f, 10.0f, 1.0f, 100.0f);
            float hueBandA = WindowScore(hueDeg, 40.0f, 70.0f, 10.0f, 1.0f, 100.0f);
            float satBandA = WindowScore(sSafe, 0.2f, 0.6f, 0.1f, 1.0f, 100.0f);
            float satBandB = WindowScore(sSafe, 0.0f, 0.2f, 0.2f, 1.0f, 100.0f);

            float valueBandB;
            if (v >= 0.5f && v <= 1.0f)
            {
                valueBandB = 100.0f;
            }
            else if (v < 0.5f)
            {
                valueBandB = PositiveOrZero(((0.5f - v) / -0.2f * 100.0f) + 100.0f);
            }
            else
            {
                valueBandB = 0.0f;
            }

            float pairPenaltyA = 0.0f;
            if (hueBandA != 0.0f && satBandA != 0.0f)
            {
                pairPenaltyA = (hueBandA + satBandA) * 0.5f * -2.0f;
            }

            float pairPenaltyB = 0.0f;
            if (satBandB != 0.0f && valueBandB != 0.0f)
            {
                pairPenaltyB = (satBandB + valueBandB) * 0.5f * -2.0f;
            }

            float total = indexBonus;
            total += valueBonus;
            total += saturationBonus;
            total += distanceBonus;
            total -= greenPenalty * 2.0f;
            total += pairPenaltyA;
            total += pairPenaltyB;
            return total;
        }

        // ================================================================
        // Render (FUN_011b04ee = Björn okhsl_to_srgb / render.cpp)
        // ================================================================

        private static float SrgbOetf(float x)
        {
            if (x <= 0.0031308f)
            {
                return 12.92f * x;
            }

            return (1.055f * MuslMath.Powf(x, 1.0f / 2.4f)) - 0.055f;
        }

        private static uint ToByte(float lin)
        {
            float v = SrgbOetf(lin);
            if (v < 0.0f)
            {
                v = 0.0f;
            }

            if (v > 1.0f)
            {
                v = 1.0f;
            }

            int i = (int)(v * 255.0f); // CVTTSS2SI truncation
            return (uint)Math.Clamp(i, 0, 255);
        }

        private static float InverseScoreToChroma(float s, Cusp cusp)
        {
            const float mul = 0.8f;
            const float inv = 1.25f;
            float c0 = cusp.L, cMid = cusp.C, cMax = cusp.CMax;
            if (s < mul)
            {
                float k = c0 * mul;
                float denom = 1.0f - ((1.0f - (k / cMid)) * s * inv);
                if (MathF.Abs(denom) < 1e-12f)
                {
                    return 0.0f;
                }

                return s * inv * k / denom;
            }

            float t = (s - mul) * 5.0f;
            float kHigh = 0.2f * cMid * cMid * inv * inv / c0;
            float diff = cMax - cMid;
            float denom2 = 1.0f - ((1.0f - (kHigh / diff)) * t);
            if (MathF.Abs(denom2) < 1e-12f)
            {
                return cMid;
            }

            return (t * kHigh / denom2) + cMid;
        }

        private static uint RenderCluster(in Cluster picked, float vCeil)
        {
            float v = Min(picked.PmsV, vCeil);
            float s = picked.PmsS;
            if (s > 0.2f)
            {
                s = Max(s, 0.6f);
            }

            if (v == 0.0f)
            {
                return 0x000000;
            }

            if (v >= 1.0f)
            {
                return 0xffffff;
            }

            float hRad = 6.2831855f * picked.Hue;
            float aUnit = MuslMath.Cosf(hRad);
            float bUnit = MuslMath.Sinf(hRad);
            float lLin = ToeInv(v);
            var cusp = GamutHelper(lLin, aUnit, bUnit);
            float c = InverseScoreToChroma(s, cusp);
            var lin = OklabToLinearSrgb(lLin, c * aUnit, c * bUnit);
            return (ToByte(lin.X) << 16) | (ToByte(lin.Y) << 8) | ToByte(lin.Z);
        }

        // std::min / std::max semantics (NaN and -0 handling differ from MathF).
        private static float Min(float a, float b) => b < a ? b : a;

        private static float Max(float a, float b) => a < b ? b : a;

        private readonly record struct Vec3(float X, float Y, float Z);

        /// <summary>FUN_011b3738 lanes: L = C_0, C = C_mid, CMax = Halley-refined gamut max.</summary>
        private readonly record struct Cusp(float L, float C, float CMax);

        /// <summary>The 0x30-byte PMS cluster record fields the rank/render read.</summary>
        private record struct Cluster(
            byte R, byte G, byte B, float Hue, float PmsS, float PmsV, float LabL, float LabA, float LabB, ulong Ordinal);

        private struct Box
        {
            public int Min0, Min1, Min2, Max0, Max1, Max2;
            public ulong Total, Unique;

            public Box(int min0, int min1, int min2, int max0, int max1, int max2)
            {
                Min0 = min0;
                Min1 = min1;
                Min2 = min2;
                Max0 = max0;
                Max1 = max1;
                Max2 = max2;
                Total = 0;
                Unique = 0;
            }

            public readonly int Lo(int axis) => axis == 0 ? Min0 : axis == 1 ? Min1 : Min2;

            public readonly int Hi(int axis) => axis == 0 ? Max0 : axis == 1 ? Max1 : Max2;

            public void SetLo(int axis, int v)
            {
                if (axis == 0)
                {
                    Min0 = v;
                }
                else if (axis == 1)
                {
                    Min1 = v;
                }
                else
                {
                    Min2 = v;
                }
            }

            public void SetHi(int axis, int v)
            {
                if (axis == 0)
                {
                    Max0 = v;
                }
                else if (axis == 1)
                {
                    Max1 = v;
                }
                else
                {
                    Max2 = v;
                }
            }
        }
    }

    /// <summary>Decoded image, RGBA8888 row-major (alpha unused).</summary>
    internal sealed record RgbaImage(int Width, int Height, byte[] Pixels);
}
