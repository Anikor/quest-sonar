Shader "Hidden/DepthPointRender"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off ZWrite On ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Points;   // xyz = мир, w = расстояние (0 = нет данных)
            float _Size, _FloorY, _HeightRange;
            float _PerFrame, _Slots, _CurSlot;

            struct appdata
            {
                uint vid : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed3 col : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            static const float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };

            float3 Hue(float h)
            {
                return saturate(abs(frac(h + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                uint pid = v.vid / 6;
                float4 p = _Points[pid];
                if (p.w <= 0.0)
                {
                    o.pos = float4(2.0, 2.0, 2.0, 1.0);   // вне экрана: точка отброшена
                    o.col = 0;
                    return o;
                }

                float2 c = corners[v.vid % 6];
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 wp = p.xyz + (right * c.x + up * c.y) * (_Size * 0.5);
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));

                float t = saturate((p.y - _FloorY) / max(_HeightRange, 0.01));
                float slot = floor((float)pid / _PerFrame);
                float age = fmod(_CurSlot - slot + _Slots, _Slots) / _Slots;   // 0 = свежая, ~1 = старая
                o.col = Hue((1.0 - t) * 0.66) * lerp(1.0, 0.3, age);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(i.col, 1.0);
            }
            ENDCG
        }
    }
}
