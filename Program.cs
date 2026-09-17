using StereoKit;
using StereoKit.Framework;

namespace StereoKitSceneMesh;

class Program
{
	static void Main(string[] args)
	{
		// Extension steppers must exist before SK.Initialize so they can add
		// their extensions to the OpenXR instance creation request.
		SK.AddStepper(new PassthroughFBExt());
		SceneMeshFBExt sceneMesh = SK.AddStepper(new SceneMeshFBExt());

		SKSettings settings = new SKSettings
		{
			appName = "StereoKitSceneMesh",
			blendPreference = DisplayBlend.AnyTransparent,
		};
		if (!SK.Initialize(settings)) return;

		Pose cubePose = new Pose(0, 0, -0.5f);
		Model cube = Model.FromMesh(Mesh.GenerateRoundedCube(Vec3.One * 0.1f, 0.02f), Material.UI);
		Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
		Material floorMaterial = new Material("floor.hlsl");
		floorMaterial.Transparency = Transparency.Blend;
		Pose meshWindowPose = new Pose(0.35f, 0, -0.55f);

		SK.Run(() =>
		{
			if (Device.DisplayBlend == DisplayBlend.Opaque)
				Mesh.Cube.Draw(floorMaterial, floorTransform);

			UI.Handle("Cube", ref cubePose, cube.Bounds);
			cube.Draw(cubePose.ToMatrix());

			UI.WindowBegin("World mesh", ref meshWindowPose, new Vec2(0.36f, 0));
			UI.Label(sceneMesh.Status, true);
			UI.Label($"Parts: {sceneMesh.MeshCount}   Triangles: {sceneMesh.TriangleCount:N0}");
			bool showSurface = sceneMesh.ShowSurface;
			bool showEdges = sceneMesh.ShowEdges;
			if (UI.Toggle("Translucent surface", ref showSurface)) sceneMesh.ShowSurface = showSurface;
			if (UI.Toggle("Triangle edges", ref showEdges)) sceneMesh.ShowEdges = showEdges;
			if (UI.Button("Refresh saved mesh")) sceneMesh.Refresh();
			UI.SameLine();
			if (UI.Button("Scan/update room")) sceneMesh.RequestCapture();
			UI.WindowEnd();
		});
	}
}
