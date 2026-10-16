// 1.0.2 net48 语法 polyfill：让同一套源码里的 C#8+ 特性（范围运算符/元组解构 foreach）在 .NET Framework 上编译。
// net10 编译时本文件整体跳过（BCL 原生自带）。
#if NETFRAMEWORK
using System;

namespace System
{
    public readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        // 编译器对 s[1..^1] 直接 new Index(1)/new Index(-1)，构造必须 public
        public Index(int value) { _value = value; }   // 负数=从尾计
        public static Index Start => new Index(0);
        public static Index End => new Index(-1);
        public static Index FromStart(int value) => value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : new Index(value);
        public static Index FromEnd(int value) => value <= 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : new Index(-value);
        // Roslyn 对 s[expr..] 的 int 表达式走隐式转换（字面量才直连 ctor），必须提供
        public static implicit operator Index(int value) => new Index(value);
        public int Value => _value < 0 ? -_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => _value < 0 ? length + _value : _value;
        public Index Offset(int by) => _value < 0 ? new Index(_value - by) : new Index(_value + by);
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object obj) => obj is Index o && Equals(o);
        public override int GetHashCode() => _value;
        public override string ToString() => _value < 0 ? "^" + -_value : _value.ToString();
    }

    public readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range All => new Range(Index.Start, Index.End);
        public static Range FromStart(Index start) => new Range(start, Index.End);
        public static Range FromEnd(Index end) => new Range(Index.Start, end);
        public Range WithStart(Index start) => new Range(start, End);
        public Range WithEnd(Index end) => new Range(Start, end);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object obj) => obj is Range r && Equals(r);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => Start + ".." + End;
    }

    public static class KeyValuePairExtensions
    {
        public static void Deconstruct<K, V>(this KeyValuePair<K, V> kvp, out K key, out V value)
        {
            key = kvp.Key;
            value = kvp.Value;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    public static class RuntimeHelpersExtensions
    {
        // 编译器对 array[range] 调用的钩子（扩展方法形式即可被找到）
        public static T[] GetSubArray<T>(this T[] array, Range range)
        {
            var (offset, length) = range.GetOffsetAndLength(array.Length);
            var dest = new T[length];
            Array.Copy(array, offset, dest, 0, length);
            return dest;
        }
    }
}
#endif
