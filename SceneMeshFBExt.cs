using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StereoKit.Framework;

/// <summary>Loads Meta Quest's saved room mesh into ordinary StereoKit meshes.</summary>
public sealed class SceneMeshFBExt : IStepper
{
	const string ExtSpatialEntity = "XR_FB_spatial_entity";
	const string ExtSpatialQuery  = "XR_FB_spatial_entity_query";
	const string ExtContainer     = "XR_FB_spatial_entity_container";
	const string ExtScene         = "XR_FB_scene";
	const string ExtSceneCapture  = "XR_FB_scene_capture";
	const string ExtMesh          = "XR_META_spatial_entity_mesh";

	readonly Dictionary<ulong, ScenePart> parts = new();
	readonly List<ScenePart> drawList = new();
	readonly HashSet<ulong> pendingRooms = new();
	readonly List<XrUuidEXT> childUuids = new();
	Material material;
	bool initialized, queryRequested = true, captureRequested;
	bool queryInFlight, captureInFlight;
	long queryRequestId, captureRequestId;
	float nextAttemptTime;
	QueryStage queryStage;
	bool roomQueryComplete;
	bool containerAvailable;

	public bool Available { get; private set; }
	public bool Enabled { get; set; } = true;
	public bool ShowSurface { get; set; } = true;
	public bool ShowEdges { get; set; } = true;
	public int MeshCount => drawList.Count;
	public int TriangleCount { get; private set; }
	public string Status { get; private set; } = "Waiting for OpenXR";

	public SceneMeshFBExt()
	{
		if (SK.IsInitialized) Log.Err("SceneMeshFBExt must be constructed before StereoKit is initialized.");
		Backend.OpenXR.RequestExt(ExtSpatialEntity);
		Backend.OpenXR.RequestExt(ExtSpatialQuery);
		Backend.OpenXR.RequestExt(ExtContainer);
		Backend.OpenXR.RequestExt(ExtScene);
		Backend.OpenXR.RequestExt(ExtSceneCapture);
		Backend.OpenXR.RequestExt(ExtMesh);
	}

	public bool Initialize()
	{
		Available = Backend.XRType == BackendXRType.OpenXR
			&& Backend.OpenXR.ExtEnabled(ExtSpatialEntity)
			&& Backend.OpenXR.ExtEnabled(ExtSpatialQuery)
			&& Backend.OpenXR.ExtEnabled(ExtScene)
			&& Backend.OpenXR.ExtEnabled(ExtSceneCapture)
			&& Backend.OpenXR.ExtEnabled(ExtMesh)
			&& LoadBindings();

		if (!Available)
		{
			SetStatus(Backend.XRType == BackendXRType.OpenXR
				? "Meta Scene extensions are unavailable"
				: "Scene mesh requires the Meta OpenXR runtime");
			return true;
		}
		containerAvailable = Backend.OpenXR.ExtEnabled(ExtContainer) && xrGetSpaceContainerFB != null;

		material = new Material("scene_mesh.hlsl")
		{
			Transparency = Transparency.Blend,
			DepthWrite = false,
			FaceCull = Cull.None,
			QueueOffset = 10,
		};
		Backend.OpenXR.OnPollEvent += OnOpenXREvent;
		initialized = true;
		SetStatus("Looking for a saved room mesh...");
		return true;
	}

	public void Step()
	{
		if (!Available || !Enabled) return;
		material.SetFloat("surface_alpha", ShowSurface ? 0.16f : 0);
		material.SetFloat("edge_alpha", ShowEdges ? 0.9f : 0);

		if (captureRequested && !captureInFlight && !queryInFlight && Time.Totalf >= nextAttemptTime) BeginCapture();
		else if (queryRequested && !queryInFlight && !captureInFlight && Time.Totalf >= nextAttemptTime) BeginQuery();

		for (int i = 0; i < drawList.Count; i++)
		{
			ScenePart part = drawList[i];
			if (TryLocate(part.Space, out part.Pose))
				part.Mesh.Draw(material, part.Pose.ToMatrix(), part.Color);
		}
	}

	public void Shutdown()
	{
		if (!initialized) return;
		Backend.OpenXR.OnPollEvent -= OnOpenXREvent;
		ClearParts();
		initialized = false;
	}

	/// <summary>Opens Meta's system-owned Space Setup flow.</summary>
	public void RequestCapture()
	{
		if (!Available) { SetStatus("Room capture is unavailable on this runtime"); return; }
		captureRequested = true;
		SetStatus("Opening Space Setup...");
	}

	/// <summary>Reloads the room mesh currently stored by the system.</summary>
	public void Refresh()
	{
		if (!Available) return;
		queryRequested = true;
		SetStatus("Refreshing saved room mesh...");
	}

	void BeginQuery()
	{
		ClearParts();
		childUuids.Clear();
		pendingRooms.Clear();
		roomQueryComplete = false;
		if (containerAvailable)
		{
			queryStage = QueryStage.Rooms;
			BeginComponentQuery(XrSpaceComponentTypeFB.RoomLayout, "Finding saved rooms...");
		}
		else BeginAllSpacesQuery();
	}

	void BeginComponentQuery(XrSpaceComponentTypeFB component, string message)
	{
		XrSpaceStorageLocationFilterInfoFB storageFilter = new();
		IntPtr storagePtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceStorageLocationFilterInfoFB>());
		IntPtr filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceComponentFilterInfoFB>());
		try
		{
			Marshal.StructureToPtr(storageFilter, storagePtr, false);
			XrSpaceComponentFilterInfoFB filter = new(component, storagePtr);
			Marshal.StructureToPtr(filter, filterPtr, false);
			XrSpaceQueryInfoFB queryInfo = new(filterPtr);
			XrResult result = xrQuerySpacesFB(Backend.OpenXR.Session, ref queryInfo, out queryRequestId);
			if (Succeeded(result))
			{
				queryInFlight = true;
				queryRequested = false;
				SetStatus(message);
			}
			else Retry($"Room query failed: {result}");
		}
		finally
		{
			Marshal.FreeHGlobal(filterPtr);
			Marshal.FreeHGlobal(storagePtr);
		}
	}

	void BeginChildrenQuery()
	{
		queryStage = QueryStage.Children;
		XrSpaceStorageLocationFilterInfoFB storageFilter = new();
		IntPtr storagePtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceStorageLocationFilterInfoFB>());
		GCHandle uuidPin = GCHandle.Alloc(childUuids.ToArray(), GCHandleType.Pinned);
		IntPtr filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceUuidFilterInfoFB>());
		try
		{
			Marshal.StructureToPtr(storageFilter, storagePtr, false);
			XrSpaceUuidFilterInfoFB filter = new((uint)childUuids.Count, uuidPin.AddrOfPinnedObject(), storagePtr);
			Marshal.StructureToPtr(filter, filterPtr, false);
			XrSpaceQueryInfoFB queryInfo = new(filterPtr);
			XrResult result = xrQuerySpacesFB(Backend.OpenXR.Session, ref queryInfo, out queryRequestId);
			if (Succeeded(result)) { queryInFlight = true; queryRequested = false; SetStatus($"Loading {childUuids.Count} room anchors..."); }
			else { queryRequested = true; Retry($"Room-anchor query failed: {result}"); }
		}
		finally { Marshal.FreeHGlobal(filterPtr); uuidPin.Free(); Marshal.FreeHGlobal(storagePtr); }
	}

	void BeginAllSpacesQuery()
	{
		queryStage = QueryStage.All;
		XrSpaceStorageLocationFilterInfoFB storageFilter = new();
		IntPtr filterPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XrSpaceStorageLocationFilterInfoFB>());
		try
		{
			Marshal.StructureToPtr(storageFilter, filterPtr, false);
			XrSpaceQueryInfoFB queryInfo = new(filterPtr);
			XrResult result = xrQuerySpacesFB(Backend.OpenXR.Session, ref queryInfo, out queryRequestId);
			if (Succeeded(result)) { queryInFlight = true; queryRequested = false; SetStatus("Checking all saved scene anchors..."); }
			else Retry($"Saved-anchor query failed: {result}");
		}
		finally { Marshal.FreeHGlobal(filterPtr); }
	}

	void BeginCapture()
	{
		XrSceneCaptureRequestInfoFB captureInfo = new();
		XrResult result = xrRequestSceneCaptureFB(Backend.OpenXR.Session, ref captureInfo, out captureRequestId);
		if (Succeeded(result))
		{
			captureInFlight = true;
			captureRequested = false;
			SetStatus("Complete Space Setup in the system dialog");
		}
		else
		{
			Retry($"Could not open Space Setup: {result}");
			captureRequested = false;
		}
	}

	void Retry(string message) { SetStatus(message); nextAttemptTime = Time.Totalf + 1; }
	void SetStatus(string message) { Status = message; Log.Info($"[SceneMeshFB] {message}"); }

	void OnOpenXREvent(IntPtr eventData)
	{
		XrStructureType type = (XrStructureType)Marshal.ReadInt32(eventData);
		switch (type)
		{
			case XrStructureType.EventSpaceQueryResultsAvailable:
			{
				var e = Marshal.PtrToStructure<XrEventDataSpaceQueryResultsAvailableFB>(eventData);
				if (e.requestId == queryRequestId) RetrieveQueryResults();
				break;
			}
			case XrStructureType.EventSpaceQueryComplete:
			{
				var e = Marshal.PtrToStructure<XrEventDataSpaceQueryCompleteFB>(eventData);
				if (e.requestId != queryRequestId) break;
				queryInFlight = false;
				if (!Succeeded(e.result)) SetStatus($"Room query failed: {e.result}");
				else if (queryStage == QueryStage.Rooms)
				{
					roomQueryComplete = true;
					FinishRoomDiscoveryIfReady();
				}
				else if (parts.Count == 0 && queryStage == QueryStage.Children)
					BeginAllSpacesQuery();
				else if (parts.Count == 0)
					SetStatus("No accessible triangle mesh found. Check Scene permission, then scan/update the room.");
				break;
			}
			case XrStructureType.EventSpaceSetStatusComplete:
			{
				var e = Marshal.PtrToStructure<XrEventDataSpaceSetStatusCompleteFB>(eventData);
				if (pendingRooms.Contains(e.space) && e.componentType == XrSpaceComponentTypeFB.SpaceContainer)
				{
					pendingRooms.Remove(e.space);
					if (Succeeded(e.result)) CollectRoomChildren(e.space);
					else xrDestroySpace(e.space);
					FinishRoomDiscoveryIfReady();
					break;
				}
				if (!parts.TryGetValue(e.space, out ScenePart part)) break;
				if (!Succeeded(e.result)) SetStatus($"Could not enable {e.componentType}: {e.result}");
				else UpdateComponentState(part);
				break;
			}
			case XrStructureType.EventSceneCaptureComplete:
			{
				var e = Marshal.PtrToStructure<XrEventDataSceneCaptureCompleteFB>(eventData);
				if (e.requestId != captureRequestId) break;
				captureInFlight = false;
				if (Succeeded(e.result)) { queryRequested = true; SetStatus("Space Setup complete; loading mesh..."); }
				else SetStatus($"Space Setup did not complete: {e.result}");
				break;
			}
		}
	}

	void RetrieveQueryResults()
	{
		XrSpaceQueryResultsFB results = new();
		XrResult result = xrRetrieveSpaceQueryResultsFB(Backend.OpenXR.Session, queryRequestId, ref results);
		if (!Succeeded(result)) { SetStatus($"Could not size room results: {result}"); return; }
		if (results.resultCountOutput == 0) return;

		int stride = Marshal.SizeOf<XrSpaceQueryResultFB>();
		IntPtr buffer = Marshal.AllocHGlobal(checked((int)results.resultCountOutput * stride));
		try
		{
			results.resultCapacityInput = results.resultCountOutput;
			results.resultCountOutput = 0;
			results.results = buffer;
			result = xrRetrieveSpaceQueryResultsFB(Backend.OpenXR.Session, queryRequestId, ref results);
			if (!Succeeded(result)) { SetStatus($"Could not load room results: {result}"); return; }

			for (int i = 0; i < results.resultCountOutput; i++)
			{
				var item = Marshal.PtrToStructure<XrSpaceQueryResultFB>(IntPtr.Add(buffer, i * stride));
				if (queryStage == QueryStage.Rooms) ProcessRoom(item.space);
				else ProcessSceneAnchor(item);
			}
		}
		finally { Marshal.FreeHGlobal(buffer); }
	}

	void ProcessRoom(ulong space)
	{
		if (!SupportsComponent(space, XrSpaceComponentTypeFB.SpaceContainer)) { xrDestroySpace(space); return; }
		if (IsComponentEnabled(space, XrSpaceComponentTypeFB.SpaceContainer)) CollectRoomChildren(space);
		else
		{
			pendingRooms.Add(space);
			XrSpaceComponentStatusSetInfoFB setInfo = new(XrSpaceComponentTypeFB.SpaceContainer, true);
			XrResult result = xrSetSpaceComponentStatusFB(space, ref setInfo, out _);
			if (!Succeeded(result)) { pendingRooms.Remove(space); xrDestroySpace(space); }
		}
	}

	void CollectRoomChildren(ulong roomSpace)
	{
		XrSpaceContainerFB container = new();
		XrResult result = xrGetSpaceContainerFB(Backend.OpenXR.Session, roomSpace, ref container);
		if (Succeeded(result) && container.uuidCountOutput > 0)
		{
			XrUuidEXT[] uuids = new XrUuidEXT[container.uuidCountOutput];
			GCHandle pin = GCHandle.Alloc(uuids, GCHandleType.Pinned);
			try
			{
				container.uuidCapacityInput = (uint)uuids.Length;
				container.uuidCountOutput = 0;
				container.uuids = pin.AddrOfPinnedObject();
				result = xrGetSpaceContainerFB(Backend.OpenXR.Session, roomSpace, ref container);
				if (Succeeded(result))
					for (int i = 0; i < container.uuidCountOutput; i++)
						if (!childUuids.Contains(uuids[i])) childUuids.Add(uuids[i]);
			}
			finally { pin.Free(); }
		}
		xrDestroySpace(roomSpace);
	}

	void FinishRoomDiscoveryIfReady()
	{
		if (!roomQueryComplete || pendingRooms.Count != 0) return;
		if (childUuids.Count > 0) BeginChildrenQuery();
		else BeginAllSpacesQuery();
	}

	void ProcessSceneAnchor(XrSpaceQueryResultFB item)
	{
		if (parts.ContainsKey(item.space)) return;
		if (!SupportsComponent(item.space, XrSpaceComponentTypeFB.TriangleMesh)) { xrDestroySpace(item.space); return; }
		ScenePart part = new(item.space, item.uuid);
		parts.Add(item.space, part);
		EnableComponent(part, XrSpaceComponentTypeFB.Locatable);
		EnableComponent(part, XrSpaceComponentTypeFB.TriangleMesh);
		UpdateComponentState(part);
	}

	bool SupportsComponent(ulong space, XrSpaceComponentTypeFB wanted)
	{
		uint count = 0;
		XrResult result = xrEnumerateSpaceSupportedComponentsFB(space, 0, out count, IntPtr.Zero);
		if (!Succeeded(result) || count == 0) return false;
		// OpenXR enums have a fixed 32-bit ABI. Marshal.SizeOf<T> rejects
		// managed enum types on some runtimes (notably Android/Mono).
		const int size = sizeof(int);
		IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * size));
		try
		{
			result = xrEnumerateSpaceSupportedComponentsFB(space, count, out count, buffer);
			if (!Succeeded(result)) return false;
			for (int i = 0; i < count; i++)
				if ((XrSpaceComponentTypeFB)Marshal.ReadInt32(buffer, i * size) == wanted) return true;
			return false;
		}
		finally { Marshal.FreeHGlobal(buffer); }
	}

	void EnableComponent(ScenePart part, XrSpaceComponentTypeFB component)
	{
		XrSpaceComponentStatusFB status = new();
		XrResult result = xrGetSpaceComponentStatusFB(part.Space, component, ref status);
		if (!Succeeded(result) || status.enabled != 0 || status.changePending != 0) return;
		XrSpaceComponentStatusSetInfoFB setInfo = new(component, true);
		xrSetSpaceComponentStatusFB(part.Space, ref setInfo, out _);
	}

	void UpdateComponentState(ScenePart part)
	{
		part.Locatable = IsComponentEnabled(part.Space, XrSpaceComponentTypeFB.Locatable);
		part.MeshEnabled = IsComponentEnabled(part.Space, XrSpaceComponentTypeFB.TriangleMesh);
		if (part.Locatable && part.MeshEnabled && part.Mesh == null) LoadMesh(part);
	}

	bool IsComponentEnabled(ulong space, XrSpaceComponentTypeFB component)
	{
		XrSpaceComponentStatusFB status = new();
		return Succeeded(xrGetSpaceComponentStatusFB(space, component, ref status)) && status.enabled != 0;
	}

	void LoadMesh(ScenePart part)
	{
		XrSpaceTriangleMeshGetInfoMETA info = new();
		XrSpaceTriangleMeshMETA data = new();
		XrResult result = xrGetSpaceTriangleMeshMETA(part.Space, ref info, ref data);
		if (!Succeeded(result) || data.vertexCountOutput == 0 || data.indexCountOutput < 3)
		{
			SetStatus($"Could not read room mesh: {result}");
			return;
		}

		XrVector3f[] positions = new XrVector3f[data.vertexCountOutput];
		uint[] sourceIndices = new uint[data.indexCountOutput];
		GCHandle positionPin = GCHandle.Alloc(positions, GCHandleType.Pinned);
		GCHandle indexPin = GCHandle.Alloc(sourceIndices, GCHandleType.Pinned);
		try
		{
			data.vertexCapacityInput = (uint)positions.Length;
			data.vertexCountOutput = 0;
			data.vertices = positionPin.AddrOfPinnedObject();
			data.indexCapacityInput = (uint)sourceIndices.Length;
			data.indexCountOutput = 0;
			data.indices = indexPin.AddrOfPinnedObject();
			result = xrGetSpaceTriangleMeshMETA(part.Space, ref info, ref data);
		}
		finally { positionPin.Free(); indexPin.Free(); }

		if (!Succeeded(result)) { SetStatus($"Could not retrieve room triangles: {result}"); return; }

		// Per-corner barycentric colors let the shader draw portable triangle
		// edges; GPU wireframe mode is unreliable on Quest/OpenGL ES.
		List<Vertex> vertices = new(sourceIndices.Length);
		List<uint> indices = new(sourceIndices.Length);
		Color32[] barycentric = { new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255) };
		for (int i = 0; i + 2 < data.indexCountOutput; i += 3)
		{
			uint ia = sourceIndices[i], ib = sourceIndices[i + 1], ic = sourceIndices[i + 2];
			if (ia >= positions.Length || ib >= positions.Length || ic >= positions.Length) continue;
			Vec3 a = positions[ia].ToVec3(), b = positions[ib].ToVec3(), c = positions[ic].ToVec3();
			Vec3 normal = FaceNormal(a, b, c);
			for (int corner = 0; corner < 3; corner++)
			{
				Vec3 position = corner == 0 ? a : corner == 1 ? b : c;
				indices.Add((uint)vertices.Count);
				vertices.Add(new Vertex(position, normal, Vec2.Zero, barycentric[corner]));
			}
		}

		part.Mesh = new Mesh();
		part.Mesh.SetData(vertices.ToArray(), indices.ToArray(), true);
		drawList.Add(part);
		TriangleCount += indices.Count / 3;
		SetStatus($"Loaded {drawList.Count} mesh part(s), {TriangleCount:N0} triangles");
	}

	static Vec3 FaceNormal(Vec3 a, Vec3 b, Vec3 c)
	{
		Vec3 n = Vec3.Cross(b - a, c - a);
		float length = MathF.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
		return length > 0.000001f ? n / length : Vec3.Up;
	}

	bool TryLocate(ulong space, out Pose pose)
	{
		XrSpaceLocation location = new();
		XrResult result = xrLocateSpace(space, Backend.OpenXR.Space, Backend.OpenXR.Time, ref location);
		if (!Succeeded(result) || (location.locationFlags & XrSpaceLocationFlags.PoseValid) != XrSpaceLocationFlags.PoseValid)
		{
			pose = default;
			return false;
		}
		pose = new Pose(location.pose.position.ToVec3(), new Quat(location.pose.orientation.x, location.pose.orientation.y, location.pose.orientation.z, location.pose.orientation.w));
		return true;
	}

	void ClearParts()
	{
		if (xrDestroySpace != null)
		{
			foreach (ScenePart part in parts.Values) xrDestroySpace(part.Space);
			foreach (ulong room in pendingRooms) xrDestroySpace(room);
		}
		parts.Clear();
		pendingRooms.Clear();
		drawList.Clear();
		TriangleCount = 0;
	}

	static Color ColorFor(XrUuidEXT uuid) => Color.HSV((uuid.a % 1000) / 1000.0f, 0.58f, 1.0f, 1.0f);

	sealed class ScenePart
	{
		public readonly ulong Space;
		public readonly Color Color;
		public bool Locatable, MeshEnabled;
		public Mesh Mesh;
		public Pose Pose;
		public ScenePart(ulong space, XrUuidEXT uuid) { Space = space; Color = ColorFor(uuid); }
	}

	#region OpenXR bindings

	enum QueryStage { Rooms, Children, All }

	enum XrResult : int { Success = 0 }
	static bool Succeeded(XrResult result) => (int)result >= 0;

	enum XrStructureType : uint
	{
		SpaceLocation = 42,
		SpaceComponentStatus = 1000113001,
		EventSpaceSetStatusComplete = 1000113006,
		SpaceComponentStatusSetInfo = 1000113007,
		SpaceQueryInfo = 1000156001,
		SpaceQueryResults = 1000156002,
		SpaceStorageLocationFilterInfo = 1000156003,
		SpaceComponentFilterInfo = 1000156052,
		SpaceUuidFilterInfo = 1000156054,
		EventSpaceQueryResultsAvailable = 1000156103,
		EventSpaceQueryComplete = 1000156104,
		EventSceneCaptureComplete = 1000198001,
		SceneCaptureRequestInfo = 1000198050,
		SpaceContainer = 1000199000,
		SpaceTriangleMeshGetInfo = 1000269001,
		SpaceTriangleMesh = 1000269002,
	}

	enum XrSpaceQueryActionFB : int { Load = 0 }
	enum XrSpaceStorageLocationFB : int { Local = 1 }
	enum XrSpaceComponentTypeFB : int { Locatable = 0, RoomLayout = 6, SpaceContainer = 7, TriangleMesh = 1000269000 }
	[Flags] enum XrSpaceLocationFlags : ulong { OrientationValid = 0x1, PositionValid = 0x2, PoseValid = OrientationValid | PositionValid }

	[StructLayout(LayoutKind.Sequential)] readonly struct XrUuidEXT { public readonly ulong a, b; }
	[StructLayout(LayoutKind.Sequential)] struct XrVector3f { public float x, y, z; public readonly Vec3 ToVec3() => new(x, y, z); }
	[StructLayout(LayoutKind.Sequential)] struct XrQuaternionf { public float x, y, z, w; }
	[StructLayout(LayoutKind.Sequential)] struct XrPosef { public XrQuaternionf orientation; public XrVector3f position; }

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceLocation
	{
		public XrStructureType type; public IntPtr next; public XrSpaceLocationFlags locationFlags; public XrPosef pose;
		public XrSpaceLocation() { type = XrStructureType.SpaceLocation; next = IntPtr.Zero; locationFlags = 0; pose = default; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceComponentFilterInfoFB
	{
		public XrStructureType type; public IntPtr next; public XrSpaceComponentTypeFB componentType;
		public XrSpaceComponentFilterInfoFB(XrSpaceComponentTypeFB component, IntPtr nextFilter) { type = XrStructureType.SpaceComponentFilterInfo; next = nextFilter; componentType = component; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceStorageLocationFilterInfoFB
	{
		public XrStructureType type; public IntPtr next; public XrSpaceStorageLocationFB location;
		public XrSpaceStorageLocationFilterInfoFB() { type = XrStructureType.SpaceStorageLocationFilterInfo; next = IntPtr.Zero; location = XrSpaceStorageLocationFB.Local; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceUuidFilterInfoFB
	{
		public XrStructureType type; public IntPtr next; public uint uuidCount; public IntPtr uuids;
		public XrSpaceUuidFilterInfoFB(uint count, IntPtr values, IntPtr nextFilter) { type = XrStructureType.SpaceUuidFilterInfo; next = nextFilter; uuidCount = count; uuids = values; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceContainerFB
	{
		public XrStructureType type; public IntPtr next; public uint uuidCapacityInput, uuidCountOutput; public IntPtr uuids;
		public XrSpaceContainerFB() { type = XrStructureType.SpaceContainer; next = IntPtr.Zero; uuidCapacityInput = uuidCountOutput = 0; uuids = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceQueryInfoFB
	{
		public XrStructureType type; public IntPtr next; public XrSpaceQueryActionFB queryAction; public uint maxResultCount; public long timeout; public IntPtr filter, excludeFilter;
		public XrSpaceQueryInfoFB(IntPtr filterPtr) { type = XrStructureType.SpaceQueryInfo; next = IntPtr.Zero; queryAction = XrSpaceQueryActionFB.Load; maxResultCount = 1024; timeout = 0; filter = filterPtr; excludeFilter = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)] struct XrSpaceQueryResultFB { public ulong space; public XrUuidEXT uuid; }
	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceQueryResultsFB
	{
		public XrStructureType type; public IntPtr next; public uint resultCapacityInput, resultCountOutput; public IntPtr results;
		public XrSpaceQueryResultsFB() { type = XrStructureType.SpaceQueryResults; next = IntPtr.Zero; resultCapacityInput = resultCountOutput = 0; results = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceComponentStatusFB
	{
		public XrStructureType type; public IntPtr next; public uint enabled, changePending;
		public XrSpaceComponentStatusFB() { type = XrStructureType.SpaceComponentStatus; next = IntPtr.Zero; enabled = changePending = 0; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceComponentStatusSetInfoFB
	{
		public XrStructureType type; public IntPtr next; public XrSpaceComponentTypeFB componentType; public uint enabled; public long timeout;
		public XrSpaceComponentStatusSetInfoFB(XrSpaceComponentTypeFB component, bool value) { type = XrStructureType.SpaceComponentStatusSetInfo; next = IntPtr.Zero; componentType = component; enabled = value ? 1u : 0u; timeout = 0; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceTriangleMeshGetInfoMETA
	{
		public XrStructureType type; public IntPtr next;
		public XrSpaceTriangleMeshGetInfoMETA() { type = XrStructureType.SpaceTriangleMeshGetInfo; next = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSpaceTriangleMeshMETA
	{
		public XrStructureType type; public IntPtr next; public uint vertexCapacityInput, vertexCountOutput; public IntPtr vertices; public uint indexCapacityInput, indexCountOutput; public IntPtr indices;
		public XrSpaceTriangleMeshMETA() { type = XrStructureType.SpaceTriangleMesh; next = IntPtr.Zero; vertexCapacityInput = vertexCountOutput = indexCapacityInput = indexCountOutput = 0; vertices = indices = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)]
	struct XrSceneCaptureRequestInfoFB
	{
		public XrStructureType type; public IntPtr next; public uint requestByteCount; public IntPtr request;
		public XrSceneCaptureRequestInfoFB() { type = XrStructureType.SceneCaptureRequestInfo; next = IntPtr.Zero; requestByteCount = 0; request = IntPtr.Zero; }
	}

	[StructLayout(LayoutKind.Sequential)] struct XrEventDataSpaceQueryResultsAvailableFB { public XrStructureType type; public IntPtr next; public long requestId; }
	[StructLayout(LayoutKind.Sequential)] struct XrEventDataSpaceQueryCompleteFB { public XrStructureType type; public IntPtr next; public long requestId; public XrResult result; }
	[StructLayout(LayoutKind.Sequential)] struct XrEventDataSceneCaptureCompleteFB { public XrStructureType type; public IntPtr next; public long requestId; public XrResult result; }
	[StructLayout(LayoutKind.Sequential)]
	struct XrEventDataSpaceSetStatusCompleteFB
	{
		public XrStructureType type; public IntPtr next; public long requestId; public XrResult result; public ulong space; public XrUuidEXT uuid; public XrSpaceComponentTypeFB componentType; public uint enabled;
	}

	delegate XrResult del_xrQuerySpacesFB(ulong session, ref XrSpaceQueryInfoFB info, out long requestId);
	delegate XrResult del_xrRetrieveSpaceQueryResultsFB(ulong session, long requestId, ref XrSpaceQueryResultsFB results);
	delegate XrResult del_xrGetSpaceComponentStatusFB(ulong space, XrSpaceComponentTypeFB component, ref XrSpaceComponentStatusFB status);
	delegate XrResult del_xrSetSpaceComponentStatusFB(ulong space, ref XrSpaceComponentStatusSetInfoFB info, out long requestId);
	delegate XrResult del_xrEnumerateSpaceSupportedComponentsFB(ulong space, uint componentTypeCapacityInput, out uint componentTypeCountOutput, IntPtr componentTypes);
	delegate XrResult del_xrGetSpaceContainerFB(ulong session, ulong space, ref XrSpaceContainerFB container);
	delegate XrResult del_xrGetSpaceTriangleMeshMETA(ulong space, ref XrSpaceTriangleMeshGetInfoMETA info, ref XrSpaceTriangleMeshMETA mesh);
	delegate XrResult del_xrLocateSpace(ulong space, ulong baseSpace, long time, ref XrSpaceLocation location);
	delegate XrResult del_xrDestroySpace(ulong space);
	delegate XrResult del_xrRequestSceneCaptureFB(ulong session, ref XrSceneCaptureRequestInfoFB info, out long requestId);

	del_xrQuerySpacesFB xrQuerySpacesFB;
	del_xrRetrieveSpaceQueryResultsFB xrRetrieveSpaceQueryResultsFB;
	del_xrGetSpaceComponentStatusFB xrGetSpaceComponentStatusFB;
	del_xrSetSpaceComponentStatusFB xrSetSpaceComponentStatusFB;
	del_xrEnumerateSpaceSupportedComponentsFB xrEnumerateSpaceSupportedComponentsFB;
	del_xrGetSpaceContainerFB xrGetSpaceContainerFB;
	del_xrGetSpaceTriangleMeshMETA xrGetSpaceTriangleMeshMETA;
	del_xrLocateSpace xrLocateSpace;
	del_xrDestroySpace xrDestroySpace;
	del_xrRequestSceneCaptureFB xrRequestSceneCaptureFB;

	bool LoadBindings()
	{
		xrQuerySpacesFB = Backend.OpenXR.GetFunction<del_xrQuerySpacesFB>("xrQuerySpacesFB");
		xrRetrieveSpaceQueryResultsFB = Backend.OpenXR.GetFunction<del_xrRetrieveSpaceQueryResultsFB>("xrRetrieveSpaceQueryResultsFB");
		xrGetSpaceComponentStatusFB = Backend.OpenXR.GetFunction<del_xrGetSpaceComponentStatusFB>("xrGetSpaceComponentStatusFB");
		xrSetSpaceComponentStatusFB = Backend.OpenXR.GetFunction<del_xrSetSpaceComponentStatusFB>("xrSetSpaceComponentStatusFB");
		xrEnumerateSpaceSupportedComponentsFB = Backend.OpenXR.GetFunction<del_xrEnumerateSpaceSupportedComponentsFB>("xrEnumerateSpaceSupportedComponentsFB");
		xrGetSpaceContainerFB = Backend.OpenXR.GetFunction<del_xrGetSpaceContainerFB>("xrGetSpaceContainerFB");
		xrGetSpaceTriangleMeshMETA = Backend.OpenXR.GetFunction<del_xrGetSpaceTriangleMeshMETA>("xrGetSpaceTriangleMeshMETA");
		xrLocateSpace = Backend.OpenXR.GetFunction<del_xrLocateSpace>("xrLocateSpace");
		xrDestroySpace = Backend.OpenXR.GetFunction<del_xrDestroySpace>("xrDestroySpace");
		xrRequestSceneCaptureFB = Backend.OpenXR.GetFunction<del_xrRequestSceneCaptureFB>("xrRequestSceneCaptureFB");
		return xrQuerySpacesFB != null && xrRetrieveSpaceQueryResultsFB != null
			&& xrGetSpaceComponentStatusFB != null && xrSetSpaceComponentStatusFB != null
			&& xrEnumerateSpaceSupportedComponentsFB != null
			&& xrGetSpaceTriangleMeshMETA != null && xrLocateSpace != null
			&& xrDestroySpace != null && xrRequestSceneCaptureFB != null;
	}

	#endregion
}
