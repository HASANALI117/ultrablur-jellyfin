namespace UltraBlur.Plugin.Algorithm
{
    /// <summary>
    /// The libc++ std::sort PMS is built with (pre-LLVM 14 __sort: insertion
    /// limit 6, median-of-3 partition, __insertion_sort_incomplete on a perfect
    /// partition), transliterated from its instantiations FUN_011b2464 (box
    /// queue) and FUN_011b40e7 (clusters). Tie order matters: the box queue
    /// sorts many equal-count / zero-volume boxes, and which one is popped or
    /// where it lands decides cluster ordinals and therefore rank scores.
    /// Array.Sort (introsort) and newer libc++ order ties differently.
    /// </summary>
    internal static class LibCxx
    {
        public static void Sort<T>(Span<T> a, Func<T, T, bool> c)
        {
            int first = 0, last = a.Length;
            while (true)
            {
            restart:
                int len = last - first;
                switch (len)
                {
                    case 0:
                    case 1:
                        return;
                    case 2:
                        if (c(a[last - 1], a[first]))
                        {
                            Swap(a, first, last - 1);
                        }

                        return;
                    case 3:
                        Sort3(a, first, first + 1, last - 1, c);
                        return;
                    case 4:
                        Sort4(a, first, first + 1, first + 2, last - 1, c);
                        return;
                    case 5:
                        Sort5(a, first, first + 1, first + 2, first + 3, last - 1, c);
                        return;
                }

                if (len <= 6)
                {
                    InsertionSort3(a, first, last, c);
                    return;
                }

                int m = first;
                int lm1 = last - 1;
                uint nSwaps;
                int delta = len / 2;
                m += delta;
                if (len >= 1000)
                {
                    delta /= 2;
                    nSwaps = Sort5(a, first, first + delta, m, m + delta, lm1, c);
                }
                else
                {
                    nSwaps = Sort3(a, first, m, lm1, c);
                }

                // Partition [first, m) < *m <= [m, last).
                int i = first;
                int j = lm1;
                if (!c(a[i], a[m]))
                {
                    // *first == *m: guard the downward search manually.
                    while (true)
                    {
                        if (i == --j)
                        {
                            // *first == *m <= everything: partition into
                            // [first, i) == *first < [i, last).
                            ++i;
                            j = last;
                            if (!c(a[first], a[--j]))
                            {
                                while (true)
                                {
                                    if (i == j)
                                    {
                                        return;
                                    }

                                    if (c(a[first], a[i]))
                                    {
                                        Swap(a, i, j);
                                        ++nSwaps;
                                        ++i;
                                        break;
                                    }

                                    ++i;
                                }
                            }

                            if (i == j)
                            {
                                return;
                            }

                            while (true)
                            {
                                while (!c(a[first], a[i]))
                                {
                                    ++i;
                                }

                                while (c(a[first], a[--j]))
                                {
                                }

                                if (i >= j)
                                {
                                    break;
                                }

                                Swap(a, i, j);
                                ++nSwaps;
                                ++i;
                            }

                            first = i;
                            goto restart;
                        }

                        if (c(a[j], a[m]))
                        {
                            Swap(a, i, j);
                            ++nSwaps;
                            break;
                        }
                    }
                }

                ++i;
                if (i < j)
                {
                    while (true)
                    {
                        while (c(a[i], a[m]))
                        {
                            ++i;
                        }

                        while (!c(a[--j], a[m]))
                        {
                        }

                        if (i > j)
                        {
                            break;
                        }

                        Swap(a, i, j);
                        ++nSwaps;
                        if (m == i)
                        {
                            m = j;
                        }

                        ++i;
                    }
                }

                if (i != m && c(a[m], a[i]))
                {
                    Swap(a, i, m);
                    ++nSwaps;
                }

                if (nSwaps == 0)
                {
                    bool fs = InsertionSortIncomplete(a, first, i, c);
                    if (InsertionSortIncomplete(a, i + 1, last, c))
                    {
                        if (fs)
                        {
                            return;
                        }

                        last = i;
                        continue;
                    }

                    if (fs)
                    {
                        first = ++i;
                        continue;
                    }
                }

                // Recurse into the smaller side, loop on the larger.
                if (i - first < last - i)
                {
                    Sort(a.Slice(first, i - first), c);
                    first = ++i;
                }
                else
                {
                    Sort(a.Slice(i + 1, last - i - 1), c);
                    last = i;
                }
            }
        }

        private static void Swap<T>(Span<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

        private static uint Sort3<T>(Span<T> a, int x, int y, int z, Func<T, T, bool> c)
        {
            if (!c(a[y], a[x]))
            {
                if (!c(a[z], a[y]))
                {
                    return 0;
                }

                Swap(a, y, z);
                if (c(a[y], a[x]))
                {
                    Swap(a, x, y);
                    return 2;
                }

                return 1;
            }

            if (c(a[z], a[y]))
            {
                Swap(a, x, z);
                return 1;
            }

            Swap(a, x, y);
            if (c(a[z], a[y]))
            {
                Swap(a, y, z);
                return 2;
            }

            return 1;
        }

        private static uint Sort4<T>(Span<T> a, int x1, int x2, int x3, int x4, Func<T, T, bool> c)
        {
            uint r = Sort3(a, x1, x2, x3, c);
            if (c(a[x4], a[x3]))
            {
                Swap(a, x3, x4);
                ++r;
                if (c(a[x3], a[x2]))
                {
                    Swap(a, x2, x3);
                    ++r;
                    if (c(a[x2], a[x1]))
                    {
                        Swap(a, x1, x2);
                        ++r;
                    }
                }
            }

            return r;
        }

        private static uint Sort5<T>(Span<T> a, int x1, int x2, int x3, int x4, int x5, Func<T, T, bool> c)
        {
            uint r = Sort4(a, x1, x2, x3, x4, c);
            if (c(a[x5], a[x4]))
            {
                Swap(a, x4, x5);
                ++r;
                if (c(a[x4], a[x3]))
                {
                    Swap(a, x3, x4);
                    ++r;
                    if (c(a[x3], a[x2]))
                    {
                        Swap(a, x2, x3);
                        ++r;
                        if (c(a[x2], a[x1]))
                        {
                            Swap(a, x1, x2);
                            ++r;
                        }
                    }
                }
            }

            return r;
        }

        // Shift a[i] left into the sorted run ending at j = i - 1.
        private static void Insert<T>(Span<T> a, int first, int i, Func<T, T, bool> c)
        {
            T t = a[i];
            int k = i - 1;
            int j = i;
            do
            {
                a[j] = a[k];
                j = k;
            }
            while (j != first && c(t, a[--k]));
            a[j] = t;
        }

        private static void InsertionSort3<T>(Span<T> a, int first, int last, Func<T, T, bool> c)
        {
            Sort3(a, first, first + 1, first + 2, c);
            for (int i = first + 3; i != last; ++i)
            {
                if (c(a[i], a[i - 1]))
                {
                    Insert(a, first, i, c);
                }
            }
        }

        private static bool InsertionSortIncomplete<T>(Span<T> a, int first, int last, Func<T, T, bool> c)
        {
            switch (last - first)
            {
                case 0:
                case 1:
                    return true;
                case 2:
                    if (c(a[last - 1], a[first]))
                    {
                        Swap(a, first, last - 1);
                    }

                    return true;
                case 3:
                    Sort3(a, first, first + 1, last - 1, c);
                    return true;
                case 4:
                    Sort4(a, first, first + 1, first + 2, last - 1, c);
                    return true;
                case 5:
                    Sort5(a, first, first + 1, first + 2, first + 3, last - 1, c);
                    return true;
            }

            Sort3(a, first, first + 1, first + 2, c);
            int count = 0;
            for (int i = first + 3; i != last; ++i)
            {
                if (c(a[i], a[i - 1]))
                {
                    Insert(a, first, i, c);
                    if (++count == 8)
                    {
                        return i + 1 == last;
                    }
                }
            }

            return true;
        }
    }
}
