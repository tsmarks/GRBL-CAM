using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GRBL_Cam.Models;
using LibTessDotNet;

namespace GRBL_Cam.Services;

public sealed record PreviewEdgeGeometry(IReadOnlyList<Point3D> Points, string Description);

public readonly record struct PreviewTriangle(Point3D A, Point3D B, Point3D C);

public sealed class LoadedPreviewGeometry
{
    public Model3DGroup Model { get; set; } = new();

    public Rect3D Bounds { get; set; } = Rect3D.Empty;

    public IReadOnlyDictionary<Model3D, PreviewEdgeGeometry> EdgeModels { get; set; }
        = new Dictionary<Model3D, PreviewEdgeGeometry>();

    public IReadOnlyList<PreviewEdgeGeometry> EdgeGeometries { get; set; } = Array.Empty<PreviewEdgeGeometry>();

    public IReadOnlyList<PreviewTriangle> SurfaceTriangles { get; set; } = Array.Empty<PreviewTriangle>();

    public bool HasRenderableGeometry { get; set; }

    public string Description { get; set; } = "Envelope preview";
}

public sealed class PreviewGeometryLoader
{
    private readonly Dictionary<string, CachedPreviewGeometry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LoadedPreviewGeometry? TryLoadPartGeometry(JobSetup setup)
    {
        return TryLoadGeometry(setup.Part.SourcePath, isStockGeometry: false, setup);
    }

    public LoadedPreviewGeometry? TryLoadStockGeometry(JobSetup setup)
    {
        return TryLoadGeometry(setup.Stock.ImportedSolidPath, isStockGeometry: true, setup);
    }

    private LoadedPreviewGeometry? TryLoadGeometry(string path, bool isStockGeometry, JobSetup setup)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var cacheKey = BuildCacheKey(path, isStockGeometry);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.LastWriteUtc == lastWrite)
        {
            return PrepareGeometryForScene(cached.Geometry, setup, isStockGeometry);
        }

        LoadedPreviewGeometry? geometry = null;
        var extension = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            geometry = extension switch
            {
                ".stl" => LoadStl(path, isStockGeometry),
                ".obj" => LoadObj(path, isStockGeometry),
                ".step" or ".stp" => LoadStepWireframe(path, isStockGeometry),
                _ => null
            };
        }
        catch
        {
            geometry = null;
        }

        if (geometry is not null)
        {
            _cache[cacheKey] = new CachedPreviewGeometry(lastWrite, geometry);
            return PrepareGeometryForScene(geometry, setup, isStockGeometry);
        }

        return null;
    }

    private static LoadedPreviewGeometry LoadStl(string path, bool isStockGeometry)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var isBinary = IsBinaryStl(stream, reader);
        stream.Position = 0;

        var mesh = isBinary ? ReadBinaryStl(reader) : ReadAsciiStl(new StreamReader(stream));
        return CreateMeshGeometry(
            mesh,
            isStockGeometry ? "STL stock mesh" : "STL part mesh",
            isStockGeometry ? Color.FromRgb(214, 182, 134) : Color.FromRgb(103, 171, 224),
            isStockGeometry ? 0.12 : 0.9);
    }

    private static LoadedPreviewGeometry LoadObj(string path, bool isStockGeometry)
    {
        var mesh = ReadObj(File.ReadLines(path));
        return CreateMeshGeometry(
            mesh,
            isStockGeometry ? "OBJ stock mesh" : "OBJ part mesh",
            isStockGeometry ? Color.FromRgb(214, 182, 134) : Color.FromRgb(103, 171, 224),
            isStockGeometry ? 0.12 : 0.9);
    }

    private static LoadedPreviewGeometry LoadStepWireframe(string path, bool isStockGeometry)
    {
        var parsedFile = StepWireframeParser.Parse(path);
        if (parsedFile.Segments.Count == 0 && parsedFile.Faces.Count == 0)
        {
            return new LoadedPreviewGeometry
            {
                HasRenderableGeometry = false,
                Description = "STEP file loaded, but no supported preview edges were found."
            };
        }

        var group = new Model3DGroup();
        var edgeModels = new Dictionary<Model3D, PreviewEdgeGeometry>();
        var edgeGeometries = new List<PreviewEdgeGeometry>();
        IReadOnlyList<PreviewTriangle> surfaceTriangles = Array.Empty<PreviewTriangle>();
        var color = isStockGeometry ? Color.FromRgb(214, 182, 134) : Color.FromRgb(103, 171, 224);
        var thickness = Math.Max(parsedFile.Bounds.SizeX, Math.Max(parsedFile.Bounds.SizeY, parsedFile.Bounds.SizeZ)) * 0.004;
        thickness = Math.Clamp(thickness, 0.18, 2.2);

        if (parsedFile.Faces.Count > 0)
        {
            var faceMesh = CreateFaceMesh(parsedFile.Faces);
            if (faceMesh.Positions.Count > 0 && faceMesh.TriangleIndices.Count > 0)
            {
                surfaceTriangles = ExtractMeshTriangles(faceMesh);
                var faceMaterial = CreateMaterial(color, isStockGeometry ? 0.12 : 0.72);
                group.Children.Add(new GeometryModel3D
                {
                    Geometry = faceMesh,
                    Material = faceMaterial,
                    BackMaterial = faceMaterial
                });
            }
        }

        if (parsedFile.Edges.Count > 0)
        {
            foreach (var edge in parsedFile.Edges)
            {
                if (edge.OrderedPoints.Count < 2)
                {
                    continue;
                }

                var edgeGeometry = new PreviewEdgeGeometry(edge.OrderedPoints, "STEP model edge");
                if (!isStockGeometry)
                {
                    edgeGeometries.Add(edgeGeometry);
                }

                for (var index = 0; index < edge.OrderedPoints.Count - 1; index++)
                {
                    var start = edge.OrderedPoints[index];
                    var end = edge.OrderedPoints[index + 1];
                    if ((end - start).LengthSquared < 0.0000001)
                    {
                        continue;
                    }

                    var model = CreateSegmentModel(
                        start,
                        end,
                        thickness,
                        color,
                        isStockGeometry ? 0.38 : 0.88);
                    group.Children.Add(model);
                    if (!isStockGeometry)
                    {
                        edgeModels[model] = edgeGeometry;
                    }
                }
            }
        }
        else
        {
            foreach (var segment in parsedFile.Segments)
            {
                group.Children.Add(CreateSegmentModel(
                    segment.Start,
                    segment.End,
                    thickness,
                    color,
                    isStockGeometry ? 0.38 : 0.88));
            }
        }

        return new LoadedPreviewGeometry
        {
            Model = group,
            Bounds = parsedFile.Bounds,
            EdgeModels = edgeModels,
            EdgeGeometries = edgeGeometries,
            SurfaceTriangles = surfaceTriangles,
            HasRenderableGeometry = true,
            Description = parsedFile.Faces.Count > 0
                ? $"STEP shaded preview ({parsedFile.Faces.Count} shaded faces, {parsedFile.Segments.Count} edge segments)"
                : $"STEP geometry ({parsedFile.Segments.Count} preview edge segments)"
        };
    }

    private static LoadedPreviewGeometry CreateMeshGeometry(MeshGeometry3D mesh, string description, Color color, double opacity)
    {
        if (mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
        {
            return new LoadedPreviewGeometry
            {
                HasRenderableGeometry = false,
                Description = $"{description} is empty."
            };
        }

        var material = CreateMaterial(color, opacity);
        var model = new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        };

        return new LoadedPreviewGeometry
        {
            Model = new Model3DGroup { Children = { model } },
            Bounds = mesh.Bounds,
            EdgeModels = new Dictionary<Model3D, PreviewEdgeGeometry>(),
            EdgeGeometries = Array.Empty<PreviewEdgeGeometry>(),
            SurfaceTriangles = ExtractMeshTriangles(mesh),
            HasRenderableGeometry = true,
            Description = description
        };
    }

    private static MeshGeometry3D CreateFaceMesh(IEnumerable<StepPlanarFace> faces)
    {
        var mesh = new MeshGeometry3D();
        foreach (var face in faces)
        {
            var baseIndex = mesh.Positions.Count;
            for (var index = 0; index < face.Vertices.Count; index++)
            {
                mesh.Positions.Add(face.Vertices[index]);
                mesh.Normals.Add(face.Normals[Math.Min(index, face.Normals.Count - 1)]);
            }

            foreach (var triangleIndex in face.TriangleIndices)
            {
                mesh.TriangleIndices.Add(baseIndex + triangleIndex);
            }
        }

        return mesh;
    }

    private static MeshGeometry3D ReadBinaryStl(BinaryReader reader)
    {
        var mesh = new MeshGeometry3D();
        reader.ReadBytes(80);
        var triangleCount = reader.ReadUInt32();

        for (var index = 0; index < triangleCount; index++)
        {
            var normal = ReadVector3(reader);
            var v0 = ReadPoint3(reader);
            var v1 = ReadPoint3(reader);
            var v2 = ReadPoint3(reader);
            reader.ReadUInt16();
            AddTriangle(mesh, v0, v1, v2, normal);
        }

        return mesh;
    }

    private static MeshGeometry3D ReadAsciiStl(StreamReader reader)
    {
        var mesh = new MeshGeometry3D();
        string? line;
        var normal = new Vector3D(0, 0, 1);
        var vertices = new List<Point3D>(3);

        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                normal = ParseVector(trimmed["facet normal".Length..]);
            }
            else if (trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                vertices.Add(ParsePoint(trimmed["vertex".Length..]));
                if (vertices.Count == 3)
                {
                    AddTriangle(mesh, vertices[0], vertices[1], vertices[2], normal);
                    vertices.Clear();
                }
            }
        }

        return mesh;
    }

    private static MeshGeometry3D ReadObj(IEnumerable<string> lines)
    {
        var positions = new List<Point3D>();
        var mesh = new MeshGeometry3D();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                positions.Add(ParsePoint(line[1..]));
                continue;
            }

            if (!line.StartsWith("f ", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            var faceIndices = tokens.Select(token => ParseObjIndex(token, positions.Count)).ToList();
            for (var index = 1; index < faceIndices.Count - 1; index++)
            {
                var v0 = positions[faceIndices[0]];
                var v1 = positions[faceIndices[index]];
                var v2 = positions[faceIndices[index + 1]];
                var normal = CalculateNormal(v0, v1, v2);
                AddTriangle(mesh, v0, v1, v2, normal);
            }
        }

        return mesh;
    }

    private static int ParseObjIndex(string token, int positionCount)
    {
        var vertexToken = token.Split('/')[0];
        var parsedIndex = int.Parse(vertexToken, CultureInfo.InvariantCulture);
        return parsedIndex > 0 ? parsedIndex - 1 : positionCount + parsedIndex;
    }

    private static Vector3D GetTranslation(JobSetup setup, bool isStockGeometry)
    {
        var stockOffset = GetOrientedStockOffset(setup);
        return isStockGeometry
            ? new Vector3D(
                setup.WorkOffset.X + setup.AlignmentOffsetX + stockOffset.X,
                setup.WorkOffset.Y + setup.AlignmentOffsetY + stockOffset.Y,
                setup.WorkOffset.Z + setup.AlignmentOffsetZ + stockOffset.Z)
            : new Vector3D(setup.WorkOffset.X + setup.AlignmentOffsetX, setup.WorkOffset.Y + setup.AlignmentOffsetY, setup.WorkOffset.Z + setup.AlignmentOffsetZ);
    }

    private static Vector3D GetOrientedStockOffset(JobSetup setup)
    {
        var offset = new Vector3D(setup.Stock.OffsetX, setup.Stock.OffsetY, setup.Stock.OffsetZ);
        if (offset.LengthSquared < 0.0000001)
        {
            return offset;
        }

        var transformGroup = new Transform3DGroup();
        AddRotation(transformGroup, new Vector3D(1, 0, 0), setup.Part.RotationA);
        AddRotation(transformGroup, new Vector3D(0, 1, 0), setup.Part.RotationB);
        AddRotation(transformGroup, new Vector3D(0, 0, 1), setup.Part.RotationC);
        return transformGroup.Children.Count == 0
            ? offset
            : transformGroup.Transform(offset);
    }

    private static bool IsBinaryStl(Stream stream, BinaryReader reader)
    {
        if (stream.Length < 84)
        {
            return false;
        }

        var header = reader.ReadBytes(80);
        var triangleCount = reader.ReadUInt32();
        var expectedLength = 84L + (triangleCount * 50L);
        var headerText = System.Text.Encoding.ASCII.GetString(header);
        return expectedLength == stream.Length || !headerText.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
    }

    private static Vector3D ReadVector3(BinaryReader reader)
    {
        return new Vector3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Point3D ReadPoint3(BinaryReader reader)
    {
        return new Point3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Point3D ParsePoint(string data)
    {
        var values = data.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return new Point3D(
            ParseDouble(values.ElementAtOrDefault(0)),
            ParseDouble(values.ElementAtOrDefault(1)),
            ParseDouble(values.ElementAtOrDefault(2)));
    }

    private static Vector3D ParseVector(string data)
    {
        var values = data.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return new Vector3D(
            ParseDouble(values.ElementAtOrDefault(0)),
            ParseDouble(values.ElementAtOrDefault(1)),
            ParseDouble(values.ElementAtOrDefault(2)));
    }

    private static double ParseDouble(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;
    }

    private static Vector3D CalculateNormal(Point3D v0, Point3D v1, Point3D v2)
    {
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var normal = Vector3D.CrossProduct(edge1, edge2);
        if (normal.LengthSquared < 0.0000001)
        {
            return new Vector3D(0, 0, 1);
        }

        normal.Normalize();
        return normal;
    }

    private static void AddTriangle(MeshGeometry3D mesh, Point3D v0, Point3D v1, Point3D v2, Vector3D normal)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(v0);
        mesh.Positions.Add(v1);
        mesh.Positions.Add(v2);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
    }

    private static GeometryModel3D CreateSegmentModel(Point3D start, Point3D end, double thickness, Color color, double opacity)
    {
        var direction = end - start;
        var length = direction.Length;
        if (length < 0.0001)
        {
            return new GeometryModel3D();
        }

        direction.Normalize();
        var reference = Math.Abs(Vector3D.DotProduct(direction, new Vector3D(0, 0, 1))) > 0.92
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 0, 1);
        var side1 = Vector3D.CrossProduct(direction, reference);
        side1.Normalize();
        side1 *= thickness / 2d;
        var side2 = Vector3D.CrossProduct(direction, side1);
        side2.Normalize();
        side2 *= thickness / 2d;

        var p0 = start - side1 - side2;
        var p1 = start + side1 - side2;
        var p2 = start + side1 + side2;
        var p3 = start - side1 + side2;
        var p4 = end - side1 - side2;
        var p5 = end + side1 - side2;
        var p6 = end + side1 + side2;
        var p7 = end - side1 + side2;

        var mesh = new MeshGeometry3D();
        AddQuad(mesh, p0, p1, p5, p4, CalculateNormal(p0, p1, p5));
        AddQuad(mesh, p1, p2, p6, p5, CalculateNormal(p1, p2, p6));
        AddQuad(mesh, p2, p3, p7, p6, CalculateNormal(p2, p3, p7));
        AddQuad(mesh, p3, p0, p4, p7, CalculateNormal(p3, p0, p4));
        AddQuad(mesh, p4, p5, p6, p7, direction);
        AddQuad(mesh, p3, p2, p1, p0, -direction);

        var material = CreateMaterial(color, opacity);
        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        };
    }

    private static void AddQuad(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }

    private static Material CreateMaterial(Color color, double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0.05, 1.0) * 255);
        var diffuseBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        diffuseBrush.Freeze();
        var highlightBrush = new SolidColorBrush(Color.FromArgb(Math.Min((byte)255, (byte)(alpha + 12)), 255, 255, 255));
        highlightBrush.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(diffuseBrush));
        material.Children.Add(new SpecularMaterial(highlightBrush, 30));
        return material;
    }

    private static LoadedPreviewGeometry PrepareGeometryForScene(LoadedPreviewGeometry geometry, JobSetup setup, bool isStockGeometry)
    {
        var sceneGeometry = Clone(geometry);
        var sceneTransform = BuildSceneTransform(sceneGeometry, setup, isStockGeometry);
        if (sceneTransform.Value.IsIdentity)
        {
            return sceneGeometry;
        }

        sceneGeometry.Model.Transform = sceneTransform;
        sceneGeometry.Bounds = sceneTransform.TransformBounds(sceneGeometry.Bounds);
        sceneGeometry.EdgeModels = TransformEdges(sceneGeometry.EdgeModels, sceneTransform);
        sceneGeometry.EdgeGeometries = TransformEdges(sceneGeometry.EdgeGeometries, sceneTransform);
        sceneGeometry.SurfaceTriangles = TransformTriangles(sceneGeometry.SurfaceTriangles, sceneTransform);
        return sceneGeometry;
    }

    private static Transform3D BuildSceneTransform(LoadedPreviewGeometry geometry, JobSetup setup, bool isStockGeometry)
    {
        var transformGroup = new Transform3DGroup();
        if (geometry.Model.Transform is not null && !geometry.Model.Transform.Value.IsIdentity)
        {
            transformGroup.Children.Add(geometry.Model.Transform);
        }

        if (!isStockGeometry)
        {
            AddRotation(transformGroup, new Vector3D(1, 0, 0), setup.Part.RotationA);
            AddRotation(transformGroup, new Vector3D(0, 1, 0), setup.Part.RotationB);
            AddRotation(transformGroup, new Vector3D(0, 0, 1), setup.Part.RotationC);
        }
        else
        {
            var center = new Point3D(
                geometry.Bounds.X + (geometry.Bounds.SizeX / 2d),
                geometry.Bounds.Y + (geometry.Bounds.SizeY / 2d),
                geometry.Bounds.Z + (geometry.Bounds.SizeZ / 2d));
            AddRotation(transformGroup, new Vector3D(1, 0, 0), setup.Stock.RotationA, center);
            AddRotation(transformGroup, new Vector3D(0, 1, 0), setup.Stock.RotationB, center);
            AddRotation(transformGroup, new Vector3D(0, 0, 1), setup.Stock.RotationC, center);
        }

        var translation = GetTranslation(setup, isStockGeometry);
        if (translation.LengthSquared >= 0.0000001)
        {
            transformGroup.Children.Add(new TranslateTransform3D(translation));
        }

        return transformGroup.Children.Count switch
        {
            0 => Transform3D.Identity,
            1 => transformGroup.Children[0],
            _ => transformGroup
        };
    }

    private static void AddRotation(Transform3DGroup transformGroup, Vector3D axis, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < 0.000001)
        {
            return;
        }

        transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angleDegrees)));
    }

    private static void AddRotation(Transform3DGroup transformGroup, Vector3D axis, double angleDegrees, Point3D center)
    {
        if (Math.Abs(angleDegrees) < 0.000001)
        {
            return;
        }

        transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angleDegrees), center));
    }

    private static IReadOnlyDictionary<Model3D, PreviewEdgeGeometry> TransformEdges(
        IReadOnlyDictionary<Model3D, PreviewEdgeGeometry> edgeModels,
        Transform3D transform)
    {
        if (edgeModels.Count == 0)
        {
            return edgeModels;
        }

        return edgeModels.ToDictionary(
            pair => pair.Key,
            pair => new PreviewEdgeGeometry(
                pair.Value.Points.Select(transform.Transform).ToList(),
                pair.Value.Description));
    }

    private static IReadOnlyList<PreviewEdgeGeometry> TransformEdges(
        IReadOnlyList<PreviewEdgeGeometry> edges,
        Transform3D transform)
    {
        if (edges.Count == 0)
        {
            return edges;
        }

        return edges
            .Select(edge => new PreviewEdgeGeometry(
                edge.Points.Select(transform.Transform).ToList(),
                edge.Description))
            .ToList();
    }

    private static IReadOnlyList<PreviewTriangle> TransformTriangles(
        IReadOnlyList<PreviewTriangle> triangles,
        Transform3D transform)
    {
        if (triangles.Count == 0)
        {
            return triangles;
        }

        return triangles
            .Select(triangle => new PreviewTriangle(
                transform.Transform(triangle.A),
                transform.Transform(triangle.B),
                transform.Transform(triangle.C)))
            .ToList();
    }

    private static LoadedPreviewGeometry Clone(LoadedPreviewGeometry geometry)
    {
        var clonedModel = geometry.Model.Clone();
        var modelMap = PairModelTrees(geometry.Model, clonedModel)
            .ToDictionary(pair => pair.Source, pair => pair.Clone);
        var edgeModels = new Dictionary<Model3D, PreviewEdgeGeometry>();
        foreach (var edgeModel in geometry.EdgeModels)
        {
            if (modelMap.TryGetValue(edgeModel.Key, out var clonedEdgeModel))
            {
                edgeModels[clonedEdgeModel] = edgeModel.Value;
            }
        }

        return new LoadedPreviewGeometry
        {
            Model = clonedModel,
            Bounds = geometry.Bounds,
            EdgeModels = edgeModels,
            EdgeGeometries = geometry.EdgeGeometries,
            SurfaceTriangles = geometry.SurfaceTriangles,
            HasRenderableGeometry = geometry.HasRenderableGeometry,
            Description = geometry.Description
        };
    }

    private static IEnumerable<(Model3D Source, Model3D Clone)> PairModelTrees(Model3D source, Model3D clone)
    {
        yield return (source, clone);

        if (source is not Model3DGroup sourceGroup || clone is not Model3DGroup cloneGroup)
        {
            yield break;
        }

        var count = Math.Min(sourceGroup.Children.Count, cloneGroup.Children.Count);
        for (var index = 0; index < count; index++)
        {
            foreach (var pair in PairModelTrees(sourceGroup.Children[index], cloneGroup.Children[index]))
            {
                yield return pair;
            }
        }
    }

    private static string BuildCacheKey(string path, bool isStockGeometry)
    {
        return $"{(isStockGeometry ? "stock" : "part")}::{path}";
    }

    private sealed record CachedPreviewGeometry(DateTime LastWriteUtc, LoadedPreviewGeometry Geometry);

    private static IReadOnlyList<PreviewTriangle> ExtractMeshTriangles(MeshGeometry3D mesh)
    {
        var triangles = new List<PreviewTriangle>();
        for (var index = 0; index + 2 < mesh.TriangleIndices.Count; index += 3)
        {
            var a = mesh.TriangleIndices[index];
            var b = mesh.TriangleIndices[index + 1];
            var c = mesh.TriangleIndices[index + 2];
            if (a < 0 || b < 0 || c < 0 ||
                a >= mesh.Positions.Count ||
                b >= mesh.Positions.Count ||
                c >= mesh.Positions.Count)
            {
                continue;
            }

            triangles.Add(new PreviewTriangle(mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]));
        }

        return triangles;
    }
}

internal sealed class StepWireframeFile
{
    public List<StepSegment> Segments { get; } = new();

    public List<StepEdgeCurve> Edges { get; } = new();

    public List<StepPlanarFace> Faces { get; } = new();

    public Rect3D Bounds { get; set; } = Rect3D.Empty;
}

internal readonly record struct StepSegment(Point3D Start, Point3D End);

internal sealed record StepPlanarFace(IReadOnlyList<Point3D> Vertices, IReadOnlyList<int> TriangleIndices, IReadOnlyList<Vector3D> Normals);

internal readonly record struct StepAxisPlacement(Point3D Origin, Vector3D Normal, Vector3D XDirection, Vector3D YDirection);

internal sealed record StepEdgeCurve(Point3D Start, Point3D End, IReadOnlyList<Point3D> OrderedPoints);

internal sealed record StepCylindricalSurface(StepAxisPlacement Placement, double Radius);

internal sealed record StepConicalSurface(StepAxisPlacement Placement, double Radius, double SemiAngle);

internal sealed record StepSplineSurface(
    int DegreeU,
    int DegreeV,
    IReadOnlyList<IReadOnlyList<Point3D>> ControlPoints,
    IReadOnlyList<double> KnotsU,
    IReadOnlyList<double> KnotsV,
    IReadOnlyList<IReadOnlyList<double>>? Weights);

internal sealed record StepFaceLoops(IReadOnlyList<Point3D> OuterLoop, IReadOnlyList<IReadOnlyList<Point3D>> InnerLoops);

internal static class StepWireframeParser
{
    private readonly record struct SplineSample(double X, double Y, double Z, double Weight)
    {
        public static SplineSample Lerp(SplineSample a, SplineSample b, double t)
        {
            return new SplineSample(
                a.X + ((b.X - a.X) * t),
                a.Y + ((b.Y - a.Y) * t),
                a.Z + ((b.Z - a.Z) * t),
                a.Weight + ((b.Weight - a.Weight) * t));
        }

        public Point3D ToPoint()
        {
            var safeWeight = Math.Abs(Weight) < 0.0000001 ? 1d : Weight;
            return new Point3D(X / safeWeight, Y / safeWeight, Z / safeWeight);
        }
    }

    public static StepWireframeFile Parse(string path)
    {
        var file = new StepWireframeFile();
        var entities = ReadEntities(path);
        var cartesianPoints = new Dictionary<int, Point3D>();
        var directions = new Dictionary<int, Vector3D>();
        var axisPlacements = new Dictionary<int, StepAxisPlacement>();
        var planes = new Dictionary<int, StepAxisPlacement>();
        var cylindricalSurfaces = new Dictionary<int, StepCylindricalSurface>();
        var conicalSurfaces = new Dictionary<int, StepConicalSurface>();
        var splineSurfaces = new Dictionary<int, StepSplineSurface>();
        var vertexPoints = new Dictionary<int, Point3D>();

        foreach (var entity in entities.Values.Where(entity => entity.Type == "CARTESIAN_POINT"))
        {
            if (TryParseCartesianPoint(entity.Body, out var point))
            {
                cartesianPoints[entity.Id] = point;
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "DIRECTION"))
        {
            if (TryParseDirection(entity.Body, out var direction))
            {
                directions[entity.Id] = direction;
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "AXIS2_PLACEMENT_3D"))
        {
            if (TryParseAxisPlacement(entity.Body, cartesianPoints, directions, out var placement))
            {
                axisPlacements[entity.Id] = placement;
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "PLANE"))
        {
            var refs = ExtractReferences(entity.Body);
            if (refs.Count > 0 && axisPlacements.TryGetValue(refs[0], out var placement))
            {
                planes[entity.Id] = placement;
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "CYLINDRICAL_SURFACE"))
        {
            var refs = ExtractReferences(entity.Body);
            if (refs.Count > 0
                && axisPlacements.TryGetValue(refs[0], out var placement)
                && TryParseLastDouble(entity.Body, out var radius)
                && radius > 0)
            {
                cylindricalSurfaces[entity.Id] = new StepCylindricalSurface(placement, radius);
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "CONICAL_SURFACE"))
        {
            var refs = ExtractReferences(entity.Body);
            if (refs.Count > 0
                && axisPlacements.TryGetValue(refs[0], out var placement)
                && TryParseTrailingDoubles(entity.Body, 2, out var values)
                && values[0] > 0)
            {
                conicalSurfaces[entity.Id] = new StepConicalSurface(placement, values[0], values[1]);
            }
        }

        foreach (var entity in entities.Values.Where(entity =>
            entity.Type == "B_SPLINE_SURFACE" ||
            entity.Body.Contains("B_SPLINE_SURFACE", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryParseSplineSurface(entity, cartesianPoints, out var splineSurface))
            {
                splineSurfaces[entity.Id] = splineSurface;
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "VERTEX_POINT"))
        {
            var refs = ExtractReferences(entity.Body);
            if (refs.Count > 0 && cartesianPoints.TryGetValue(refs[0], out var point))
            {
                vertexPoints[entity.Id] = point;
            }
        }

        var uniqueSegments = new HashSet<string>(StringComparer.Ordinal);
        var edgeCurves = new Dictionary<int, StepEdgeCurve>();
        var bounds = Rect3D.Empty;

        foreach (var entity in entities.Values.Where(entity => entity.Type == "EDGE_CURVE"))
        {
            var refs = ExtractReferences(entity.Body);
            if (refs.Count < 3)
            {
                continue;
            }

            if (!vertexPoints.TryGetValue(refs[0], out var start) || !vertexPoints.TryGetValue(refs[1], out var end))
            {
                continue;
            }

            if (!entities.TryGetValue(refs[2], out var curveEntity))
            {
                continue;
            }

            var curveSegments = BuildCurveSegments(curveEntity, entities, cartesianPoints, vertexPoints, axisPlacements, start, end);
            if (curveSegments.Count == 0)
            {
                curveSegments.Add(new StepSegment(start, end));
            }

            var orderedPoints = BuildOrderedCurvePoints(curveSegments, start, end);
            var edgeCurve = new StepEdgeCurve(start, end, orderedPoints);
            edgeCurves[entity.Id] = edgeCurve;
            file.Edges.Add(edgeCurve);

            foreach (var segment in curveSegments)
            {
                if ((segment.End - segment.Start).LengthSquared < 0.0000001)
                {
                    continue;
                }

                var key = BuildSegmentKey(segment.Start, segment.End);
                if (!uniqueSegments.Add(key))
                {
                    continue;
                }

                file.Segments.Add(segment);
                bounds = Expand(bounds, segment.Start);
                bounds = Expand(bounds, segment.End);
            }
        }

        foreach (var entity in entities.Values.Where(entity => entity.Type == "ADVANCED_FACE"))
        {
            var face = BuildSurfaceFace(entity, entities, planes, cylindricalSurfaces, conicalSurfaces, splineSurfaces, edgeCurves);
            if (face is null)
            {
                continue;
            }

            file.Faces.Add(face);
            foreach (var vertex in face.Vertices)
            {
                bounds = Expand(bounds, vertex);
            }
        }

        file.Bounds = bounds;
        return file;
    }

    private static List<StepSegment> BuildCurveSegments(
        StepEntity curveEntity,
        IReadOnlyDictionary<int, StepEntity> entities,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        IReadOnlyDictionary<int, Point3D> vertexPoints,
        IReadOnlyDictionary<int, StepAxisPlacement> axisPlacements,
        Point3D start,
        Point3D end)
    {
        return curveEntity.Type switch
        {
            "LINE" => new List<StepSegment> { new(start, end) },
            "POLYLINE" => BuildPolylineSegments(curveEntity.Body, cartesianPoints, start, end),
            "CIRCLE" => BuildCircleSegments(curveEntity.Body, axisPlacements, start, end),
            "B_SPLINE_CURVE_WITH_KNOTS" or "B_SPLINE_CURVE" => BuildSplineSegments(curveEntity.Body, cartesianPoints, start, end),
            "TRIMMED_CURVE" => BuildTrimmedCurveSegments(curveEntity.Body, entities, cartesianPoints, vertexPoints, axisPlacements, start, end),
            _ when curveEntity.Body.Contains("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase) => BuildSplineSegments(curveEntity.Body, cartesianPoints, start, end),
            _ => new List<StepSegment> { new(start, end) }
        };
    }

    private static List<StepSegment> BuildTrimmedCurveSegments(
        string trimmedCurveBody,
        IReadOnlyDictionary<int, StepEntity> entities,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        IReadOnlyDictionary<int, Point3D> vertexPoints,
        IReadOnlyDictionary<int, StepAxisPlacement> axisPlacements,
        Point3D start,
        Point3D end)
    {
        var refs = ExtractReferences(trimmedCurveBody);
        if (refs.Count == 0 || !entities.TryGetValue(refs[0], out var basisCurve))
        {
            return new List<StepSegment> { new(start, end) };
        }

        var trimPoints = refs
            .Skip(1)
            .Select(reference => ResolvePoint(reference, cartesianPoints, vertexPoints))
            .Where(point => point.HasValue)
            .Select(point => point!.Value)
            .ToList();

        var trimmedStart = trimPoints.Count > 0 ? trimPoints[0] : start;
        var trimmedEnd = trimPoints.Count > 1 ? trimPoints[1] : end;

        return basisCurve.Type switch
        {
            "LINE" => new List<StepSegment> { new(trimmedStart, trimmedEnd) },
            "POLYLINE" => BuildPolylineSegments(basisCurve.Body, cartesianPoints, trimmedStart, trimmedEnd),
            "CIRCLE" => BuildCircleSegments(basisCurve.Body, axisPlacements, trimmedStart, trimmedEnd),
            "B_SPLINE_CURVE_WITH_KNOTS" or "B_SPLINE_CURVE" => BuildSplineSegments(basisCurve.Body, cartesianPoints, trimmedStart, trimmedEnd),
            _ when basisCurve.Body.Contains("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase) => BuildSplineSegments(basisCurve.Body, cartesianPoints, trimmedStart, trimmedEnd),
            _ => new List<StepSegment> { new(trimmedStart, trimmedEnd) }
        };
    }

    private static List<StepSegment> BuildPolylineSegments(
        string polylineBody,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        Point3D start,
        Point3D end)
    {
        var points = ExtractReferences(polylineBody)
            .Where(cartesianPoints.ContainsKey)
            .Select(reference => cartesianPoints[reference])
            .ToList();

        if (points.Count < 2)
        {
            return new List<StepSegment> { new(start, end) };
        }

        var forwardError = DistanceSquared(points[0], start) + DistanceSquared(points[^1], end);
        var reverseError = DistanceSquared(points[0], end) + DistanceSquared(points[^1], start);
        if (reverseError < forwardError)
        {
            points.Reverse();
        }

        points[0] = start;
        points[^1] = end;

        var segments = new List<StepSegment>();
        for (var index = 0; index < points.Count - 1; index++)
        {
            segments.Add(new StepSegment(points[index], points[index + 1]));
        }

        return segments;
    }

    private static List<StepSegment> BuildSplineSegments(
        string splineBody,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        Point3D start,
        Point3D end)
    {
        var controlReferences = ExtractSplineControlReferences(splineBody);
        var controlPoints = controlReferences
            .Where(cartesianPoints.ContainsKey)
            .Select(reference => cartesianPoints[reference])
            .ToList();

        if (controlPoints.Count < 2)
        {
            return new List<StepSegment> { new(start, end) };
        }

        var points = TrySampleSplineCurve(splineBody, controlPoints, out var sampledPoints)
            ? sampledPoints
            : controlPoints;

        if (points.Count < 2)
        {
            return new List<StepSegment> { new(start, end) };
        }

        var forwardError = DistanceSquared(points[0], start) + DistanceSquared(points[^1], end);
        var reverseError = DistanceSquared(points[0], end) + DistanceSquared(points[^1], start);
        if (reverseError < forwardError)
        {
            points.Reverse();
        }

        points[0] = start;
        points[^1] = end;

        var segments = new List<StepSegment>();
        for (var index = 0; index < points.Count - 1; index++)
        {
            if (DistanceSquared(points[index], points[index + 1]) > 0.0000001)
            {
                segments.Add(new StepSegment(points[index], points[index + 1]));
            }
        }

        return segments;
    }

    private static List<int> ExtractSplineControlReferences(string splineBody)
    {
        if (TryExtractFunctionArguments(splineBody, "B_SPLINE_CURVE", out var curveArguments))
        {
            var arguments = SplitTopLevelArguments(curveArguments);
            if (arguments.Count > 1)
            {
                return ExtractReferences(arguments[1]);
            }
        }

        var bodyArguments = SplitTopLevelArguments(splineBody);
        return bodyArguments.Count > 2
            ? ExtractReferences(bodyArguments[2])
            : ExtractReferences(splineBody);
    }

    private static bool TrySampleSplineCurve(
        string splineBody,
        IReadOnlyList<Point3D> controlPoints,
        out List<Point3D> sampledPoints)
    {
        sampledPoints = new List<Point3D>();
        if (!TryParseSplineDefinition(splineBody, controlPoints.Count, out var degree, out var knots, out var weights))
        {
            return false;
        }

        if (degree < 1 || controlPoints.Count <= degree || knots.Count < controlPoints.Count + degree + 1)
        {
            return false;
        }

        var startParameter = knots[degree];
        var endParameter = knots[controlPoints.Count];
        if (endParameter <= startParameter)
        {
            return false;
        }

        var distinctKnots = knots
            .Where(knot => knot >= startParameter && knot <= endParameter)
            .OrderBy(knot => knot)
            .Aggregate(new List<double>(), (list, knot) =>
            {
                if (list.Count == 0 || Math.Abs(list[^1] - knot) > 0.0000001)
                {
                    list.Add(knot);
                }

                return list;
            });

        var targetSampleCount = Math.Clamp(controlPoints.Count * 8, 24, 192);
        var samplesPerSpan = Math.Max(3, targetSampleCount / Math.Max(1, distinctKnots.Count - 1));
        sampledPoints.Add(EvaluateSplinePoint(controlPoints, knots, weights, degree, startParameter));

        for (var spanIndex = 0; spanIndex < distinctKnots.Count - 1; spanIndex++)
        {
            var spanStart = distinctKnots[spanIndex];
            var spanEnd = distinctKnots[spanIndex + 1];
            if (spanEnd - spanStart <= 0.0000001)
            {
                continue;
            }

            for (var sampleIndex = 1; sampleIndex <= samplesPerSpan; sampleIndex++)
            {
                var t = spanStart + ((spanEnd - spanStart) * sampleIndex / samplesPerSpan);
                var point = EvaluateSplinePoint(controlPoints, knots, weights, degree, t);
                if (sampledPoints.Count == 0 || DistanceSquared(sampledPoints[^1], point) > 0.000001)
                {
                    sampledPoints.Add(point);
                }
            }
        }

        return sampledPoints.Count >= 2;
    }

    private static bool TryParseSplineDefinition(
        string splineBody,
        int controlPointCount,
        out int degree,
        out List<double> expandedKnots,
        out IReadOnlyList<double>? weights)
    {
        degree = 0;
        expandedKnots = new List<double>();
        weights = null;

        string curveArguments;
        if (TryExtractFunctionArguments(splineBody, "B_SPLINE_CURVE", out curveArguments))
        {
            var arguments = SplitTopLevelArguments(curveArguments);
            if (arguments.Count == 0 || !int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out degree))
            {
                return false;
            }
        }
        else
        {
            var arguments = SplitTopLevelArguments(splineBody);
            if (arguments.Count < 8 || !int.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out degree))
            {
                return false;
            }
        }

        List<int> multiplicities;
        List<double> knots;
        if (TryExtractFunctionArguments(splineBody, "B_SPLINE_CURVE_WITH_KNOTS", out var knotArguments))
        {
            var arguments = SplitTopLevelArguments(knotArguments);
            if (arguments.Count < 2)
            {
                return false;
            }

            multiplicities = ParseIntegerList(arguments[0]);
            knots = ParseDoubleList(arguments[1]);
        }
        else
        {
            var arguments = SplitTopLevelArguments(splineBody);
            if (arguments.Count < 8)
            {
                return false;
            }

            multiplicities = ParseIntegerList(arguments[6]);
            knots = ParseDoubleList(arguments[7]);
        }

        if (multiplicities.Count != knots.Count || multiplicities.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < knots.Count; index++)
        {
            for (var count = 0; count < multiplicities[index]; count++)
            {
                expandedKnots.Add(knots[index]);
            }
        }

        if (expandedKnots.Count < controlPointCount + degree + 1)
        {
            return false;
        }

        if (TryExtractFunctionArguments(splineBody, "RATIONAL_B_SPLINE_CURVE", out var rationalArguments))
        {
            var parsedWeights = ParseDoubleList(rationalArguments);
            if (parsedWeights.Count == controlPointCount)
            {
                weights = parsedWeights;
            }
        }

        return true;
    }

    private static bool TryParseSplineSurface(
        StepEntity entity,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        out StepSplineSurface surface)
    {
        surface = default!;
        if (!TryExtractFunctionArguments(entity.Body, "B_SPLINE_SURFACE", out var surfaceArguments))
        {
            return false;
        }

        var arguments = SplitTopLevelArguments(surfaceArguments);
        if (arguments.Count < 3 ||
            !int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var degreeU) ||
            !int.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var degreeV))
        {
            return false;
        }

        var controlRows = ParseReferenceRows(arguments[2])
            .Select(row => row
                .Where(cartesianPoints.ContainsKey)
                .Select(reference => cartesianPoints[reference])
                .ToList())
            .Where(row => row.Count > 0)
            .Cast<IReadOnlyList<Point3D>>()
            .ToList();

        if (controlRows.Count <= degreeU || controlRows[0].Count <= degreeV || controlRows.Any(row => row.Count != controlRows[0].Count))
        {
            return false;
        }

        if (!TryExtractFunctionArguments(entity.Body, "B_SPLINE_SURFACE_WITH_KNOTS", out var knotArguments))
        {
            return false;
        }

        var knotArgumentList = SplitTopLevelArguments(knotArguments);
        if (knotArgumentList.Count < 4)
        {
            return false;
        }

        var knotsU = ExpandKnots(ParseIntegerList(knotArgumentList[0]), ParseDoubleList(knotArgumentList[2]));
        var knotsV = ExpandKnots(ParseIntegerList(knotArgumentList[1]), ParseDoubleList(knotArgumentList[3]));
        if (knotsU.Count < controlRows.Count + degreeU + 1 || knotsV.Count < controlRows[0].Count + degreeV + 1)
        {
            return false;
        }

        IReadOnlyList<IReadOnlyList<double>>? weights = null;
        if (TryExtractFunctionArguments(entity.Body, "RATIONAL_B_SPLINE_SURFACE", out var rationalArguments))
        {
            var weightRows = ParseDoubleRows(rationalArguments)
                .Where(row => row.Count > 0)
                .Cast<IReadOnlyList<double>>()
                .ToList();
            if (weightRows.Count == controlRows.Count && weightRows.All(row => row.Count == controlRows[0].Count))
            {
                weights = weightRows;
            }
        }

        surface = new StepSplineSurface(degreeU, degreeV, controlRows, knotsU, knotsV, weights);
        return true;
    }

    private static Point3D EvaluateSplinePoint(
        IReadOnlyList<Point3D> controlPoints,
        IReadOnlyList<double> knots,
        IReadOnlyList<double>? weights,
        int degree,
        double parameter)
    {
        var controlPointCount = controlPoints.Count;
        var knotSpan = FindKnotSpan(controlPointCount - 1, degree, parameter, knots);
        var points = new SplineSample[degree + 1];

        for (var index = 0; index <= degree; index++)
        {
            var controlIndex = Math.Clamp(knotSpan - degree + index, 0, controlPointCount - 1);
            var weight = weights is not null && controlIndex < weights.Count
                ? weights[controlIndex]
                : 1d;
            var point = controlPoints[controlIndex];
            points[index] = new SplineSample(point.X * weight, point.Y * weight, point.Z * weight, weight);
        }

        for (var level = 1; level <= degree; level++)
        {
            for (var index = degree; index >= level; index--)
            {
                var knotIndex = knotSpan - degree + index;
                var denominator = knots[knotIndex + degree - level + 1] - knots[knotIndex];
                var alpha = Math.Abs(denominator) < 0.0000001
                    ? 0d
                    : (parameter - knots[knotIndex]) / denominator;
                alpha = Math.Clamp(alpha, 0d, 1d);
                points[index] = SplineSample.Lerp(points[index - 1], points[index], alpha);
            }
        }

        return points[degree].ToPoint();
    }

    private static Point3D EvaluateSplineSurfacePoint(StepSplineSurface surface, double u, double v)
    {
        var rowCount = surface.ControlPoints.Count;
        var columnCount = surface.ControlPoints[0].Count;
        var vControls = new List<SplineSample>(columnCount);
        for (var column = 0; column < columnCount; column++)
        {
            var uControls = new List<SplineSample>(rowCount);
            for (var row = 0; row < rowCount; row++)
            {
                var weight = surface.Weights is not null && row < surface.Weights.Count && column < surface.Weights[row].Count
                    ? surface.Weights[row][column]
                    : 1d;
                uControls.Add(ToSplineSample(surface.ControlPoints[row][column], weight));
            }

            vControls.Add(EvaluateSplineSamples(uControls, surface.KnotsU, surface.DegreeU, u));
        }

        return EvaluateSplineSamples(vControls, surface.KnotsV, surface.DegreeV, v).ToPoint();
    }

    private static SplineSample EvaluateSplineSamples(
        IReadOnlyList<SplineSample> controlSamples,
        IReadOnlyList<double> knots,
        int degree,
        double parameter)
    {
        var controlPointCount = controlSamples.Count;
        var knotSpan = FindKnotSpan(controlPointCount - 1, degree, parameter, knots);
        var points = new SplineSample[degree + 1];
        for (var index = 0; index <= degree; index++)
        {
            var controlIndex = Math.Clamp(knotSpan - degree + index, 0, controlPointCount - 1);
            points[index] = controlSamples[controlIndex];
        }

        for (var level = 1; level <= degree; level++)
        {
            for (var index = degree; index >= level; index--)
            {
                var knotIndex = knotSpan - degree + index;
                var denominator = knots[knotIndex + degree - level + 1] - knots[knotIndex];
                var alpha = Math.Abs(denominator) < 0.0000001
                    ? 0d
                    : (parameter - knots[knotIndex]) / denominator;
                points[index] = SplineSample.Lerp(points[index - 1], points[index], Math.Clamp(alpha, 0d, 1d));
            }
        }

        return points[degree];
    }

    private static SplineSample ToSplineSample(Point3D point, double weight)
    {
        return new SplineSample(point.X * weight, point.Y * weight, point.Z * weight, weight);
    }

    private static int FindKnotSpan(int maxControlIndex, int degree, double parameter, IReadOnlyList<double> knots)
    {
        if (parameter >= knots[maxControlIndex + 1])
        {
            return maxControlIndex;
        }

        if (parameter <= knots[degree])
        {
            return degree;
        }

        var low = degree;
        var high = maxControlIndex + 1;
        var middle = (low + high) / 2;
        while (parameter < knots[middle] || parameter >= knots[middle + 1])
        {
            if (parameter < knots[middle])
            {
                high = middle;
            }
            else
            {
                low = middle;
            }

            middle = (low + high) / 2;
        }

        return middle;
    }

    private static List<StepSegment> BuildCircleSegments(
        string circleBody,
        IReadOnlyDictionary<int, StepAxisPlacement> axisPlacements,
        Point3D start,
        Point3D end)
    {
        var refs = ExtractReferences(circleBody);
        if (refs.Count == 0 || !axisPlacements.TryGetValue(refs[0], out var placement) || !TryParseLastDouble(circleBody, out var radius) || radius <= 0)
        {
            return new List<StepSegment> { new(start, end) };
        }

        var startVector = start - placement.Origin;
        var endVector = end - placement.Origin;
        var startAngle = Math.Atan2(Vector3D.DotProduct(startVector, placement.YDirection), Vector3D.DotProduct(startVector, placement.XDirection));
        var endAngle = Math.Atan2(Vector3D.DotProduct(endVector, placement.YDirection), Vector3D.DotProduct(endVector, placement.XDirection));
        var closedLoop = DistanceSquared(start, end) < 0.0001;
        var sweepAngle = closedLoop ? Math.PI * 2d : NormalizeAngle(endAngle - startAngle);

        var segmentCount = closedLoop
            ? 48
            : Math.Max(8, (int)Math.Ceiling(Math.Abs(sweepAngle) / (Math.PI / 14d)));

        var arcPoints = new List<Point3D>(segmentCount + 1);
        for (var index = 0; index <= segmentCount; index++)
        {
            if (index == 0)
            {
                arcPoints.Add(start);
                continue;
            }

            if (index == segmentCount)
            {
                arcPoints.Add(closedLoop ? start : end);
                continue;
            }

            var t = (double)index / segmentCount;
            var angle = startAngle + (sweepAngle * t);
            arcPoints.Add(
                placement.Origin +
                (placement.XDirection * (Math.Cos(angle) * radius)) +
                (placement.YDirection * (Math.Sin(angle) * radius)));
        }

        var segments = new List<StepSegment>();
        for (var index = 0; index < arcPoints.Count - 1; index++)
        {
            segments.Add(new StepSegment(arcPoints[index], arcPoints[index + 1]));
        }

        return segments;
    }

    private static IReadOnlyList<Point3D> BuildOrderedCurvePoints(IReadOnlyList<StepSegment> segments, Point3D start, Point3D end)
    {
        if (segments.Count == 0)
        {
            return new List<Point3D> { start, end };
        }

        var orderedPoints = new List<Point3D> { segments[0].Start };
        foreach (var segment in segments)
        {
            if (DistanceSquared(orderedPoints[^1], segment.Start) > 0.001)
            {
                orderedPoints.Add(segment.Start);
            }

            if (DistanceSquared(orderedPoints[^1], segment.End) > 0.001)
            {
                orderedPoints.Add(segment.End);
            }
        }

        if (DistanceSquared(orderedPoints[0], start) > DistanceSquared(orderedPoints[^1], start))
        {
            orderedPoints.Reverse();
        }

        if (DistanceSquared(orderedPoints[0], start) > 0.001)
        {
            orderedPoints[0] = start;
        }

        if (DistanceSquared(orderedPoints[^1], end) > 0.001)
        {
            orderedPoints[^1] = end;
        }

        return orderedPoints;
    }

    private static StepPlanarFace? BuildSurfaceFace(
        StepEntity faceEntity,
        IReadOnlyDictionary<int, StepEntity> entities,
        IReadOnlyDictionary<int, StepAxisPlacement> planes,
        IReadOnlyDictionary<int, StepCylindricalSurface> cylindricalSurfaces,
        IReadOnlyDictionary<int, StepConicalSurface> conicalSurfaces,
        IReadOnlyDictionary<int, StepSplineSurface> splineSurfaces,
        IReadOnlyDictionary<int, StepEdgeCurve> edgeCurves)
    {
        var refs = ExtractReferences(faceEntity.Body);
        if (refs.Count < 2)
        {
            return null;
        }

        var surfaceRef = refs[^1];
        var faceLoops = BuildFaceLoops(refs.Take(refs.Count - 1), entities, edgeCurves);
        if (faceLoops is null || faceLoops.OuterLoop.Count < 3)
        {
            return null;
        }

        if (planes.TryGetValue(surfaceRef, out var plane))
        {
            return BuildPlanarFace(faceLoops, plane);
        }

        if (cylindricalSurfaces.TryGetValue(surfaceRef, out var cylindricalSurface))
        {
            return BuildCylindricalFace(faceLoops, cylindricalSurface);
        }

        if (conicalSurfaces.TryGetValue(surfaceRef, out var conicalSurface))
        {
            return BuildConicalFace(faceLoops, conicalSurface);
        }

        if (splineSurfaces.TryGetValue(surfaceRef, out var splineSurface))
        {
            return BuildSplineSurfaceFace(faceLoops, splineSurface);
        }

        return null;
    }

    private static StepPlanarFace? BuildPlanarFace(StepFaceLoops loops, StepAxisPlacement plane)
    {
        if (loops.OuterLoop.Count < 3)
        {
            return null;
        }

        var outerProjected = loops.OuterLoop
            .Select(point => new Point(
                Vector3D.DotProduct(point - plane.Origin, plane.XDirection),
                Vector3D.DotProduct(point - plane.Origin, plane.YDirection)))
            .ToList();
        var holeProjected = loops.InnerLoops
            .Select(loop => loop
                .Select(point => new Point(
                    Vector3D.DotProduct(point - plane.Origin, plane.XDirection),
                    Vector3D.DotProduct(point - plane.Origin, plane.YDirection)))
                .ToList())
            .Where(loop => loop.Count >= 3)
            .Cast<IReadOnlyList<Point>>()
            .ToList();

        if (GetPolygonSignedArea(outerProjected) < 0)
        {
            outerProjected.Reverse();
        }

        return BuildTessellatedFace(
            outerProjected,
            holeProjected,
            point => plane.Origin + (plane.XDirection * point.X) + (plane.YDirection * point.Y),
            _ => plane.Normal);
    }

    private static StepPlanarFace? BuildCylindricalFace(StepFaceLoops loops, StepCylindricalSurface cylindricalSurface)
    {
        if (loops.OuterLoop.Count < 4)
        {
            return null;
        }

        var outerProjected = ProjectCylindricalPoints(loops.OuterLoop, cylindricalSurface.Placement);
        if (outerProjected.Count < 3)
        {
            return null;
        }

        NormalizeCylindricalLoop(outerProjected);
        var outerMin = outerProjected.Min(point => point.X);
        var outerMax = outerProjected.Max(point => point.X);
        var holeProjected = loops.InnerLoops
            .Select(loop => ProjectCylindricalPoints(loop, cylindricalSurface.Placement))
            .Where(loop => loop.Count >= 3)
            .Select(loop =>
            {
                AlignLoopToOuterRange(loop, outerMin, outerMax);
                return (IReadOnlyList<Point>)loop;
            })
            .ToList();

        return BuildTessellatedFace(
            outerProjected,
            holeProjected,
            point =>
            {
                var radial =
                    (cylindricalSurface.Placement.XDirection * Math.Cos(point.X)) +
                    (cylindricalSurface.Placement.YDirection * Math.Sin(point.X));
                radial.Normalize();
                return cylindricalSurface.Placement.Origin +
                    (cylindricalSurface.Placement.Normal * point.Y) +
                    (radial * cylindricalSurface.Radius);
            },
            point =>
            {
                var radial =
                    (cylindricalSurface.Placement.XDirection * Math.Cos(point.X)) +
                    (cylindricalSurface.Placement.YDirection * Math.Sin(point.X));
                radial.Normalize();
                return radial;
            });
    }

    private static StepPlanarFace? BuildConicalFace(StepFaceLoops loops, StepConicalSurface conicalSurface)
    {
        if (loops.OuterLoop.Count < 4)
        {
            return null;
        }

        var outerProjected = ProjectCylindricalPoints(loops.OuterLoop, conicalSurface.Placement);
        if (outerProjected.Count < 3)
        {
            return null;
        }

        NormalizeCylindricalLoop(outerProjected);
        var outerMin = outerProjected.Min(point => point.X);
        var outerMax = outerProjected.Max(point => point.X);
        var holeProjected = loops.InnerLoops
            .Select(loop => ProjectCylindricalPoints(loop, conicalSurface.Placement))
            .Where(loop => loop.Count >= 3)
            .Select(loop =>
            {
                AlignLoopToOuterRange(loop, outerMin, outerMax);
                return (IReadOnlyList<Point>)loop;
            })
            .ToList();

        var slope = Math.Tan(conicalSurface.SemiAngle);

        return BuildTessellatedFace(
            outerProjected,
            holeProjected,
            point =>
            {
                var radial =
                    (conicalSurface.Placement.XDirection * Math.Cos(point.X)) +
                    (conicalSurface.Placement.YDirection * Math.Sin(point.X));
                radial.Normalize();
                var radius = Math.Max(0.05, conicalSurface.Radius + (slope * point.Y));
                return conicalSurface.Placement.Origin +
                    (conicalSurface.Placement.Normal * point.Y) +
                    (radial * radius);
            },
            point =>
            {
                var radial =
                    (conicalSurface.Placement.XDirection * Math.Cos(point.X)) +
                    (conicalSurface.Placement.YDirection * Math.Sin(point.X));
                radial.Normalize();
                var normal = radial - (conicalSurface.Placement.Normal * slope);
                if (normal.LengthSquared < 0.0000001)
                {
                    normal = radial;
                }

                normal.Normalize();
                return normal;
            });
    }

    private static StepPlanarFace? BuildSplineSurfaceFace(StepFaceLoops loops, StepSplineSurface splineSurface)
    {
        if (loops.OuterLoop.Count < 3 ||
            splineSurface.ControlPoints.Count <= splineSurface.DegreeU ||
            splineSurface.ControlPoints[0].Count <= splineSurface.DegreeV)
        {
            return null;
        }

        var uCount = Math.Clamp(splineSurface.ControlPoints.Count * 4, 12, 52);
        var vCount = Math.Clamp(splineSurface.ControlPoints[0].Count * 6, 10, 52);
        var uStart = splineSurface.KnotsU[splineSurface.DegreeU];
        var uEnd = splineSurface.KnotsU[splineSurface.ControlPoints.Count];
        var vStart = splineSurface.KnotsV[splineSurface.DegreeV];
        var vEnd = splineSurface.KnotsV[splineSurface.ControlPoints[0].Count];
        if (uEnd <= uStart || vEnd <= vStart)
        {
            return null;
        }

        var grid = new Point3D[uCount, vCount];
        var vertices = new List<Point3D>(uCount * vCount);
        for (var uIndex = 0; uIndex < uCount; uIndex++)
        {
            var u = uStart + ((uEnd - uStart) * uIndex / Math.Max(1, uCount - 1));
            for (var vIndex = 0; vIndex < vCount; vIndex++)
            {
                var v = vStart + ((vEnd - vStart) * vIndex / Math.Max(1, vCount - 1));
                var point = EvaluateSplineSurfacePoint(splineSurface, u, v);
                grid[uIndex, vIndex] = point;
                vertices.Add(point);
            }
        }

        if (!IsSplineSurfacePatchSane(loops, vertices))
        {
            return null;
        }

        var normals = new List<Vector3D>(vertices.Count);
        for (var uIndex = 0; uIndex < uCount; uIndex++)
        {
            for (var vIndex = 0; vIndex < vCount; vIndex++)
            {
                var previousU = grid[Math.Max(0, uIndex - 1), vIndex];
                var nextU = grid[Math.Min(uCount - 1, uIndex + 1), vIndex];
                var previousV = grid[uIndex, Math.Max(0, vIndex - 1)];
                var nextV = grid[uIndex, Math.Min(vCount - 1, vIndex + 1)];
                var normal = Vector3D.CrossProduct(nextU - previousU, nextV - previousV);
                if (normal.LengthSquared < 0.0000001)
                {
                    normal = new Vector3D(0, 0, 1);
                }

                normal.Normalize();
                normals.Add(normal);
            }
        }

        var triangleIndices = new List<int>((uCount - 1) * (vCount - 1) * 6);
        for (var uIndex = 0; uIndex < uCount - 1; uIndex++)
        {
            for (var vIndex = 0; vIndex < vCount - 1; vIndex++)
            {
                var a = (uIndex * vCount) + vIndex;
                var b = ((uIndex + 1) * vCount) + vIndex;
                var c = ((uIndex + 1) * vCount) + vIndex + 1;
                var d = (uIndex * vCount) + vIndex + 1;
                triangleIndices.Add(a);
                triangleIndices.Add(b);
                triangleIndices.Add(c);
                triangleIndices.Add(a);
                triangleIndices.Add(c);
                triangleIndices.Add(d);
            }
        }

        return new StepPlanarFace(vertices, triangleIndices, normals);
    }

    private static bool IsSplineSurfacePatchSane(StepFaceLoops loops, IReadOnlyList<Point3D> vertices)
    {
        var loopBounds = Rect3D.Empty;
        foreach (var point in loops.OuterLoop)
        {
            loopBounds = Expand(loopBounds, point);
        }

        foreach (var loop in loops.InnerLoops)
        {
            foreach (var point in loop)
            {
                loopBounds = Expand(loopBounds, point);
            }
        }

        var patchBounds = Rect3D.Empty;
        foreach (var point in vertices)
        {
            patchBounds = Expand(patchBounds, point);
        }

        if (loopBounds.IsEmpty || patchBounds.IsEmpty)
        {
            return false;
        }

        var loopDiagonal = GetBoundsDiagonal(loopBounds);
        var patchDiagonal = GetBoundsDiagonal(patchBounds);
        if (loopDiagonal <= 0.0001 || patchDiagonal <= 0.0001)
        {
            return false;
        }

        var loopCenter = GetBoundsCenter(loopBounds);
        var patchCenter = GetBoundsCenter(patchBounds);
        return patchDiagonal <= loopDiagonal * 2.4 &&
            (patchCenter - loopCenter).Length <= loopDiagonal * 0.9;
    }

    private static StepFaceLoops? BuildFaceLoops(
        IEnumerable<int> boundRefs,
        IReadOnlyDictionary<int, StepEntity> entities,
        IReadOnlyDictionary<int, StepEdgeCurve> edgeCurves)
    {
        List<Point3D>? outerLoop = null;
        var innerLoops = new List<IReadOnlyList<Point3D>>();

        foreach (var boundRef in boundRefs)
        {
            if (!entities.TryGetValue(boundRef, out var boundEntity))
            {
                continue;
            }

            if (boundEntity.Type is not ("FACE_OUTER_BOUND" or "FACE_BOUND"))
            {
                continue;
            }

            var cleanedLoop = SimplifyLoop(BuildFaceLoopPoints(boundEntity, entities, edgeCurves));
            if (cleanedLoop.Count < 3)
            {
                continue;
            }

            if (boundEntity.Type == "FACE_OUTER_BOUND" && outerLoop is null)
            {
                outerLoop = cleanedLoop;
            }
            else
            {
                innerLoops.Add(cleanedLoop);
            }
        }

        if (outerLoop is null)
        {
            outerLoop = innerLoops.FirstOrDefault()?.ToList();
            if (outerLoop is null)
            {
                return null;
            }

            innerLoops = innerLoops.Skip(1).ToList();
        }

        return new StepFaceLoops(outerLoop, innerLoops);
    }

    private static List<Point3D> BuildFaceLoopPoints(
        StepEntity boundEntity,
        IReadOnlyDictionary<int, StepEntity> entities,
        IReadOnlyDictionary<int, StepEdgeCurve> edgeCurves)
    {
        var boundRefs = ExtractReferences(boundEntity.Body);
        if (boundRefs.Count == 0 || !entities.TryGetValue(boundRefs[0], out var loopEntity) || loopEntity.Type != "EDGE_LOOP")
        {
            return new List<Point3D>();
        }

        var points = new List<Point3D>();
        foreach (var orientedEdgeRef in ExtractReferences(loopEntity.Body))
        {
            if (!entities.TryGetValue(orientedEdgeRef, out var orientedEdge) || orientedEdge.Type != "ORIENTED_EDGE")
            {
                continue;
            }

            var refs = ExtractReferences(orientedEdge.Body);
            if (refs.Count == 0)
            {
                continue;
            }

            var edgeCurveRef = refs[^1];
            if (!edgeCurves.TryGetValue(edgeCurveRef, out var edgeCurve))
            {
                continue;
            }

            var edgePoints = IsTrueSense(orientedEdge.Body)
                ? edgeCurve.OrderedPoints
                : edgeCurve.OrderedPoints.Reverse().ToList();

            AppendPolyline(points, edgePoints);
        }

        return points;
    }

    private static bool IsTrueSense(string body)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body, @"\.(T|F)\.\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return !match.Success || string.Equals(match.Groups[1].Value, "T", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendPolyline(List<Point3D> target, IReadOnlyList<Point3D> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (target.Count == 0)
        {
            target.AddRange(points);
            return;
        }

        var startIndex = DistanceSquared(target[^1], points[0]) < 0.001 ? 1 : 0;
        for (var index = startIndex; index < points.Count; index++)
        {
            if (target.Count == 0 || DistanceSquared(target[^1], points[index]) >= 0.001)
            {
                target.Add(points[index]);
            }
        }
    }

    private static List<Point3D> SimplifyLoop(List<Point3D> points)
    {
        var cleaned = new List<Point3D>();
        foreach (var point in points)
        {
            if (cleaned.Count == 0 || DistanceSquared(cleaned[^1], point) >= 0.001)
            {
                cleaned.Add(point);
            }
        }

        if (cleaned.Count > 1 && DistanceSquared(cleaned[0], cleaned[^1]) < 0.001)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        return cleaned;
    }

    private static double GetPolygonSignedArea(IReadOnlyList<Point> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            area += (points[index].X * points[next].Y) - (points[next].X * points[index].Y);
        }

        return area / 2d;
    }

    private static List<Point> ProjectCylindricalPoints(IReadOnlyList<Point3D> points, StepAxisPlacement placement)
    {
        var projected = new List<Point>(points.Count);
        double? previousAngle = null;
        var angleOffset = 0d;

        foreach (var point in points)
        {
            var vector = point - placement.Origin;
            var axisDistance = Vector3D.DotProduct(vector, placement.Normal);
            var radial = vector - (placement.Normal * axisDistance);
            var x = Vector3D.DotProduct(radial, placement.XDirection);
            var y = Vector3D.DotProduct(radial, placement.YDirection);
            var angle = Math.Atan2(y, x);

            if (previousAngle.HasValue)
            {
                var delta = angle - previousAngle.Value;
                if (delta > Math.PI)
                {
                    angleOffset -= Math.PI * 2d;
                }
                else if (delta < -Math.PI)
                {
                    angleOffset += Math.PI * 2d;
                }
            }

            previousAngle = angle;
            projected.Add(new Point(angle + angleOffset, axisDistance));
        }

        return projected;
    }

    private static void NormalizeCylindricalLoop(List<Point> loop)
    {
        if (loop.Count == 0)
        {
            return;
        }

        var min = loop.Min(point => point.X);
        for (var index = 0; index < loop.Count; index++)
        {
            loop[index] = new Point(loop[index].X - min, loop[index].Y);
        }
    }

    private static void AlignLoopToOuterRange(List<Point> loop, double outerMin, double outerMax)
    {
        if (loop.Count == 0)
        {
            return;
        }

        var loopCenter = loop.Average(point => point.X);
        var outerCenter = (outerMin + outerMax) / 2d;
        var shiftTurns = Math.Round((outerCenter - loopCenter) / (Math.PI * 2d));
        var shift = shiftTurns * Math.PI * 2d;
        for (var index = 0; index < loop.Count; index++)
        {
            loop[index] = new Point(loop[index].X + shift, loop[index].Y);
        }
    }

    private static StepPlanarFace? BuildTessellatedFace(
        IReadOnlyList<Point> outerLoop,
        IReadOnlyList<IReadOnlyList<Point>> holeLoops,
        Func<Point, Point3D> mapPoint,
        Func<Point, Vector3D> mapNormal)
    {
        if (outerLoop.Count < 3)
        {
            return null;
        }

        var tess = new Tess();
        AddContour(tess, outerLoop);
        foreach (var holeLoop in holeLoops.Where(loop => loop.Count >= 3))
        {
            AddContour(tess, holeLoop);
        }

        tess.Tessellate(
            WindingRule.EvenOdd,
            ElementType.Polygons,
            3,
            null,
            new Vec3 { X = 0, Y = 0, Z = 1 });

        if (tess.VertexCount == 0 || tess.ElementCount == 0)
        {
            return null;
        }

        var vertices = new List<Point3D>(tess.VertexCount);
        var normals = new List<Vector3D>(tess.VertexCount);
        foreach (var vertex in tess.Vertices)
        {
            var point = new Point(vertex.Position.X, vertex.Position.Y);
            vertices.Add(mapPoint(point));
            var normal = mapNormal(point);
            if (normal.LengthSquared < 0.0000001)
            {
                normal = new Vector3D(0, 0, 1);
            }

            normal.Normalize();
            normals.Add(normal);
        }

        var triangleIndices = new List<int>(tess.ElementCount * 3);
        foreach (var element in tess.Elements)
        {
            if (element >= 0)
            {
                triangleIndices.Add(element);
            }
        }

        return triangleIndices.Count >= 3
            ? new StepPlanarFace(vertices, triangleIndices, normals)
            : null;
    }

    private static void AddContour(Tess tess, IReadOnlyList<Point> loop)
    {
        if (loop.Count < 3)
        {
            return;
        }

        var contour = new ContourVertex[loop.Count];
        for (var index = 0; index < loop.Count; index++)
        {
            contour[index].Position = new Vec3
            {
                X = (float)loop[index].X,
                Y = (float)loop[index].Y,
                Z = 0
            };
        }

        tess.AddContour(contour, ContourOrientation.Original);
    }

    private static Dictionary<int, StepEntity> ReadEntities(string path)
    {
        var entities = new Dictionary<int, StepEntity>();
        var current = new System.Text.StringBuilder();

        foreach (var rawLine in File.ReadLines(path))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            current.Append(trimmed);
            if (!trimmed.EndsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var text = current.ToString();
            current.Clear();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var equalsIndex = text.IndexOf('=');
            var openIndex = text.IndexOf('(');
            var closeIndex = text.LastIndexOf(");", StringComparison.Ordinal);
            if (equalsIndex < 0 || openIndex < 0 || closeIndex < 0)
            {
                continue;
            }

            if (!int.TryParse(text[1..equalsIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var type = text[(equalsIndex + 1)..openIndex].Trim().ToUpperInvariant();
            var body = text[(openIndex + 1)..closeIndex];
            entities[id] = new StepEntity(id, type, body);
        }

        return entities;
    }

    private static bool TryParseCartesianPoint(string body, out Point3D point)
    {
        var coordinateStart = body.LastIndexOf('(');
        var coordinateEnd = body.LastIndexOf(')');
        if (coordinateStart < 0 || coordinateEnd <= coordinateStart)
        {
            point = new Point3D();
            return false;
        }

        var coordinateText = body[(coordinateStart + 1)..coordinateEnd];
        var values = coordinateText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d)
            .ToArray();

        if (values.Length < 3)
        {
            point = new Point3D();
            return false;
        }

        point = new Point3D(values[0], values[1], values[2]);
        return true;
    }

    private static bool TryParseDirection(string body, out Vector3D direction)
    {
        var coordinateStart = body.LastIndexOf('(');
        var coordinateEnd = body.LastIndexOf(')');
        if (coordinateStart < 0 || coordinateEnd <= coordinateStart)
        {
            direction = new Vector3D(1, 0, 0);
            return false;
        }

        var coordinateText = body[(coordinateStart + 1)..coordinateEnd];
        var values = coordinateText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d)
            .ToArray();

        if (values.Length < 3)
        {
            direction = new Vector3D(1, 0, 0);
            return false;
        }

        direction = new Vector3D(values[0], values[1], values[2]);
        if (direction.LengthSquared < 0.0000001)
        {
            direction = new Vector3D(1, 0, 0);
            return false;
        }

        direction.Normalize();
        return true;
    }

    private static bool TryParseAxisPlacement(
        string body,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        IReadOnlyDictionary<int, Vector3D> directions,
        out StepAxisPlacement placement)
    {
        var refs = ExtractReferences(body);
        if (refs.Count == 0 || !cartesianPoints.TryGetValue(refs[0], out var origin))
        {
            placement = default;
            return false;
        }

        var normal = refs.Count > 1 && directions.TryGetValue(refs[1], out var parsedNormal)
            ? parsedNormal
            : new Vector3D(0, 0, 1);
        if (normal.LengthSquared < 0.0000001)
        {
            normal = new Vector3D(0, 0, 1);
        }

        normal.Normalize();

        var xDirection = refs.Count > 2 && directions.TryGetValue(refs[2], out var parsedXDirection)
            ? parsedXDirection
            : GetPerpendicular(normal);
        xDirection -= normal * Vector3D.DotProduct(xDirection, normal);
        if (xDirection.LengthSquared < 0.0000001)
        {
            xDirection = GetPerpendicular(normal);
        }

        xDirection.Normalize();
        var yDirection = Vector3D.CrossProduct(normal, xDirection);
        if (yDirection.LengthSquared < 0.0000001)
        {
            yDirection = Vector3D.CrossProduct(normal, GetPerpendicular(normal));
        }

        yDirection.Normalize();
        placement = new StepAxisPlacement(origin, normal, xDirection, yDirection);
        return true;
    }

    private static List<int> ExtractReferences(string text)
    {
        var refs = new List<int>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"#(\d+)");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                refs.Add(id);
            }
        }

        return refs;
    }

    private static bool TryExtractFunctionArguments(string text, string functionName, out string arguments)
    {
        arguments = string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(functionName)}\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var openIndex = text.IndexOf('(', match.Index);
        if (openIndex < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        for (var index = openIndex; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\'')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    arguments = text[(openIndex + 1)..index];
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> SplitTopLevelArguments(string text)
    {
        var arguments = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\'')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (current == ',' && depth == 0)
            {
                arguments.Add(text[start..index].Trim());
                start = index + 1;
            }
        }

        if (start <= text.Length)
        {
            arguments.Add(text[start..].Trim());
        }

        return arguments;
    }

    private static List<int> ParseIntegerList(string text)
    {
        var values = new List<int>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[+-]?\d+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static List<double> ParseDoubleList(string text)
    {
        var values = new List<double>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[+-]?(?:\d+\.?\d*|\.\d+)(?:[Ee][+-]?\d+)?",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static List<List<int>> ParseReferenceRows(string text)
    {
        var rows = new List<List<int>>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\((#[^()]*)\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var refs = ExtractReferences(match.Groups[1].Value);
            if (refs.Count > 0)
            {
                rows.Add(refs);
            }
        }

        if (rows.Count == 0)
        {
            var refs = ExtractReferences(text);
            if (refs.Count > 0)
            {
                rows.Add(refs);
            }
        }

        return rows;
    }

    private static List<List<double>> ParseDoubleRows(string text)
    {
        var rows = new List<List<double>>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\(([^()]+)\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var values = ParseDoubleList(match.Groups[1].Value);
            if (values.Count > 0)
            {
                rows.Add(values);
            }
        }

        if (rows.Count == 0)
        {
            var values = ParseDoubleList(text);
            if (values.Count > 0)
            {
                rows.Add(values);
            }
        }

        return rows;
    }

    private static List<double> ExpandKnots(IReadOnlyList<int> multiplicities, IReadOnlyList<double> knots)
    {
        var expanded = new List<double>();
        if (multiplicities.Count != knots.Count)
        {
            return expanded;
        }

        for (var index = 0; index < knots.Count; index++)
        {
            for (var count = 0; count < multiplicities[index]; count++)
            {
                expanded.Add(knots[index]);
            }
        }

        return expanded;
    }

    private static Point3D? ResolvePoint(
        int reference,
        IReadOnlyDictionary<int, Point3D> cartesianPoints,
        IReadOnlyDictionary<int, Point3D> vertexPoints)
    {
        if (vertexPoints.TryGetValue(reference, out var vertexPoint))
        {
            return vertexPoint;
        }

        if (cartesianPoints.TryGetValue(reference, out var cartesianPoint))
        {
            return cartesianPoint;
        }

        return null;
    }

    private static bool TryParseLastDouble(string text, out double value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"([+-]?(?:\d+\.?\d*|\.\d+)(?:[Ee][+-]?\d+)?)\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0d;
        return false;
    }

    private static bool TryParseTrailingDoubles(string text, int count, out double[] values)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[+-]?(?:\d+\.?\d*|\.\d+)(?:[Ee][+-]?\d+)?",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (matches.Count < count)
        {
            values = Array.Empty<double>();
            return false;
        }

        values = new double[count];
        for (var index = 0; index < count; index++)
        {
            if (!double.TryParse(
                matches[matches.Count - count + index].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out values[index]))
            {
                values = Array.Empty<double>();
                return false;
            }
        }

        return true;
    }

    private static Vector3D GetPerpendicular(Vector3D vector)
    {
        var reference = Math.Abs(Vector3D.DotProduct(vector, new Vector3D(0, 0, 1))) > 0.92
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 0, 1);
        var perpendicular = Vector3D.CrossProduct(vector, reference);
        if (perpendicular.LengthSquared < 0.0000001)
        {
            perpendicular = Vector3D.CrossProduct(vector, new Vector3D(0, 1, 0));
        }

        perpendicular.Normalize();
        return perpendicular;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -Math.PI)
        {
            angle += Math.PI * 2d;
        }

        while (angle > Math.PI)
        {
            angle -= Math.PI * 2d;
        }

        return angle;
    }

    private static double DistanceSquared(Point3D a, Point3D b)
    {
        var delta = a - b;
        return delta.LengthSquared;
    }

    private static string BuildSegmentKey(Point3D start, Point3D end)
    {
        var a = $"{start.X:0.###},{start.Y:0.###},{start.Z:0.###}";
        var b = $"{end.X:0.###},{end.Y:0.###},{end.Z:0.###}";
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private static Rect3D Expand(Rect3D bounds, Point3D point)
    {
        if (bounds.IsEmpty)
        {
            return new Rect3D(point, new Size3D(0.0001, 0.0001, 0.0001));
        }

        bounds.Union(point);
        return bounds;
    }

    private static double GetBoundsDiagonal(Rect3D bounds)
    {
        return Math.Sqrt(
            (bounds.SizeX * bounds.SizeX) +
            (bounds.SizeY * bounds.SizeY) +
            (bounds.SizeZ * bounds.SizeZ));
    }

    private static Point3D GetBoundsCenter(Rect3D bounds)
    {
        return new Point3D(
            bounds.X + (bounds.SizeX / 2d),
            bounds.Y + (bounds.SizeY / 2d),
            bounds.Z + (bounds.SizeZ / 2d));
    }

    private readonly record struct StepEntity(int Id, string Type, string Body);
}
