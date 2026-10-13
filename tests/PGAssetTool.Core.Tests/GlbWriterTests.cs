using System.Text.Json;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Tests;

public class GlbWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pgassettool-glb").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// One triangle, enough to check framing, handedness and winding without a game installed.
    private static UnityMesh Triangle(bool skinned = false) => new()
    {
        Name = "triangle",
        VertexCount = 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = [0, 0, 1, 1, 0, 2, 0, 1, 3],
            [VertexAttribute.Normal] = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            [VertexAttribute.TexCoord0] = [0, 0, 1, 0, 0, 1],
            [VertexAttribute.BlendIndices] = skinned ? [0, 1, 0] : [],
        }.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.Normal] = 3,
            [VertexAttribute.TexCoord0] = 2,
            [VertexAttribute.BlendIndices] = 1,
        },
        Indices = [0, 1, 2],
        SubMeshes = [new SubMesh(0, 3, Topology: 0, BaseVertex: 0)],
        BindPoses = skinned ? [Identity(), Identity()] : [],
        BoneNameHashes = skinned ? [111u, 222u] : [],
    };

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private (JsonElement Json, byte[] Binary) WriteAndRead(
        UnityMesh mesh, IReadOnlyList<float[]>? restPoses = null)
    {
        var path = Path.Combine(_directory, "out.glb");
        GlbWriter.Write(mesh, path, restPoses);
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));

        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength)).RootElement;
        var binaryStart = 20 + jsonLength + 8;
        return (json, bytes[binaryStart..]);
    }

    private static float[] Floats(JsonElement json, byte[] binary, int accessor, int components)
    {
        var a = json.GetProperty("accessors")[accessor];
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        var count = a.GetProperty("count").GetInt32() * components;
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = BitConverter.ToSingle(binary, offset + i * 4);
        return values;
    }

    [Fact]
    public void TheContainerIsWellFormed()
    {
        var (json, _) = WriteAndRead(Triangle());
        Assert.Equal("2.0", json.GetProperty("asset").GetProperty("version").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoBufferViewReachesPastTheDeclaredBufferLength(bool skinned)
    {
        // The skin appends to the buffer after the mesh data. Declaring the length before that ran
        // left the inverse bind matrices outside it, and a reader is entitled to refuse them.
        var (json, _) = WriteAndRead(Triangle(skinned));
        var declared = json.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32();

        foreach (var view in json.GetProperty("bufferViews").EnumerateArray())
        {
            var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            Assert.True(offset + view.GetProperty("byteLength").GetInt32() <= declared);
        }
    }

    [Fact]
    public void BindMatricesComeOutColumnMajorAndUnmirrored()
    {
        // Unity names these eRC and stores them row by row; glTF reads them column by column, so
        // passing them through unchanged transposes every one. A basis change that mirrored instead
        // of rotating would show up as a negative determinant.
        var mesh = Triangle(skinned: true) with { };
        var (json, binary) = WriteAndRead(mesh);
        var accessor = json.GetProperty("skins")[0].GetProperty("inverseBindMatrices").GetInt32();
        var m = Floats(json, binary, accessor, 16);

        Assert.Equal(1f, m[15]);
        var determinant =
            m[0] * (m[5] * m[10] - m[6] * m[9])
            - m[4] * (m[1] * m[10] - m[2] * m[9])
            + m[8] * (m[1] * m[6] - m[2] * m[5]);
        Assert.Equal(1f, determinant, 4);
    }

    [Fact]
    public void AtRestTheJointCancelsItsInverseBindMatrix()
    {
        // The whole point of the pair: at the bind pose the deform must come out as identity, or the
        // mesh is right until the armature is switched on and wrong after. A joint left without a
        // transform sits at the origin and leaves the inverse bind matrix applied to every vertex.
        var pose = new float[]
        {
            0, -1, 0, 0.5f,
            1, 0, 0, -0.25f,
            0, 0, 1, 2,
            0, 0, 0, 1,
        };
        var (json, binary) = WriteAndRead(Triangle(skinned: true) with { BindPoses = [pose, pose] });

        var skin = json.GetProperty("skins")[0];
        var inverseBind = Floats(json, binary, skin.GetProperty("inverseBindMatrices").GetInt32(), 16);

        foreach (var (jointIndex, slot) in skin.GetProperty("joints").EnumerateArray().Select((j, i) => (j.GetInt32(), i)))
        {
            var placement = json.GetProperty("nodes")[jointIndex].GetProperty("matrix")
                .EnumerateArray().Select(v => v.GetSingle()).ToArray();

            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                {
                    float sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += placement[k * 4 + row] * inverseBind[slot * 16 + column * 4 + k];
                    Assert.Equal(row == column ? 1f : 0f, sum, 4);
                }
        }
    }

    /// Told where the bones actually are, the joints go there instead — so switching the armature on
    /// assembles the model rather than leaving it as written.
    ///
    /// This is the difference between a weapon and a pile of its parts for 518 of the game's 1517
    /// weapons, whose bones do not all carry the same skinning matrix at rest. The two bones here
    /// are a metre apart, which is that case in miniature: with the pair cancelling, both halves of
    /// the triangle stay where they were written; with the rest poses given, the half carried by
    /// the second bone moves by the metre between them.
    [Fact]
    public void WithTheBonesWhereTheyReallyAreTheArmatureAssemblesTheModel()
    {
        var mesh = Triangle(skinned: true);

        // Column major, as the rest of the tool keeps them: the second bone a metre along x.
        float[] here = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        float[] along = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1];

        var (json, binary) = WriteAndRead(mesh, [here, along]);
        var skin = json.GetProperty("skins")[0];
        var inverseBind = Floats(json, binary, skin.GetProperty("inverseBindMatrices").GetInt32(), 16);
        var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToList();

        float[] Placement(int slot) => json.GetProperty("nodes")[joints[slot]].GetProperty("matrix")
            .EnumerateArray().Select(v => v.GetSingle()).ToArray();

        // The bind poses are identities, so each joint's whole deform is where the bone sits.
        Assert.Equal(0f, Deform(Placement(0), inverseBind, 0, 12), 4);
        Assert.Equal(1f, Deform(Placement(1), inverseBind, 1, 12), 4);

        // And without them, the pair still cancels — every mesh that was right stays right.
        var (plain, plainBinary) = WriteAndRead(mesh);
        var plainBind = Floats(plain, plainBinary, plain.GetProperty("skins")[0]
            .GetProperty("inverseBindMatrices").GetInt32(), 16);
        var plainJoints = plain.GetProperty("skins")[0].GetProperty("joints")
            .EnumerateArray().Select(j => j.GetInt32()).ToList();

        for (var slot = 0; slot < plainJoints.Count; slot++)
        {
            var placement = plain.GetProperty("nodes")[plainJoints[slot]].GetProperty("matrix")
                .EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.Equal(0f, Deform(placement, plainBind, slot, 12), 4);
        }
    }

    /// One cell of (joint matrix × inverse bind matrix), which is what a glTF reader deforms with.
    private static float Deform(float[] placement, float[] inverseBind, int slot, int cell)
    {
        var (column, row) = (cell / 4, cell % 4);
        float sum = 0;
        for (var k = 0; k < 4; k++) sum += placement[k * 4 + row] * inverseBind[slot * 16 + column * 4 + k];
        return sum;
    }

    [Fact]
    public void ATranslationInTheBindPoseKeepsItsPlaceAndFlipsZ()
    {
        var mesh = Triangle(skinned: true);
        // Row-major e00..e33 with the translation in the last column: x=1, y=2, z=3.
        var pose = new float[] { 1, 0, 0, 1, 0, 1, 0, 2, 0, 0, 1, 3, 0, 0, 0, 1 };
        var (json, binary) = WriteAndRead(mesh with { BindPoses = [pose, pose] });

        var m = Floats(json, binary, json.GetProperty("skins")[0].GetProperty("inverseBindMatrices").GetInt32(), 16);
        Assert.Equal([1f, 2f, -3f], [m[12], m[13], m[14]]);
    }

    [Fact]
    public void ZIsNegatedToTurnUnitysLeftHandedSpaceIntoGltfsRightHanded()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("POSITION").GetInt32();

        // Source z values were 1, 2, 3.
        var positions = Floats(json, binary, accessor, 3);
        Assert.Equal([-1f, -2f, -3f], [positions[2], positions[5], positions[8]]);
    }

    [Fact]
    public void WindingIsReversedSoFacesKeepFacingOutward()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var primitive = json.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var a = json.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()];
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;

        var indices = Enumerable.Range(0, 3).Select(i => BitConverter.ToUInt16(binary, offset + i * 2)).ToArray();
        Assert.Equal([(ushort)2, (ushort)1, (ushort)0], indices);
    }

    [Fact]
    public void TheTextureOriginIsMovedToTheTopLeft()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32();

        // Source v values were 0, 0, 1.
        var uv = Floats(json, binary, accessor, 2);
        Assert.Equal([1f, 1f, 0f], [uv[1], uv[3], uv[5]]);
    }

    [Fact]
    public void PositionCarriesTheBoundsGltfRequires()
    {
        var (json, _) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("accessors")[
            json.GetProperty("meshes")[0].GetProperty("primitives")[0]
                .GetProperty("attributes").GetProperty("POSITION").GetInt32()];

        Assert.Equal([0f, 0f, -3f], accessor.GetProperty("min").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal([1f, 1f, -1f], accessor.GetProperty("max").EnumerateArray().Select(v => v.GetSingle()));
    }

    [Fact]
    public void ASingleBoneBindGetsTheFourWidePairGltfExpects()
    {
        // Most meshes here bind one bone per vertex and carry no weight channel at all.
        var (json, binary) = WriteAndRead(Triangle(skinned: true));
        var attributes = json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty("JOINTS_0", out _));
        var weights = Floats(json, binary, attributes.GetProperty("WEIGHTS_0").GetInt32(), 4);
        Assert.Equal([1f, 0f, 0f, 0f], weights[..4]);
        Assert.Equal(2, json.GetProperty("skins")[0].GetProperty("joints").GetArrayLength());
    }

    [Fact]
    public void AnUnskinnedMeshHasNoSkin()
    {
        var (json, _) = WriteAndRead(Triangle());
        Assert.False(json.TryGetProperty("skins", out _));
    }

    [Fact]
    public void ANormalThatIsNotANumberStillWritesAFile()
    {
        // avatar_gunslinger's body ships two of these. Bounding the normals as well as the positions
        // put a NaN into the JSON, which cannot hold one, and the whole extract failed over it.
        var mesh = Triangle();
        mesh.Attributes[VertexAttribute.Normal][3] = float.NaN;

        var (json, binary) = WriteAndRead(mesh);
        var attributes = json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");

        var normals = Floats(json, binary, attributes.GetProperty("NORMAL").GetInt32(), 3);
        Assert.All(normals, n => Assert.True(float.IsFinite(n)));
        Assert.Equal([0f, 1f, 0f], normals[3..6]);

        var accessor = json.GetProperty("accessors")[attributes.GetProperty("NORMAL").GetInt32()];
        Assert.False(accessor.TryGetProperty("min", out _));
    }

    [Fact]
    public void SubmeshesSharingAVertexBufferComeBackSharingIt()
    {
        // Every submesh of a Unity mesh indexes the one vertex buffer, and that is how it is written
        // out. Read back a primitive at a time, each submesh brought its own copy of the vertices:
        // #1200's Space Dessert went back into the game with twice the vertices it came out with.
        var mesh = Triangle() with
        {
            VertexCount = 4,
            Attributes = new Dictionary<VertexAttribute, float[]>
            {
                [VertexAttribute.Position] = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0],
                [VertexAttribute.Normal] = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
                [VertexAttribute.TexCoord0] = [0, 0, 1, 0, 0, 1, 1, 1],
            },
            Indices = [0, 1, 2, 1, 3, 2],
            SubMeshes = [new SubMesh(0, 3, Topology: 0, BaseVertex: 0), new SubMesh(3, 3, Topology: 0, BaseVertex: 0)],
        };

        var path = Path.Combine(_directory, "two.glb");
        GlbWriter.Write(mesh, path);
        var read = Import.Meshes.GltfMeshReader.Read(path);

        Assert.Equal(4, read.VertexCount);
        Assert.Equal(2, read.SubMeshes.Count);
        Assert.Equal(12, read.Attributes[VertexAttribute.Position].Length);

        // Both triangles still land on the vertices they started on, in either winding.
        var triangles = read.Indices.Chunk(3).Select(t => t.Order().ToArray()).ToList();
        Assert.Equal([0, 1, 2], triangles[0]);
        Assert.Equal([1, 2, 3], triangles[1]);
    }
}
