using System.Globalization;
using System.Numerics;

namespace Scone;

public class AcBuilder
{
	public string Header { get; private set; } = "AC3Db";
	public List<Material> Materials { get; private set; } = [];
	public List<Object> Objects { get; private set; } = [];

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

	public void Merge(AcBuilder other, Matrix4x4? transform = null)
	{
		// Track the material index offset for updating surface references
		int materialOffset = Materials.Count;

		// Merge materials
		Materials.AddRange(other.Materials);

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

			Group wrapper = new("merged")
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
	}
}