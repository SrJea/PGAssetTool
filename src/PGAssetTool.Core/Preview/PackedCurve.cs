using AssetsTools.NET;

namespace PGAssetTool.Core.Preview;

/// Reads the rotation curves a clip keeps packed into bits.
///
/// 213 of the game's 8,926 weapon clips were saved with Unity's animation compression, which keeps
/// rotations in `m_CompressedRotationCurves` rather than `m_RotationCurves`: times as whole
/// hundredths, each the difference from the one before; each rotation in 32 bits, three of them
/// saying which component is left out and its sign, and the other three in 9, 10 and 10 bits; and a
/// slope per key in however few bits its range needs. Read as if they were not there, those clips
/// played as a model standing still — #15 Hammer's Idle among them.
///
/// One slope a key, not two. Over every packed curve in the game the count of slopes is four times
/// the count of keys without exception, so each key carries one slope for its four components, and
/// a curve through it leaves at the angle it arrived.
public static class PackedCurve
{
    /// The curves of one clip's packed rotations, in the same terms as its plain ones.
    public static IEnumerable<MotionCurve> Rotations(AssetTypeValueField clip)
    {
        var packed = clip["m_CompressedRotationCurves"];
        if (packed.IsDummy) yield break;

        foreach (var curve in packed["Array"].Children)
        {
            var count = (int)curve["m_Times"]["m_NumItems"].AsUInt;
            if (count == 0) continue;

            var steps = Ints(curve["m_Times"], count);
            var values = Quaternions(curve["m_Values"], count);
            var slopes = Floats(curve["m_Slopes"]);

            var keys = new List<MotionKey>(count);
            var time = 0;
            for (var i = 0; i < count; i++)
            {
                time += steps[i];
                var q = values[i];
                var s = slopes.Length >= (i + 1) * 4 ? slopes[(i * 4)..(i * 4 + 4)] : NoSlope;

                keys.Add(new MotionKey(time * 0.01f,
                    q[0], q[1], q[2], q[3],
                    s[0], s[1], s[2], s[3],
                    s[0], s[1], s[2], s[3]));
            }

            yield return new MotionCurve(curve["m_Path"].AsString, MotionChannel.Rotation, keys);
        }
    }

    private static readonly float[] NoSlope = [0, 0, 0, 0];

    private static byte[] Bytes(AssetTypeValueField vector)
    {
        var data = vector["m_Data"];
        if (data.IsDummy) return [];
        var array = data["Array"];
        return array.AsByteArray ?? array.Children.Select(c => (byte)c.AsInt).ToArray();
    }

    /// Bits one after another, lowest first, across byte boundaries.
    private sealed class BitReader(byte[] data)
    {
        private int _index, _bit;

        public uint Read(int width)
        {
            uint value = 0;
            var have = 0;
            while (have < width)
            {
                var b = _index < data.Length ? data[_index] : 0;
                value |= (uint)(b >> _bit) << have;
                var took = Math.Min(width - have, 8 - _bit);
                _bit += took;
                have += took;
                if (_bit == 8) { _index++; _bit = 0; }
            }
            return width >= 32 ? value : value & ((1u << width) - 1);
        }
    }

    /// Unity's PackedIntVector.
    public static int[] Ints(AssetTypeValueField vector, int count)
        => Ints(Bytes(vector), vector["m_BitSize"].AsByte, count);

    public static int[] Ints(byte[] data, int width, int count)
    {
        var reader = new BitReader(data);
        var values = new int[count];
        for (var i = 0; i < count; i++) values[i] = (int)reader.Read(width);
        return values;
    }

    /// Unity's PackedQuatVector: per rotation, which component is missing (two bits) and whether it
    /// is negative (one), then the three others — the one after the missing one in 9 bits, the rest
    /// in 10 — each scaled from [-1, 1]. The missing one is what makes it a unit quaternion.
    public static float[][] Quaternions(AssetTypeValueField vector, int count)
        => Quaternions(Bytes(vector), count);

    public static float[][] Quaternions(byte[] data, int count)
    {
        var reader = new BitReader(data);
        var values = new float[count][];
        for (var i = 0; i < count; i++)
        {
            var flags = reader.Read(3);
            var missing = (int)(flags & 3);
            var q = new float[4];
            var sum = 0f;
            for (var c = 0; c < 4; c++)
            {
                if (c == missing) continue;
                var width = (missing + 1) % 4 == c ? 9 : 10;
                var x = reader.Read(width);
                q[c] = x / (0.5f * ((1 << width) - 1)) - 1;
                sum += q[c] * q[c];
            }
            q[missing] = MathF.Sqrt(MathF.Max(0, 1 - sum));
            if ((flags & 4) != 0) q[missing] = -q[missing];
            values[i] = q;
        }
        return values;
    }

    /// Unity's PackedFloatVector: each value in the same number of bits, spread over a range.
    public static float[] Floats(AssetTypeValueField vector)
        => Floats(Bytes(vector), vector["m_BitSize"].AsByte, vector["m_Start"].AsFloat,
            vector["m_Range"].AsFloat, (int)vector["m_NumItems"].AsUInt);

    public static float[] Floats(byte[] data, int width, float start, float range, int count)
    {
        var values = new float[count];

        // Nothing to read: every value is where the range starts. Dividing by a width of zero would
        // make them all not-a-number instead.
        if (width == 0) { Array.Fill(values, start); return values; }

        var reader = new BitReader(data);
        var top = (float)((1L << width) - 1);
        for (var i = 0; i < count; i++) values[i] = reader.Read(width) / top * range + start;
        return values;
    }
}
