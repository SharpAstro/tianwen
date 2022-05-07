using System;
using System.Text;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace Astap.Lib
{
    public struct BitMatrix
    {
        #region Masks
        static readonly int M1 = BitVector32.CreateMask();
        static readonly int M2 = BitVector32.CreateMask(M1);
        static readonly int M3 = BitVector32.CreateMask(M2);
        static readonly int M4 = BitVector32.CreateMask(M3);
        static readonly int M5 = BitVector32.CreateMask(M4);
        static readonly int M6 = BitVector32.CreateMask(M5);
        static readonly int M7 = BitVector32.CreateMask(M6);
        static readonly int M8 = BitVector32.CreateMask(M7);
        static readonly int M9 = BitVector32.CreateMask(M8);
        static readonly int M10 = BitVector32.CreateMask(M9);
        static readonly int M11 = BitVector32.CreateMask(M10);
        static readonly int M12 = BitVector32.CreateMask(M11);
        static readonly int M13 = BitVector32.CreateMask(M12);
        static readonly int M14 = BitVector32.CreateMask(M13);
        static readonly int M15 = BitVector32.CreateMask(M14);
        static readonly int M16 = BitVector32.CreateMask(M15);
        static readonly int M17 = BitVector32.CreateMask(M16);
        static readonly int M18 = BitVector32.CreateMask(M17);
        static readonly int M19 = BitVector32.CreateMask(M18);
        static readonly int M20 = BitVector32.CreateMask(M19);
        static readonly int M21 = BitVector32.CreateMask(M20);
        static readonly int M22 = BitVector32.CreateMask(M21);
        static readonly int M23 = BitVector32.CreateMask(M22);
        static readonly int M24 = BitVector32.CreateMask(M23);
        static readonly int M25 = BitVector32.CreateMask(M24);
        static readonly int M26 = BitVector32.CreateMask(M25);
        static readonly int M27 = BitVector32.CreateMask(M26);
        static readonly int M28 = BitVector32.CreateMask(M27);
        static readonly int M29 = BitVector32.CreateMask(M28);
        static readonly int M30 = BitVector32.CreateMask(M29);
        static readonly int M31 = BitVector32.CreateMask(M30);
        static readonly int M32 = BitVector32.CreateMask(M31);
        #endregion

        const int VECTOR_SIZE = 32;
        const int VECTOR_SIZE_SHIFT = 5;
        const int VECTOR_SIZE_MASK = VECTOR_SIZE - 1;

        static readonly int[] Masks = new int[VECTOR_SIZE]
        {
        M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12, M13, M14, M15, M16,
        M17, M18, M19, M20, M21, M22, M23, M24, M25, M26, M27, M28, M29, M30, M31, M32
        };

        private readonly BitVector32[,] _vectors;
        private readonly int _d1;

        public BitMatrix(int d0, int d1)
        {
            var div = DivRem(_d1 = d1, out var rem);

            _vectors = new BitVector32[d0, div + (rem > 0 ? 1 : 0)];
        }

        public bool this[int d0, int d1]
        {
            get
            {
                if (d1 < 0 || d1 >= _d1)
                {
                    throw new IndexOutOfRangeException();
                }

                var d1div = DivRem(d1, out var rem);
                var mask = Masks[rem];

                return _vectors[d0, d1div][mask];
            }
            set
            {
                if (d1 < 0 || d1 >= _d1)
                {
                    throw new IndexOutOfRangeException();
                }

                var d1div = DivRem(d1, out var rem);
                var mask = Masks[rem];

                _vectors[d0, d1div][mask] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DivRem(int d1, out int rem)
        {
            rem = d1 & VECTOR_SIZE_MASK;
            return d1 >> VECTOR_SIZE_SHIFT;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLength(int dim)
        {
            switch (dim)
            {
                case 0: return _vectors.GetLength(0);
                case 1: return _d1;
                default: throw new ArgumentOutOfRangeException(nameof(dim), dim, "Must be 0 or 1");
            }
        }

        public void Clear()
        {
            for (var i = 0; i < _vectors.GetLength(0); i++)
            {
                for (var j = 0; j < _vectors.GetLength(1); j++)
                {
                    _vectors[i, j] = new BitVector32();
                }
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < _vectors.GetLength(0); i++)
            {
                for (var j = 0; j < _vectors.GetLength(1); j++)
                {
                    if (j > 0)
                    {
                        sb.Append(", ");
                    }

                    var vectorBits = Convert.ToString(_vectors[i, j].Data, 2).PadLeft(VECTOR_SIZE, '0');

                    for (var k = 0; k < VECTOR_SIZE / 8; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append(' ');
                        }
                        sb.Append(vectorBits.Substring(k * 8, 8));
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}