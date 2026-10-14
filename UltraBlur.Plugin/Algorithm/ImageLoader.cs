using SkiaSharp;

namespace UltraBlur.Plugin.Algorithm
{
    /// <summary>
    /// Decodes artwork the way PMS does (cv::imdecode IMREAD_COLOR, then
    /// FUN_011ae916's resize-if-over-8.3MP). SkiaSharp is the Jellyfin host's
    /// image library; its bundled libjpeg-turbo decodes byte-identical to
    /// Plex's OpenCV 4.5 / libjpeg-turbo 2.1.2 (verified on all 199 dataset
    /// JPEGs, pms/RE/diff_1.43/cmpdecode.py).
    /// </summary>
    internal static class ImageLoader
    {
        /// <summary>PMS resize threshold (DAT_003c7cb0 = 0x7e9000 = 3840*2160).</summary>
        private const int MaxPixels = 0x7e9000;

        // Trust-boundary guard: refuse absurd dimensions instead of allocating GBs.
        private const long MaxDecodePixels = 100_000_000;

        public static RgbaImage? Load(string path)
        {
            using var codec = SKCodec.Create(path);
            if (codec == null)
            {
                return null;
            }

            int w = codec.Info.Width, h = codec.Info.Height;
            if (w <= 0 || h <= 0 || (long)w * h > MaxDecodePixels)
            {
                return null;
            }

            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            SKCodecResult result;
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    result = codec.GetPixels(info, (IntPtr)p);
                }
            }

            // IncompleteInput = truncated file; libjpeg (and so OpenCV) also
            // returns the partially decoded image for a premature EOF.
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                return null;
            }

            var img = new RgbaImage(w, h, pixels);

            // OpenCV 4.5 ExifReader only parses JPEG APP1, so only JPEGs rotate.
            if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg)
            {
                img = ApplyExifOrientation(img, codec.EncodedOrigin);
            }

            return MaybeResize(img);
        }

        // ---- EXIF orientation (OpenCV loadsave.cpp ExifTransform) -----------

        private static RgbaImage ApplyExifOrientation(RgbaImage img, SKEncodedOrigin origin)
        {
            // SKEncodedOrigin values are the EXIF orientation tag values 1..8.
            return origin switch
            {
                SKEncodedOrigin.TopRight => Flip(img, horizontal: true, vertical: false),
                SKEncodedOrigin.BottomRight => Flip(img, horizontal: true, vertical: true),
                SKEncodedOrigin.BottomLeft => Flip(img, horizontal: false, vertical: true),
                SKEncodedOrigin.LeftTop => Transpose(img),
                SKEncodedOrigin.RightTop => Flip(Transpose(img), horizontal: true, vertical: false),
                SKEncodedOrigin.RightBottom => Flip(Transpose(img), horizontal: true, vertical: true),
                SKEncodedOrigin.LeftBottom => Flip(Transpose(img), horizontal: false, vertical: true),
                _ => img,
            };
        }

        private static RgbaImage Flip(RgbaImage img, bool horizontal, bool vertical)
        {
            int w = img.Width, h = img.Height;
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(img.Pixels);
            var outBytes = new byte[img.Pixels.Length];
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(outBytes.AsSpan());
            for (int y = 0; y < h; y++)
            {
                int sy = vertical ? h - 1 - y : y;
                for (int x = 0; x < w; x++)
                {
                    int sx = horizontal ? w - 1 - x : x;
                    dst[(y * w) + x] = src[(sy * w) + sx];
                }
            }

            return new RgbaImage(w, h, outBytes);
        }

        private static RgbaImage Transpose(RgbaImage img)
        {
            int w = img.Width, h = img.Height;
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(img.Pixels);
            var outBytes = new byte[img.Pixels.Length];
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(outBytes.AsSpan());
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    dst[(x * h) + y] = src[(y * w) + x];
                }
            }

            return new RgbaImage(h, w, outBytes);
        }

        // ---- resize (FUN_011ae916 + OpenCV 4.5.0 resize.cpp, 8UC3) ------------

        internal static RgbaImage MaybeResize(RgbaImage img)
        {
            int px = img.Width * img.Height;
            if (px <= MaxPixels)
            {
                return img;
            }

            // PMS passes an explicit dsize truncated with a C cast (cvttsd2si).
            double s = Math.Sqrt((double)MaxPixels / px);
            int dw = (int)(img.Width * s), dh = (int)(img.Height * s);

            // PMS resizes the 3-channel BGR Mat. Per-channel math is channel-order
            // independent, but the SIMD/scalar split runs over width*cn, so the
            // resize must see exactly 3 channels.
            const int cn = 3;
            var src = new byte[img.Width * img.Height * cn];
            for (int i = 0, j = 0; j < src.Length; i += 4, j += 3)
            {
                src[j] = img.Pixels[i];
                src[j + 1] = img.Pixels[i + 1];
                src[j + 2] = img.Pixels[i + 2];
            }

            var dst = ResizeBgr(src, img.Width, img.Height, dw, dh);
            var outBytes = new byte[dw * dh * 4];
            for (int i = 0, j = 0; j < dst.Length; i += 4, j += 3)
            {
                outBytes[i] = dst[j];
                outBytes[i + 1] = dst[j + 1];
                outBytes[i + 2] = dst[j + 2];
                outBytes[i + 3] = 255;
            }

            return new RgbaImage(dw, dh, outBytes);
        }

        /// <summary>
        /// cv::resize(INTER_LINEAR) for CV_8UC3, downscale only. Exactly-2x
        /// downscales take OpenCV's INTER_AREA fast path; everything else the
        /// fixed-point resizeGeneric_ with CV_SIMD128 vertical pass.
        /// </summary>
        internal static byte[] ResizeBgr(byte[] src, int sw, int sh, int dw, int dh)
        {
            const int cn = 3;
            double invScaleX = (double)dw / sw, invScaleY = (double)dh / sh;
            double scaleX = 1.0 / invScaleX, scaleY = 1.0 / invScaleY;
            int iscaleX = SaturateInt(scaleX), iscaleY = SaturateInt(scaleY);
            const double dblEpsilon = 2.220446049250313e-16; // DBL_EPSILON (not double.Epsilon)
            bool isAreaFast = Math.Abs(scaleX - iscaleX) < dblEpsilon && Math.Abs(scaleY - iscaleY) < dblEpsilon;
            var dst = new byte[dw * dh * cn];

            if (isAreaFast && iscaleX == 2 && iscaleY == 2)
            {
                // resizeAreaFast_ 2x2: (a + b + c + d + 2) >> 2 per channel.
                int sstep = sw * cn;
                for (int dy = 0; dy < dh; dy++)
                {
                    int s0 = dy * 2 * sstep, s1 = s0 + sstep, d = dy * dw * cn;
                    for (int dx = 0; dx < dw * cn; dx += 3)
                    {
                        int idx = dx * 2;
                        for (int c = 0; c < 3; c++)
                        {
                            dst[d + dx + c] = (byte)((src[s0 + idx + c] + src[s0 + idx + c + 3]
                                + src[s1 + idx + c] + src[s1 + idx + c + 3] + 2) >> 2);
                        }
                    }
                }

                return dst;
            }

            const int coefScale = 2048; // INTER_RESIZE_COEF_SCALE
            int width = dw * cn;
            var xofs = new int[width];
            var ialpha = new short[width * 2];
            int xmax = dw;
            for (int dx = 0; dx < dw; dx++)
            {
                float fx = (float)(((dx + 0.5) * scaleX) - 0.5);
                int sx = (int)MathF.Floor(fx);
                fx -= sx;
                if (sx < 0)
                {
                    fx = 0;
                    sx = 0;
                }

                if (sx + 1 >= sw)
                {
                    xmax = Math.Min(xmax, dx);
                    if (sx >= sw - 1)
                    {
                        fx = 0;
                        sx = sw - 1;
                    }
                }

                short a0 = SaturateShort((1.0f - fx) * coefScale);
                short a1 = SaturateShort(fx * coefScale);
                for (int k = 0; k < cn; k++)
                {
                    xofs[(dx * cn) + k] = (sx * cn) + k;
                    ialpha[(((dx * cn) + k) * 2) + 0] = a0;
                    ialpha[(((dx * cn) + k) * 2) + 1] = a1;
                }
            }

            xmax *= cn; // xmin unused: the clamped left-border xofs/alpha give the same sums

            var yofs = new int[dh];
            var ibeta = new short[dh * 2];
            for (int dy = 0; dy < dh; dy++)
            {
                float fy = (float)(((dy + 0.5) * scaleY) - 0.5);
                int sy = (int)MathF.Floor(fy);
                fy -= sy;
                yofs[dy] = sy;
                ibeta[dy * 2] = SaturateShort((1.0f - fy) * coefScale);
                ibeta[(dy * 2) + 1] = SaturateShort(fy * coefScale);
            }

            int sstride = sw * cn;
            var row0 = new int[width];
            var row1 = new int[width];
            for (int dy = 0; dy < dh; dy++)
            {
                HResize(src, Clip(yofs[dy], sh) * sstride, row0, xofs, ialpha, width, xmax, cn);
                HResize(src, Clip(yofs[dy] + 1, sh) * sstride, row1, xofs, ialpha, width, xmax, cn);
                VResize(row0, row1, dst, dy * width, ibeta[dy * 2], ibeta[(dy * 2) + 1], width);
            }

            return dst;
        }

        private static void HResize(byte[] src, int rowOff, int[] dst, int[] xofs, short[] alpha, int width, int xmax, int cn)
        {
            int dx = 0;
            for (; dx < xmax; dx++)
            {
                int sx = rowOff + xofs[dx];
                dst[dx] = (src[sx] * alpha[dx * 2]) + (src[sx + cn] * alpha[(dx * 2) + 1]);
            }

            for (; dx < width; dx++)
            {
                dst[dx] = src[rowOff + xofs[dx]] * 2048;
            }
        }

        /// <summary>
        /// VResizeLinear 8u with VResizeLinearVec_32s8u (CV_SIMD128: 16 then 8
        /// lanes): SIMD lanes compute ((S0>>4)*b0 >> 16) + ((S1>>4)*b1 >> 16),
        /// then (x + 2) >> 2; the scalar tail computes (S0*b0 + S1*b1 + 2^21) >> 22.
        /// The two differ by rounding, so the split point is load-bearing.
        /// </summary>
        private static void VResize(int[] s0, int[] s1, byte[] dst, int off, short b0, short b1, int width)
        {
            int x = 0;
            for (; x <= width - 16; x += 16)
            {
                for (int i = x; i < x + 16; i++)
                {
                    dst[off + i] = SimdLane(s0[i], s1[i], b0, b1);
                }
            }

            for (; x < width - 8; x += 8)
            {
                for (int i = x; i < x + 8; i++)
                {
                    dst[off + i] = SimdLane(s0[i], s1[i], b0, b1);
                }
            }

            for (; x < width; x++)
            {
                int v = ((s0[x] * b0) + (s1[x] * b1) + (1 << 21)) >> 22;
                dst[off + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        private static byte SimdLane(int s0, int s1, short b0, short b1)
        {
            short t0 = (short)Math.Clamp(s0 >> 4, short.MinValue, short.MaxValue); // v_pack (saturating)
            short t1 = (short)Math.Clamp(s1 >> 4, short.MinValue, short.MaxValue);
            int m0 = (t0 * b0) >> 16; // v_mul_hi
            int m1 = (t1 * b1) >> 16;
            short sum = (short)Math.Clamp(m0 + m1, short.MinValue, short.MaxValue); // saturating add
            short rounded = (short)Math.Clamp(sum + 2, short.MinValue, short.MaxValue);
            return (byte)Math.Clamp(rounded >> 2, 0, 255); // v_rshr_pack_u<2>
        }

        private static int Clip(int v, int n) => v >= 0 ? (v < n ? v : n - 1) : 0;

        private static int SaturateInt(double v) => (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue);

        // saturate_cast<short>(float) = cvRound (round-half-even via cvtss2si).
        private static short SaturateShort(float v) => (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
    }
}
