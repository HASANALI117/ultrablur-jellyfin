using System.Runtime.CompilerServices;

namespace UltraBlur.Plugin.Algorithm
{
    /// <summary>
    /// Bit-exact C# ports of the musl 1.2.5 single-precision libm routines the
    /// UltraBlur pipeline calls (cbrtf, powf, sinf, cosf, atan2f). PMS links
    /// musl; MathF.* forwards to the host CRT (glibc / MSVC), which differs in
    /// the last ULP and flips corner colors by 1-2/255 on ~30% of images.
    /// Sources: pms/RE/ref-src/musl (copied from zig 0.13's bundled musl, the
    /// toolchain the bit-exact ultrablur_cli was built with). x86_64 musl has
    /// FLT_EVAL_METHOD 0 and TOINT_INTRINSICS 0, so double_t == double and the
    /// exp2 SHIFT path is used.
    /// </summary>
    internal static class MuslMath
    {
        // ---- cbrtf ---------------------------------------------------------

        private const uint CbrtB1 = 709958130;
        private const uint CbrtB2 = 642849266;

        public static float Cbrtf(float x)
        {
            uint ui = BitConverter.SingleToUInt32Bits(x);
            uint hx = ui & 0x7fffffff;
            if (hx >= 0x7f800000)
            {
                return x + x;
            }

            if (hx < 0x00800000)
            {
                if (hx == 0)
                {
                    return x;
                }

                ui = BitConverter.SingleToUInt32Bits(x * 16777216f); // 0x1p24f
                hx = ui & 0x7fffffff;
                hx = (hx / 3) + CbrtB2;
            }
            else
            {
                hx = (hx / 3) + CbrtB1;
            }

            ui &= 0x80000000;
            ui |= hx;

            double t = BitConverter.UInt32BitsToSingle(ui);
            double r = t * t * t;
            t = t * ((double)x + x + r) / (x + r + r);
            r = t * t * t;
            t = t * ((double)x + x + r) / (x + r + r);
            return (float)t;
        }

        // ---- powf ----------------------------------------------------------

        private const int PowfLog2TableBits = 4;
        private const uint PowfOff = 0x3f330000;

        private static readonly double[] PowfInvc = Bits(
            0x3FF661EC79F8F3BE, 0x3FF571ED4AAF883D, 0x3FF49539F0F010B0, 0x3FF3C995B0B80385,
            0x3FF30D190C8864A5, 0x3FF25E227B0B8EA0, 0x3FF1BB4A4A1A343F, 0x3FF12358F08AE5BA,
            0x3FF0953F419900A7, 0x3FF0000000000000, 0x3FEE608CFD9A47AC, 0x3FECA4B31F026AA0,
            0x3FEB2036576AFCE6, 0x3FE9C2D163A1AA2D, 0x3FE886E6037841ED, 0x3FE767DCF5534862);

        private static readonly double[] PowfLogc = Bits(
            0xBFDEFEC65B963019, 0xBFDB0B6832D4FCA4, 0xBFD7418B0A1FB77B, 0xBFD39DE91A6DCF7B,
            0xBFD01D9BF3F2B631, 0xBFC97C1D1B3B7AF0, 0xBFC2F9E393AF3C9F, 0xBFB960CBBF788D5C,
            0xBFAA6F9DB6475FCE, 0x0000000000000000, 0x3FB338CA9F24F53D, 0x3FC476A9543891BA,
            0x3FCE840B4AC4E4D2, 0x3FD40645F0C6651C, 0x3FD88E9C2C1B9FF8, 0x3FDCE0A44EB17BCC);

        private static readonly double[] PowfPoly = Bits(
            0x3FD27616C9496E0B, 0xBFD71969A075C67A, 0x3FDEC70A6CA7BADD, 0xBFE7154748BEF6C8,
            0x3FF71547652AB82B);

        private const int Exp2fTableBits = 5;
        private const uint Exp2fSignBias = 1u << (Exp2fTableBits + 11);

        private static readonly ulong[] Exp2fTab =
        {
            0x3ff0000000000000, 0x3fefd9b0d3158574, 0x3fefb5586cf9890f, 0x3fef9301d0125b51,
            0x3fef72b83c7d517b, 0x3fef54873168b9aa, 0x3fef387a6e756238, 0x3fef1e9df51fdee1,
            0x3fef06fe0a31b715, 0x3feef1a7373aa9cb, 0x3feedea64c123422, 0x3feece086061892d,
            0x3feebfdad5362a27, 0x3feeb42b569d4f82, 0x3feeab07dd485429, 0x3feea47eb03a5585,
            0x3feea09e667f3bcd, 0x3fee9f75e8ec5f74, 0x3feea11473eb0187, 0x3feea589994cce13,
            0x3feeace5422aa0db, 0x3feeb737b0cdc5e5, 0x3feec49182a3f090, 0x3feed503b23e255d,
            0x3feee89f995ad3ad, 0x3feeff76f2fb5e47, 0x3fef199bdd85529c, 0x3fef3720dcef9069,
            0x3fef5818dcfba487, 0x3fef7c97337b9b5f, 0x3fefa4afa2a490da, 0x3fefd0765b6e4540,
        };

        private static readonly double Exp2fShiftScaled = BitConverter.UInt64BitsToDouble(0x42E8000000000000);

        private static readonly double[] Exp2fPoly = Bits(0x3FAC6AF84B912394, 0x3FCEBFCE50FAC4F3, 0x3FE62E42FF0C52D6);

        public static float Powf(float x, float y)
        {
            uint signBias = 0;
            uint ix = BitConverter.SingleToUInt32Bits(x);
            uint iy = BitConverter.SingleToUInt32Bits(y);
            if (ix - 0x00800000 >= 0x7f800000 - 0x00800000 || ZeroInfNan(iy))
            {
                // Either (x < 0x1p-126 or inf or nan) or (y is 0 or inf or nan).
                if (ZeroInfNan(iy))
                {
                    // musl builds without WANT_SNAN, so issignalingf_inline() == 0.
                    if (2 * iy == 0)
                    {
                        return 1.0f;
                    }

                    if (ix == 0x3f800000)
                    {
                        return 1.0f;
                    }

                    if (2 * ix > 2u * 0x7f800000 || 2 * iy > 2u * 0x7f800000)
                    {
                        return x + y;
                    }

                    if (2 * ix == 2 * 0x3f800000)
                    {
                        return 1.0f;
                    }

                    if ((2 * ix < 2 * 0x3f800000) == ((iy & 0x80000000) == 0))
                    {
                        return 0.0f;
                    }

                    return y * y;
                }

                if (ZeroInfNan(ix))
                {
                    float x2 = x * x;
                    if ((ix & 0x80000000) != 0 && CheckInt(iy) == 1)
                    {
                        x2 = -x2;
                    }

                    return (iy & 0x80000000) != 0 ? 1 / x2 : x2;
                }

                // x and y are non-zero finite.
                if ((ix & 0x80000000) != 0)
                {
                    int yint = CheckInt(iy);
                    if (yint == 0)
                    {
                        return (x - x) / (x - x); // __math_invalidf
                    }

                    if (yint == 1)
                    {
                        signBias = Exp2fSignBias;
                    }

                    ix &= 0x7fffffff;
                }

                if (ix < 0x00800000)
                {
                    // Normalize subnormal x so exponent becomes negative.
                    ix = BitConverter.SingleToUInt32Bits(x * 8388608f); // 0x1p23f
                    ix &= 0x7fffffff;
                    ix -= 23 << 23;
                }
            }

            double logx = Log2Inline(ix);
            double ylogx = y * logx;
            if (((BitConverter.DoubleToUInt64Bits(ylogx) >> 47) & 0xffff) >= (BitConverter.DoubleToUInt64Bits(126.0) >> 47))
            {
                // |y*log(x)| >= 126.
                if (ylogx > BitConverter.UInt64BitsToDouble(0x405FFFFFFFD1D571))
                {
                    return XflowF(signBias, BitConverter.UInt32BitsToSingle(0x70000000)); // __math_oflowf: 0x1p97f
                }

                if (ylogx <= -150.0)
                {
                    return XflowF(signBias, BitConverter.UInt32BitsToSingle(0x10000000)); // __math_uflowf: 0x1p-95f
                }
            }

            return Exp2Inline(ylogx, signBias);
        }

        private static float XflowF(uint sign, float y)
        {
            float v = sign != 0 ? -y : y;
            return v * y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Log2Inline(uint ix)
        {
            const int n = 1 << PowfLog2TableBits;
            uint tmp = ix - PowfOff;
            int i = (int)((tmp >> (23 - PowfLog2TableBits)) % n);
            uint top = tmp & 0xff800000;
            uint iz = ix - top;
            int k = (int)top >> 23; // arithmetic shift, POWF_SCALE_BITS == 0
            double invc = PowfInvc[i];
            double logc = PowfLogc[i];
            double z = BitConverter.UInt32BitsToSingle(iz);

            // log2(x) = log1p(z/c-1)/ln2 + log2(c) + k
            double r = (z * invc) - 1;
            double y0 = logc + k;

            double r2 = r * r;
            double y = (PowfPoly[0] * r) + PowfPoly[1];
            double p = (PowfPoly[2] * r) + PowfPoly[3];
            double r4 = r2 * r2;
            double q = (PowfPoly[4] * r) + y0;
            q = (p * r2) + q;
            y = (y * r4) + q;
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Exp2Inline(double xd, uint signBias)
        {
            const int n = 1 << Exp2fTableBits;
            double kd = xd + Exp2fShiftScaled;
            ulong ki = BitConverter.DoubleToUInt64Bits(kd);
            kd -= Exp2fShiftScaled;
            double r = xd - kd;

            ulong t = Exp2fTab[ki % n];
            ulong ski = ki + signBias;
            t += ski << (52 - Exp2fTableBits);
            double s = BitConverter.UInt64BitsToDouble(t);
            double z = (Exp2fPoly[0] * r) + Exp2fPoly[1];
            double r2 = r * r;
            double y = (Exp2fPoly[2] * r) + 1;
            y = (z * r2) + y;
            y = y * s;
            return (float)y;
        }

        private static int CheckInt(uint iy)
        {
            int e = (int)((iy >> 23) & 0xff);
            if (e < 0x7f)
            {
                return 0;
            }

            if (e > 0x7f + 23)
            {
                return 2;
            }

            if ((iy & ((1u << (0x7f + 23 - e)) - 1)) != 0)
            {
                return 0;
            }

            if ((iy & (1u << (0x7f + 23 - e))) != 0)
            {
                return 1;
            }

            return 2;
        }

        private static bool ZeroInfNan(uint ix) => (2 * ix) - 1 >= (2u * 0x7f800000) - 1;

        // ---- sinf / cosf ---------------------------------------------------

        private static readonly double S1Pio2 = BitConverter.UInt64BitsToDouble(0x3FF921FB54442D18);
        private static readonly double S2Pio2 = BitConverter.UInt64BitsToDouble(0x400921FB54442D18);
        private static readonly double S3Pio2 = BitConverter.UInt64BitsToDouble(0x4012D97C7F3321D2);
        private static readonly double S4Pio2 = BitConverter.UInt64BitsToDouble(0x401921FB54442D18);

        private static readonly double SinS1 = BitConverter.UInt64BitsToDouble(0xBFC5555554CBAC77);
        private static readonly double SinS2 = BitConverter.UInt64BitsToDouble(0x3F811110896EFBB2);
        private static readonly double SinS3 = BitConverter.UInt64BitsToDouble(0xBF2A00F9E2CAE774);
        private static readonly double SinS4 = BitConverter.UInt64BitsToDouble(0x3EC6CD878C3B46A7);
        private static readonly double CosC0 = BitConverter.UInt64BitsToDouble(0xBFDFFFFFFD0C5E81);
        private static readonly double CosC1 = BitConverter.UInt64BitsToDouble(0x3FA55553E1053A42);
        private static readonly double CosC2 = BitConverter.UInt64BitsToDouble(0xBF56C087E80F1E27);
        private static readonly double CosC3 = BitConverter.UInt64BitsToDouble(0x3EF99342E0EE5069);

        public static float Sinf(float x)
        {
            uint ix = BitConverter.SingleToUInt32Bits(x);
            bool sign = (ix >> 31) != 0;
            ix &= 0x7fffffff;

            if (ix <= 0x3f490fda)
            {
                // |x| ~<= pi/4
                if (ix < 0x39800000)
                {
                    return x;
                }

                return SinDf(x);
            }

            if (ix <= 0x407b53d1)
            {
                // |x| ~<= 5*pi/4
                if (ix <= 0x4016cbe3)
                {
                    // |x| ~<= 3pi/4
                    return sign ? -CosDf(x + S1Pio2) : CosDf(x - S1Pio2);
                }

                return SinDf(sign ? -(x + S2Pio2) : -(x - S2Pio2));
            }

            if (ix <= 0x40e231d5)
            {
                // |x| ~<= 9*pi/4
                if (ix <= 0x40afeddf)
                {
                    // |x| ~<= 7*pi/4
                    return sign ? CosDf(x + S3Pio2) : -CosDf(x - S3Pio2);
                }

                return SinDf(sign ? x + S4Pio2 : x - S4Pio2);
            }

            if (ix >= 0x7f800000)
            {
                return x - x;
            }

            int nq = RemPio2f(x, out double y);
            return (nq & 3) switch
            {
                0 => SinDf(y),
                1 => CosDf(y),
                2 => SinDf(-y),
                _ => -CosDf(y),
            };
        }

        public static float Cosf(float x)
        {
            uint ix = BitConverter.SingleToUInt32Bits(x);
            bool sign = (ix >> 31) != 0;
            ix &= 0x7fffffff;

            if (ix <= 0x3f490fda)
            {
                // |x| ~<= pi/4
                if (ix < 0x39800000)
                {
                    return 1.0f;
                }

                return CosDf(x);
            }

            if (ix <= 0x407b53d1)
            {
                // |x| ~<= 5*pi/4
                if (ix > 0x4016cbe3)
                {
                    // |x| ~> 3*pi/4
                    return -CosDf(sign ? x + S2Pio2 : x - S2Pio2);
                }

                return sign ? SinDf(x + S1Pio2) : SinDf(S1Pio2 - x);
            }

            if (ix <= 0x40e231d5)
            {
                // |x| ~<= 9*pi/4
                if (ix > 0x40afeddf)
                {
                    // |x| ~> 7*pi/4
                    return CosDf(sign ? x + S4Pio2 : x - S4Pio2);
                }

                return sign ? SinDf(-x - S3Pio2) : SinDf(x - S3Pio2);
            }

            if (ix >= 0x7f800000)
            {
                return x - x;
            }

            int nq = RemPio2f(x, out double y);
            return (nq & 3) switch
            {
                0 => CosDf(y),
                1 => SinDf(-y),
                2 => -CosDf(y),
                _ => SinDf(y),
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SinDf(double x)
        {
            double z = x * x;
            double w = z * z;
            double r = SinS3 + (z * SinS4);
            double s = z * x;
            return (float)((x + (s * (SinS1 + (z * SinS2)))) + (s * w * r));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float CosDf(double x)
        {
            double z = x * x;
            double w = z * z;
            double r = CosC2 + (z * CosC3);
            return (float)(((1.0 + (z * CosC0)) + (w * CosC1)) + (w * z * r));
        }

        private static int RemPio2f(float x, out double y)
        {
            const double toint = 6755399441055744.0; // 1.5 / DBL_EPSILON
            const double invpio2 = 6.36619772367581382433e-01;
            const double pio2_1 = 1.57079631090164184570e+00;
            const double pio2_1t = 1.58932547735281966916e-08;
            double pio4 = BitConverter.UInt64BitsToDouble(0x3FE921FB60000000);

            uint ix = BitConverter.SingleToUInt32Bits(x) & 0x7fffffff;

            // ponytail: medium-size reduction only (|x| < 2^28*pi/2). The
            // pipeline only feeds hue angles in [0, 2pi], which never reach
            // here at all; port __rem_pio2_large if that ever changes.
            if (ix >= 0x4dc90fdb)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, "musl __rem_pio2_large path not ported");
            }

            double fn = ((double)x * invpio2) + toint - toint;
            int n = (int)fn;
            y = x - (fn * pio2_1) - (fn * pio2_1t);
            if (y < -pio4)
            {
                n--;
                fn--;
                y = x - (fn * pio2_1) - (fn * pio2_1t);
            }
            else if (y > pio4)
            {
                n++;
                fn++;
                y = x - (fn * pio2_1) - (fn * pio2_1t);
            }

            return n;
        }

        // ---- atan2f / atanf ------------------------------------------------

        private const float AtanPi = 3.1415927410e+00f;      // 0x40490fdb
        private const float AtanPiLo = -8.7422776573e-08f;   // 0xb3bbbd2e

        private static readonly float[] AtanHi =
        {
            4.6364760399e-01f, 7.8539812565e-01f, 9.8279368877e-01f, 1.5707962513e+00f,
        };

        private static readonly float[] AtanLo =
        {
            5.0121582440e-09f, 3.7748947079e-08f, 3.4473217170e-08f, 7.5497894159e-08f,
        };

        private static readonly float[] AtanT =
        {
            3.3333328366e-01f, -1.9999158382e-01f, 1.4253635705e-01f, -1.0648017377e-01f, 6.1687607318e-02f,
        };

        public static float Atan2f(float y, float x)
        {
            if (float.IsNaN(x) || float.IsNaN(y))
            {
                return x + y;
            }

            uint ix = BitConverter.SingleToUInt32Bits(x);
            uint iy = BitConverter.SingleToUInt32Bits(y);
            if (ix == 0x3f800000)
            {
                return Atanf(y);
            }

            uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2); // 2*sign(x)+sign(y)
            ix &= 0x7fffffff;
            iy &= 0x7fffffff;

            if (iy == 0)
            {
                switch (m)
                {
                    case 0:
                    case 1: return y;
                    case 2: return AtanPi;
                    default: return -AtanPi;
                }
            }

            if (ix == 0)
            {
                return (m & 1) != 0 ? -AtanPi / 2 : AtanPi / 2;
            }

            if (ix == 0x7f800000)
            {
                if (iy == 0x7f800000)
                {
                    switch (m)
                    {
                        case 0: return AtanPi / 4;
                        case 1: return -AtanPi / 4;
                        case 2: return 3 * AtanPi / 4;
                        default: return -3 * AtanPi / 4;
                    }
                }

                switch (m)
                {
                    case 0: return 0.0f;
                    case 1: return -0.0f;
                    case 2: return AtanPi;
                    default: return -AtanPi;
                }
            }

            // |y/x| > 0x1p26
            if (ix + (26u << 23) < iy || iy == 0x7f800000)
            {
                return (m & 1) != 0 ? -AtanPi / 2 : AtanPi / 2;
            }

            float z;
            if ((m & 2) != 0 && iy + (26u << 23) < ix)
            {
                // |y/x| < 0x1p-26, x < 0
                z = 0.0f;
            }
            else
            {
                z = Atanf(MathF.Abs(y / x));
            }

            switch (m)
            {
                case 0: return z;
                case 1: return -z;
                case 2: return AtanPi - (z - AtanPiLo);
                default: return (z - AtanPiLo) - AtanPi;
            }
        }

        private static float Atanf(float x)
        {
            uint ix = BitConverter.SingleToUInt32Bits(x);
            bool sign = (ix >> 31) != 0;
            ix &= 0x7fffffff;
            int id;
            float z;

            if (ix >= 0x4c800000)
            {
                // |x| >= 2**26
                if (float.IsNaN(x))
                {
                    return x;
                }

                z = AtanHi[3] + BitConverter.UInt32BitsToSingle(0x03800000); // 0x1p-120f
                return sign ? -z : z;
            }

            if (ix < 0x3ee00000)
            {
                // |x| < 0.4375
                if (ix < 0x39800000)
                {
                    return x;
                }

                id = -1;
            }
            else
            {
                x = MathF.Abs(x);
                if (ix < 0x3f980000)
                {
                    // |x| < 1.1875
                    if (ix < 0x3f300000)
                    {
                        id = 0;
                        x = ((2.0f * x) - 1.0f) / (2.0f + x);
                    }
                    else
                    {
                        id = 1;
                        x = (x - 1.0f) / (x + 1.0f);
                    }
                }
                else if (ix < 0x401c0000)
                {
                    // |x| < 2.4375
                    id = 2;
                    x = (x - 1.5f) / (1.0f + (1.5f * x));
                }
                else
                {
                    id = 3;
                    x = -1.0f / x;
                }
            }

            z = x * x;
            float w = z * z;
            float s1 = z * (AtanT[0] + (w * (AtanT[2] + (w * AtanT[4]))));
            float s2 = w * (AtanT[1] + (w * AtanT[3]));
            if (id < 0)
            {
                return x - (x * (s1 + s2));
            }

            z = AtanHi[id] - (((x * (s1 + s2)) - AtanLo[id]) - x);
            return sign ? -z : z;
        }

        private static double[] Bits(params ulong[] bits)
        {
            var d = new double[bits.Length];
            for (int i = 0; i < bits.Length; i++)
            {
                d[i] = BitConverter.UInt64BitsToDouble(bits[i]);
            }

            return d;
        }
    }
}
