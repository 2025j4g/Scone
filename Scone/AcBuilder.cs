using System.Globalization;
using System.Numerics;
using Newtonsoft.Json.Linq;
using SharpGLTF.Schema2;

namespace Scone;

public class AcBuilder
{
	public string Header { get; private set; } = "AC3Db";
	public List<Material> Materials { get; private set; } = [];
	public List<Object> Objects { get; private set; } = [];
	public HashSet<string> TextureFiles { get; private set; } = [];

	public AcBuilder() { }

	public int AddMaterial(Material mat)
	{
		Materials.Add(mat);
		return Materials.Count - 1;
	}

	public World AddWorld(string name = "")
	{
		World world = new(name);
		Objects.Add(world);
		return world;
	}

	public Group AddGroup(string name = "")
	{
		Group group = new(name);
		Objects.Add(group);
		return group;
	}

	public Poly AddPoly(string name = "")
	{
		Poly poly = new(name);
		Objects.Add(poly);
		return poly;
	}

	public void Merge(AcBuilder other, Matrix4x4? transform = null, string? name = null)
	{
		// Track the material index offset for updating surface references
		int materialOffset = Materials.Count;

		// Merge materials
		Materials.AddRange(other.Materials);

		// Merge textures
		TextureFiles.UnionWith(other.TextureFiles);

		Vector3? scale = null;

		// If we have a transform, wrap the other scene's objects in a Group
		if (transform.HasValue && transform.Value != Matrix4x4.Identity)
		{
			// Decompose the transform matrix into rotation, scale, and translation
			Matrix4x4.Decompose(transform.Value, out Vector3 scaleVec, out Quaternion rotation, out Vector3 translation);

			// Check if scale is valid and non-uniform
			if (float.IsFinite(scaleVec.X) && float.IsFinite(scaleVec.Y) && float.IsFinite(scaleVec.Z) &&
				scaleVec != Vector3.One)
			{
				scale = scaleVec;
			}

			// Create rotation matrix from quaternion
			Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(rotation);

			Group wrapper = new(name ?? "merged")
			{
				Rotation = rotationMatrix,
				Location = translation
			};

			// Clone and add objects as children of the wrapper, applying scale to vertices
			foreach (var obj in other.Objects)
			{
				wrapper.AddChild(CloneObject(obj, materialOffset, scale));
			}

			Objects.Add(wrapper);
		}
		else
		{
			// No transform needed, just clone and add objects directly
			foreach (var obj in other.Objects)
			{
				Objects.Add(CloneObject(obj, materialOffset, null));
			}
		}
	}

	private static Object CloneObject(Object source, int materialOffset, Vector3? scale = null)
	{
		return source switch
		{
			World world => CloneWorld(world, materialOffset, scale),
			Group group => CloneGroup(group, materialOffset, scale),
			Poly poly => ClonePoly(poly, materialOffset, scale),
			_ => throw new NotSupportedException($"Unknown object type: {source.GetType()}")
		};
	}

	private static World CloneWorld(World source, int materialOffset, Vector3? scale = null)
	{
		World clone = new(source.Name) { Url = source.Url };
		foreach (var kid in source.Kids)
		{
			clone.AddChild(CloneObject(kid, materialOffset, scale));
		}
		return clone;
	}

	private static Group CloneGroup(Group source, int materialOffset, Vector3? scale = null)
	{
		Group clone = new(source.Name)
		{
			Url = source.Url,
			Rotation = source.Rotation,
			Location = source.Location
		};
		foreach (var kid in source.Kids)
		{
			clone.AddChild(CloneObject(kid, materialOffset, scale));
		}
		return clone;
	}

	private static Poly ClonePoly(Poly source, int materialOffset, Vector3? scale = null)
	{
		Poly clone = new(source.Name)
		{
			Url = source.Url,
			Rotation = source.Rotation,
			Location = source.Location,
			Texture = source.Texture,
			TexRep = source.TexRep
		};

		// Clone vertices and apply scale if provided
		if (scale.HasValue)
		{
			foreach (var vertex in source.Vertices)
			{
				clone.Vertices.Add(vertex * scale.Value);
			}
		}
		else
		{
			clone.Vertices.AddRange(source.Vertices);
		}

		// Clone surfaces and update material indices
		foreach (var surf in source.Surfaces)
		{
			Surface clonedSurf = new(surf.Flags)
			{
				MaterialIndex = surf.MaterialIndex.HasValue
					? surf.MaterialIndex.Value + materialOffset
					: null
			};
			clonedSurf.Refs.AddRange(surf.Refs);
			clone.Surfaces.Add(clonedSurf);
		}

		// Clone children
		foreach (var kid in source.Kids)
		{
			clone.AddChild(CloneObject(kid, materialOffset, scale));
		}

		return clone;
	}

	public abstract class Object
	{
		public List<Object> Kids { get; private set; } = [];
		public string Name { get; set; } = "";
		public string? Url { get; set; }

		protected Object() { }
		protected Object(string name)
		{
			Name = name;
		}

		public void AddChild(Object child)
		{
			Kids.Add(child);
		}

		public void WriteTo(StreamWriter writer)
		{
			string type = GetType().Name.ToLower();
			writer.WriteLine($"OBJECT {type}");

			if (!string.IsNullOrEmpty(Name))
			{
				writer.WriteLine($"name \"{Name}\"");
			}

			if (!string.IsNullOrEmpty(Url))
			{
				writer.WriteLine($"url \"{Url}\"");
			}

			WriteSpecificData(writer);

			writer.WriteLine($"kids {Kids.Count}");

			foreach (Object kid in Kids)
			{
				kid.WriteTo(writer);
			}
		}

		protected abstract void WriteSpecificData(StreamWriter writer);
	}

	public class World : Object
	{
		public World() : base() { }
		public World(string name) : base(name) { }

		protected override void WriteSpecificData(StreamWriter writer)
		{
			// World objects typically have no specific data
		}
	}

	public class Group : Object
	{
		public Matrix4x4? Rotation { get; set; }
		public Vector3? Location { get; set; }

		public Group() : base() { }
		public Group(string name) : base(name) { }

		protected override void WriteSpecificData(StreamWriter writer)
		{
			if (Rotation.HasValue && Rotation.Value != Matrix4x4.Identity)
			{
				Matrix4x4 rot = Rotation.Value;
				writer.WriteLine($"rot {F(rot.M11)} {F(rot.M12)} {F(rot.M13)}  {F(rot.M21)} {F(rot.M22)} {F(rot.M23)}  {F(rot.M31)} {F(rot.M32)} {F(rot.M33)}");
			}

			if (Location.HasValue && Location.Value != Vector3.Zero)
			{
				writer.WriteLine($"loc {F(Location.Value.X)} {F(Location.Value.Y)} {F(Location.Value.Z)}");
			}
		}
	}

	public class Poly : Object
	{
		public Matrix4x4? Rotation { get; set; }
		public Vector3? Location { get; set; }
		public string? Texture { get; set; }
		public Vector2? TexRep { get; set; }
		public List<Vector3> Vertices { get; private set; } = [];
		public List<Surface> Surfaces { get; private set; } = [];

		public Poly() : base() { }
		public Poly(string name) : base(name) { }

		public int AddVertex(Vector3 vertex)
		{
			Vertices.Add(vertex);
			return Vertices.Count - 1;
		}

		public Surface AddSurface(int flags = 0x20) // 0x20 = shaded polygon
		{
			Surface surf = new(flags);
			Surfaces.Add(surf);
			return surf;
		}

		protected override void WriteSpecificData(StreamWriter writer)
		{
			if (!string.IsNullOrEmpty(Texture))
			{
				writer.WriteLine($"texture \"{Texture}\"");
			}

			if (TexRep.HasValue && TexRep.Value != Vector2.One)
			{
				writer.WriteLine($"texrep {F(TexRep.Value.X)} {F(TexRep.Value.Y)}");
			}

			if (Rotation.HasValue && Rotation.Value != Matrix4x4.Identity)
			{
				Matrix4x4 rot = Rotation.Value;
				writer.WriteLine($"rot {F(rot.M11)} {F(rot.M12)} {F(rot.M13)}  {F(rot.M21)} {F(rot.M22)} {F(rot.M23)}  {F(rot.M31)} {F(rot.M32)} {F(rot.M33)}");
			}

			if (Location.HasValue && Location.Value != Vector3.Zero)
			{
				writer.WriteLine($"loc {F(Location.Value.X)} {F(Location.Value.Y)} {F(Location.Value.Z)}");
			}

			if (Vertices.Count > 0)
			{
				writer.WriteLine($"numvert {Vertices.Count}");
				foreach (Vector3 vertex in Vertices)
				{
					writer.WriteLine($"{F(vertex.X)} {F(vertex.Y)} {F(vertex.Z)}");
				}
			}

			if (Surfaces.Count > 0)
			{
				writer.WriteLine($"numsurf {Surfaces.Count}");
				foreach (Surface surface in Surfaces)
				{
					surface.WriteTo(writer);
				}
			}
		}
	}

	public class Surface
	{
		public int Flags { get; set; } // type and shading flags
		public int? MaterialIndex { get; set; }
		public List<SurfaceRef> Refs { get; private set; } = [];

		public Surface(int flags = 0x20)
		{
			Flags = flags;
		}

		public void AddRef(int vertexIndex, Vector2 texCoord)
		{
			Refs.Add(new SurfaceRef { VertexIndex = vertexIndex, TexCoord = texCoord });
		}

		public void AddRef(int vertexIndex, float u = 0, float v = 0)
		{
			Refs.Add(new SurfaceRef { VertexIndex = vertexIndex, TexCoord = new Vector2(u, v) });
		}

		public void WriteTo(StreamWriter writer)
		{
			writer.WriteLine($"SURF 0x{Flags:X}");

			if (MaterialIndex.HasValue)
			{
				writer.WriteLine($"mat {MaterialIndex.Value}");
			}

			writer.WriteLine($"refs {Refs.Count}");
			foreach (SurfaceRef surfRef in Refs)
			{
				writer.WriteLine($"{surfRef.VertexIndex} {F(surfRef.TexCoord.X)} {F(surfRef.TexCoord.Y)}");
			}
		}
	}

	public struct SurfaceRef
	{
		public int VertexIndex;
		public Vector2 TexCoord;
	}

	public struct Material
	{
		public string Name;
		public Vector3 Rgb;
		public Vector3 Ambient;
		public Vector3 Emissive;
		public Vector3 Specular;
		public int Shininess;
		public float Transparency;

		public Material()
		{
			Name = "";
			Rgb = Vector3.One;
			Ambient = new Vector3(0.2f, 0.2f, 0.2f);
			Emissive = Vector3.Zero;
			Specular = new Vector3(0.5f, 0.5f, 0.5f);
			Shininess = 10;
			Transparency = 0;
		}
	}

	// Helper to format floats consistently
	private static string F(float value)
	{
		return value.ToString("0.######", CultureInfo.InvariantCulture);
	}

	// Build AC3D Poly from glTF mesh primitive data
	public Poly? BuildPolyFromGltf(string srcPath, string srcBgl, JObject meshJson, JArray accJson, JArray bvJson,
		JArray matsJson, JArray texJson, JArray imgJson, byte[] glbBinBytes, ref List<Material> materials)
	{
		string meshName = meshJson["name"]?.Value<string>() ?? "UnnamedMesh";
		JArray primitives = (JArray)meshJson["primitives"]!;

		if (primitives.Count == 0) return null;

		// For now, just handle the first primitive (can expand later to handle multiple)
		JObject primJson = (JObject)primitives[0]!;

		// Check for invisible material
		int materialIndex = primJson["material"]?.Value<int>() ?? -1;
		if (materialIndex >= 0 && materialIndex < matsJson.Count)
		{
			JObject matJson = (JObject)matsJson[materialIndex];
			if (matJson["extensions"]?["ASOBO_material_invisible"] != null || matJson["extensions"]?["ASOBO_material_environment_occluder"] != null)
				return null;
		}

		Poly poly = new(meshName);

		// Get accessor indices
		int? idxAccIndex = primJson["indices"]?.Value<int>();
		int? posAccIndex = primJson["attributes"]?["POSITION"]?.Value<int>();
		int? texCoord0Index = primJson["attributes"]?["TEXCOORD_0"]?.Value<int>();

		if (!posAccIndex.HasValue) return null;

		// Load position data
		Vector3[] positions = LoadPositionData((JObject)accJson[posAccIndex.Value], bvJson, glbBinBytes);
		foreach (var pos in positions)
		{
			poly.AddVertex(pos);
		}

		// Load texture coordinates if available
		Vector2[]? texCoords = null;
		if (texCoord0Index.HasValue && texCoord0Index.Value < accJson.Count)
		{
			texCoords = LoadTexCoordData((JObject)accJson[texCoord0Index.Value], bvJson, glbBinBytes);
		}

		// Load indices
		int[]? indices = null;
		if (idxAccIndex.HasValue && idxAccIndex.Value < accJson.Count)
		{
			indices = LoadIndexData((JObject)accJson[idxAccIndex.Value], bvJson, glbBinBytes);
		}

		// Process material
		int? acMatIndex = null;
		string? texturePath = null;
		if (materialIndex >= 0 && materialIndex < matsJson.Count)
		{
			JObject matJson = (JObject)matsJson[materialIndex];
			string matName = matJson["name"]?.Value<string>() ?? "Material";

			// Extract material properties
			Vector3 baseColor = Vector3.One;
			if (matJson["pbrMetallicRoughness"]?["baseColorFactor"] != null)
			{
				JArray colorFactor = (JArray)matJson["pbrMetallicRoughness"]!["baseColorFactor"]!;
				baseColor = new Vector3(
					colorFactor[0].Value<float>(),
					colorFactor[1].Value<float>(),
					colorFactor[2].Value<float>()
				);
			}

			// Get texture if present
			if (matJson["pbrMetallicRoughness"]?["baseColorTexture"] != null)
			{
				int texIndex = matJson["pbrMetallicRoughness"]!["baseColorTexture"]!["index"]!.Value<int>();
				if (texIndex >= 0 && texIndex < texJson.Count)
				{
					string imgUri = imgJson[texJson[texIndex]["extensions"]!["MSFT_texture_dds"]!["source"]!.Value<int>()]["uri"]!.Value<string>()?.Split('\\').Last()?.Split('/').Last() ?? "";
					if (!string.IsNullOrEmpty(imgUri))
					{
						string[] imageMatches = [.. Directory.GetFiles(srcPath, "*", SearchOption.AllDirectories).Where(f => string.Equals(Path.GetFileName(f), imgUri, StringComparison.OrdinalIgnoreCase))];

						int bestScore = -1;
						foreach (string match in imageMatches)
						{
							int i = 0;
							while (i < Math.Min(match.Length, srcBgl.Length) && match[i] == srcBgl[i])
								i++;
							if (i > bestScore)
							{
								bestScore = i;
								texturePath = match;
							}
						}
					}
				}
			}

			// Create AC3D material
			Material acMat = new()
			{
				Name = matName,
				Rgb = baseColor,
				Ambient = new Vector3(0.2f, 0.2f, 0.2f),
				Emissive = Vector3.Zero,
				Specular = new Vector3(0.5f, 0.5f, 0.5f),
				Shininess = 10,
				Transparency = 0
			};

			// Check if material already exists (deduplicate)
			acMatIndex = -1;
			for (int i = 0; i < materials.Count; i++)
			{
				Material existing = materials[i];
				if (existing.Name == acMat.Name &&
					existing.Rgb == acMat.Rgb &&
					existing.Ambient == acMat.Ambient &&
					existing.Emissive == acMat.Emissive &&
					existing.Specular == acMat.Specular &&
					existing.Shininess == acMat.Shininess &&
					Math.Abs(existing.Transparency - acMat.Transparency) < 0.001f)
				{
					acMatIndex = i;
					break;
				}
			}

			// Add new material if not found
			if (acMatIndex < 0)
			{
				acMatIndex = materials.Count;
				materials.Add(acMat);
			}
		}

		// Set texture if found
		if (!string.IsNullOrEmpty(texturePath))
		{
			TextureFiles.Add(texturePath);
			poly.Texture = Path.GetFileName(texturePath);
		}

		// Create surfaces from triangles
		if (indices != null)
		{
			for (int i = 0; i < indices.Length; i += 3)
			{
				if (i + 2 >= indices.Length) break;

				Surface surf = poly.AddSurface(0x20); // shaded polygon
				surf.MaterialIndex = acMatIndex;

				// Add vertices in reverse order to fix winding
				for (int j = 2; j >= 0; j--)
				{
					int idx = indices[i + j];
					Vector2 uv = texCoords != null && idx < texCoords.Length ? texCoords[idx] : Vector2.Zero;
					// Flip V coordinate for AC3D
					uv.Y = 1.0f - uv.Y;
					surf.AddRef(idx, uv);
				}
			}
		}
		else if (positions.Length >= 3)
		{
			// No indices, treat as triangle list
			for (int i = 0; i < positions.Length; i += 3)
			{
				if (i + 2 >= positions.Length) break;

				Surface surf = poly.AddSurface(0x20);
				surf.MaterialIndex = acMatIndex;

				// Add vertices in reverse order to fix winding
				for (int j = 2; j >= 0; j--)
				{
					int idx = i + j;
					Vector2 uv = texCoords != null && idx < texCoords.Length ? texCoords[idx] : Vector2.Zero;
					// Flip V coordinate for AC3D
					uv.Y = 1.0f - uv.Y;
					surf.AddRef(idx, uv);
				}
			}
		}

		return poly;
	}

	private static Vector3[] LoadPositionData(JObject accessorJson, JArray bufferViewsJson, byte[] binBytes)
	{
		int count = accessorJson["count"]!.Value<int>();
		JObject bufferView = (JObject)bufferViewsJson[accessorJson["bufferView"]!.Value<int>()];
		int accessorByteOffset = accessorJson["byteOffset"]?.Value<int>() ?? 0;
		int bufferViewByteOffset = bufferView["byteOffset"]?.Value<int>() ?? 0;
		int stride = bufferView["byteStride"]?.Value<int>() ?? 12; // 3 floats * 4 bytes

		Vector3[] positions = new Vector3[count];
		for (int i = 0; i < count; i++)
		{
			int offset = bufferViewByteOffset + accessorByteOffset + (i * stride);
			positions[i] = new Vector3(
				BitConverter.ToSingle(binBytes, offset),
				BitConverter.ToSingle(binBytes, offset + 4),
				BitConverter.ToSingle(binBytes, offset + 8)
			);
		}
		return positions;
	}

	private static Vector2[] LoadTexCoordData(JObject accessorJson, JArray bufferViewsJson, byte[] binBytes)
	{
		int count = accessorJson["count"]!.Value<int>();
		JObject bufferView = (JObject)bufferViewsJson[accessorJson["bufferView"]!.Value<int>()];
		int accessorByteOffset = accessorJson["byteOffset"]?.Value<int>() ?? 0;
		int bufferViewByteOffset = bufferView["byteOffset"]?.Value<int>() ?? 0;
		int stride = bufferView["byteStride"]?.Value<int>() ?? 8; // 2 floats * 4 bytes

		Vector2[] texCoords = new Vector2[count];
		for (int i = 0; i < count; i++)
		{
			int offset = bufferViewByteOffset + accessorByteOffset + (i * stride);
			texCoords[i] = new Vector2(
				BitConverter.ToSingle(binBytes, offset),
				BitConverter.ToSingle(binBytes, offset + 4)
			);
		}
		return texCoords;
	}

	private static int[] LoadIndexData(JObject accessorJson, JArray bufferViewsJson, byte[] binBytes)
	{
		int count = accessorJson["count"]!.Value<int>();
		JObject bufferView = (JObject)bufferViewsJson[accessorJson["bufferView"]!.Value<int>()];
		int accessorByteOffset = accessorJson["byteOffset"]?.Value<int>() ?? 0;
		int bufferViewByteOffset = bufferView["byteOffset"]?.Value<int>() ?? 0;
		int componentType = accessorJson["componentType"]!.Value<int>();

		int[] indices = new int[count];
		for (int i = 0; i < count; i++)
		{
			int offset = bufferViewByteOffset + accessorByteOffset;
			indices[i] = componentType switch
			{
				5121 => binBytes[offset + i], // UNSIGNED_BYTE
				5123 => BitConverter.ToUInt16(binBytes, offset + (i * 2)), // UNSIGNED_SHORT
				5125 => (int)BitConverter.ToUInt32(binBytes, offset + (i * 4)), // UNSIGNED_INT
				_ => 0
			};
		}
		return indices;
	}

	public void WriteToFile(string path)
	{
		using StreamWriter writer = new(path);
		writer.WriteLine(Header);

		// Write materials
		foreach (Material mat in Materials)
		{
			writer.WriteLine($"MATERIAL \"{mat.Name}\" rgb {F(mat.Rgb.X)} {F(mat.Rgb.Y)} {F(mat.Rgb.Z)}  amb {F(mat.Ambient.X)} {F(mat.Ambient.Y)} {F(mat.Ambient.Z)}  emis {F(mat.Emissive.X)} {F(mat.Emissive.Y)} {F(mat.Emissive.Z)}  spec {F(mat.Specular.X)} {F(mat.Specular.Y)} {F(mat.Specular.Z)}  shi {mat.Shininess}  trans {F(mat.Transparency)}");
		}

		// Write objects
		foreach (Object obj in Objects)
		{
			obj.WriteTo(writer);
		}

		foreach (string texFile in TextureFiles)
		{
			string destTexPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(texFile));
			if (!File.Exists(destTexPath))
			{
				File.Copy(texFile, destTexPath);
			}
		}
	}
}