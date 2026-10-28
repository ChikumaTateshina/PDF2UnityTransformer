// Vector content (fills, strokes, glyphs, shadings) baked into vertex colours.
// Colours are stored as sRGB; they are linearised here in Linear colour space projects.
// Painting order: triangle order inside a mesh + Renderer.sortingOrder between batches,
// therefore ZWrite is off and the queue is Transparent.
Shader "PDF2Unity/VertexColor"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull [_Cull]
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                half4 c = i.color;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                c.rgb = GammaToLinearSpace(c.rgb);
            #endif
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
