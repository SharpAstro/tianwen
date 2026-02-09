using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace TianWen.Lib;

public static class ArrayPoolHelper
{
    public static SharedObject<T> Rent<T>(int minimumLength) => new SharedObject<T>(minimumLength);

    public readonly struct SharedObject<T>(int minimumLength) : IDisposable
    {
        private readonly T[] _value = ArrayPool<T>.Shared.Rent(minimumLength);
        private readonly int _length = minimumLength;

        public int Length => _length;

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get => _value[index];
            
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            set => _value[index] = value;
        }

        public T this[Index index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get => index.IsFromEnd ? _value[_length - index.Value] : _value[index];

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            set
            {
                if (index.IsFromEnd)
                {
                    _value[_length - index.Value] = value;
                }
                else
                {
                    _value[index] = value;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Span<T> AsSpan(int start = 0) => _value.AsSpan(start, _length - start);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Span<T> AsSpan(int start, int length) => _value.AsSpan(start, length);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Memory<T> AsMemory(int start = 0) => _value.AsMemory(start, _length - start);

        public void Dispose() => ArrayPool<T>.Shared.Return(_value);

        public static implicit operator Memory<T>(SharedObject<T> sharedObject)
            => sharedObject._value.AsMemory(0, sharedObject._length);

        public static implicit operator ReadOnlyMemory<T>(SharedObject<T> sharedObject)
            => sharedObject._value.AsMemory(0, sharedObject._length);

        public static implicit operator Span<T>(SharedObject<T> sharedObject)
            => sharedObject._value.AsSpan(0, sharedObject._length);

        public static implicit operator ReadOnlySpan<T>(SharedObject<T> sharedObject)
            => sharedObject._value.AsSpan(0, sharedObject._length);
    }
}