// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Shared;

#pragma warning disable S1210
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
#pragma warning restore S1210
{
    private static readonly Unit s_value = new();

    public static ref readonly Unit Value => ref s_value;

    public static ValueTask<Unit> TaskValue => new(s_value);

    public int CompareTo(Unit other) => 0;

    int IComparable.CompareTo(object? obj) => 0;

    public override int GetHashCode() => 0;

    public bool Equals(Unit other) => true;

    public override bool Equals(object? obj) => obj is Unit;

    public static bool operator ==(Unit _, Unit __) => true;

    public static bool operator !=(Unit _, Unit __) => false;

    public override string ToString() => "()";
}
