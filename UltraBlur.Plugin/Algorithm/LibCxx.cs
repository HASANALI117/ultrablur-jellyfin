namespace UltraBlur.Plugin.Algorithm
{
    /// <summary>
    /// libc++ (LLVM 18, as bundled by zig 0.13) std::sort / heap algorithms,
    /// transliterated so tie-breaking between equal boxes/clusters is identical
    /// to the bit-exact ultrablur_cli. Array.Sort is introsort with different
    /// tie order and would reorder equal-weight boxes, which shifts ordinals and
    /// therefore rank scores. Sources: pms/RE/ref-src/libcxx.
    /// </summary>
    internal static class LibCxx
    {
        /// <summary>
        /// std::sort for ranges shorter than libc++'s insertion-sort limit (24).
        /// </summary>
        public static void Sort<T>(Span<T> a, Func<T, T, bool> less)
        {
            int len = a.Length;
            switch (len)
            {
                case 0:
                case 1:
                    return;
                case 2:
                    if (less(a[1], a[0]))
                    {
                        Swap(a, 0, 1);
                    }

                    return;
                case 3:
                    Sort3(a, 0, 1, 2, less);
                    return;
                case 4:
                    Sort4(a, 0, 1, 2, 3, less);
                    return;
                case 5:
                    Sort5(a, 0, 1, 2, 3, 4, less);
                    return;
            }

            // ponytail: introsort partitioning not ported; UltraBlur never sorts
            // more than ~10 boxes/clusters. Port __introsort's partition if needed.
            if (len >= 24)
            {
                throw new ArgumentOutOfRangeException(nameof(a), len, "libc++ introsort partition path not ported");
            }

            InsertionSort(a, less);
        }

        public static void MakeHeap<T>(Span<T> a, Func<T, T, bool> less)
        {
            int n = a.Length;
            if (n > 1)
            {
                for (int start = (n - 2) / 2; start >= 0; --start)
                {
                    SiftDown(a, less, n, start);
                }
            }
        }

        /// <summary>std::push_heap: the new element is already at a[^1].</summary>
        public static void PushHeap<T>(Span<T> a, Func<T, T, bool> less) => SiftUp(a, less, a.Length);

        /// <summary>std::pop_heap: moves the top to a[^1].</summary>
        public static void PopHeap<T>(Span<T> a, Func<T, T, bool> less)
        {
            int len = a.Length;
            if (len > 1)
            {
                T top = a[0];
                int hole = FloydSiftDown(a, less, len);
                int last = len - 1;
                if (hole == last)
                {
                    a[hole] = top;
                }
                else
                {
                    a[hole] = a[last];
                    ++hole;
                    a[last] = top;
                    SiftUp(a, less, hole);
                }
            }
        }

        private static void Swap<T>(Span<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

        private static void Sort3<T>(Span<T> a, int x, int y, int z, Func<T, T, bool> c)
        {
            if (!c(a[y], a[x]))
            {
                if (!c(a[z], a[y]))
                {
                    return;
                }

                Swap(a, y, z);
                if (c(a[y], a[x]))
                {
                    Swap(a, x, y);
                }

                return;
            }

            if (c(a[z], a[y]))
            {
                Swap(a, x, z);
                return;
            }

            Swap(a, x, y);
            if (c(a[z], a[y]))
            {
                Swap(a, y, z);
            }
        }

        private static void Sort4<T>(Span<T> a, int x1, int x2, int x3, int x4, Func<T, T, bool> c)
        {
            Sort3(a, x1, x2, x3, c);
            if (c(a[x4], a[x3]))
            {
                Swap(a, x3, x4);
                if (c(a[x3], a[x2]))
                {
                    Swap(a, x2, x3);
                    if (c(a[x2], a[x1]))
                    {
                        Swap(a, x1, x2);
                    }
                }
            }
        }

        private static void Sort5<T>(Span<T> a, int x1, int x2, int x3, int x4, int x5, Func<T, T, bool> c)
        {
            Sort4(a, x1, x2, x3, x4, c);
            if (c(a[x5], a[x4]))
            {
                Swap(a, x4, x5);
                if (c(a[x4], a[x3]))
                {
                    Swap(a, x3, x4);
                    if (c(a[x3], a[x2]))
                    {
                        Swap(a, x2, x3);
                        if (c(a[x2], a[x1]))
                        {
                            Swap(a, x1, x2);
                        }
                    }
                }
            }
        }

        private static void InsertionSort<T>(Span<T> a, Func<T, T, bool> c)
        {
            for (int i = 1; i < a.Length; ++i)
            {
                int j = i - 1;
                if (c(a[i], a[j]))
                {
                    T t = a[i];
                    int k = j;
                    j = i;
                    do
                    {
                        a[j] = a[k];
                        j = k;
                    }
                    while (j != 0 && c(t, a[--k]));
                    a[j] = t;
                }
            }
        }

        private static void SiftDown<T>(Span<T> a, Func<T, T, bool> c, int len, int start)
        {
            int child = start;
            if (len < 2 || (len - 2) / 2 < child)
            {
                return;
            }

            child = (2 * child) + 1;
            if (child + 1 < len && c(a[child], a[child + 1]))
            {
                ++child;
            }

            if (c(a[child], a[start]))
            {
                return;
            }

            T top = a[start];
            do
            {
                a[start] = a[child];
                start = child;
                if ((len - 2) / 2 < child)
                {
                    break;
                }

                child = (2 * child) + 1;
                if (child + 1 < len && c(a[child], a[child + 1]))
                {
                    ++child;
                }
            }
            while (!c(a[child], top));
            a[start] = top;
        }

        private static int FloydSiftDown<T>(Span<T> a, Func<T, T, bool> c, int len)
        {
            int hole = 0;
            int childI = 0;
            int child = 0;
            while (true)
            {
                childI += child + 1;
                child = (2 * child) + 1;
                if (child + 1 < len && c(a[childI], a[childI + 1]))
                {
                    ++childI;
                    ++child;
                }

                a[hole] = a[childI];
                hole = childI;
                if (child > (len - 2) / 2)
                {
                    return hole;
                }
            }
        }

        /// <summary>__sift_up over [0, len): the element at len-1 bubbles up.</summary>
        private static void SiftUp<T>(Span<T> a, Func<T, T, bool> c, int len)
        {
            if (len > 1)
            {
                int last = len - 1;
                len = (len - 2) / 2;
                int ptr = len;
                if (c(a[ptr], a[last]))
                {
                    T t = a[last];
                    do
                    {
                        a[last] = a[ptr];
                        last = ptr;
                        if (len == 0)
                        {
                            break;
                        }

                        len = (len - 1) / 2;
                        ptr = len;
                    }
                    while (c(a[ptr], t));
                    a[last] = t;
                }
            }
        }
    }
}
