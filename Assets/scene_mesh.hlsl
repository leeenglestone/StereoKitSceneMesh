#include <stereokit.hlsli>

//--name = app/scene_mesh

//--surface_alpha = 0.16
float surface_alpha;
//--edge_alpha = 0.9
float edge_alpha;

struct vsIn {
	float4 pos   : SV_POSITION;
	float3 norm  : NORMAL;
	float2 uv    : TEXCOORD0;
	float4 color : COLOR0;
};

struct psIn {
	float4 pos     : SV_POSITION;
	float3 normal  : TEXCOORD0;
	float3 bary    : TEXCOORD1;
	float4 tint    : COLOR0;
	uint   view_id : SV_RenderTargetArrayIndex;
};

psIn vs(vsIn input, uint id : SV_InstanceID) {
	psIn o;
	o.view_id = id % sk_view_count;
	id = id / sk_view_count;
	float4 world = mul(input.pos, sk_inst[id].world);
	o.pos    = mul(world, sk_viewproj[o.view_id]);
	o.normal = normalize(mul(float4(input.norm, 0), sk_inst[id].world).xyz);
	o.bary   = input.color.rgb;
	o.tint   = sk_inst[id].color;
	return o;
}

float4 ps(psIn input) : SV_TARGET {
	float nearestEdge = min(input.bary.x, min(input.bary.y, input.bary.z));
	float edgeWidth   = max(fwidth(nearestEdge) * 1.00, 0.0001);
	float edge        = 1.0 - smoothstep(edgeWidth, edgeWidth * 2.0, nearestEdge);
	float alpha       = max(surface_alpha, edge * edge_alpha);
	if (alpha <= 0.001) discard;
	float light = 0.55 + 0.45 * abs(dot(normalize(input.normal), normalize(float3(0.35, 0.8, -0.3))));
	float3 surface = input.tint.rgb * light;
	float3 edgeColor = input.tint.rgb * 0.28;
	return float4(lerp(surface, edgeColor, edge), alpha * input.tint.a);
}
