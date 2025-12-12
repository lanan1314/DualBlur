Shader "Unlit/DualKawaseBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Offset ("Offset", Float) = 1.0
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline"
        }
        
        LOD 100
        ZWrite Off
        Cull Off
        ZTest Always
        
        // Pass 0: DownSample
        Pass
        {
            Name "DownSample"
            
            HLSLPROGRAM
            #pragma vertex Vert_DownSample
            #pragma fragment Frag_DownSample
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            // struct Attributes
            // {
            //     float4 positionOS : POSITION;
            //     float2 uv : TEXCOORD0;
            // };
            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float4 uv01 : TEXCOORD2;
                float4 uv23 : TEXCOORD3;
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float _Offset;
            
            Varyings Vert_DownSample(Attributes input)
            {
                Varyings output;
                
                // // 全屏三角形顶点处理
                // output.positionCS = float4(input.positionOS.xy, 0.0, 1.0);
                //
                // // 转换 UV 坐标
                // output.texcoord = input.uv;
                //
                // #if UNITY_UV_STARTS_AT_TOP
                //     output.texcoord = output.texcoord * float2(1.0, -1.0) + float2(0.0, 1.0);
                // #endif
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                
                float2 uv = output.texcoord;
                
                // 计算降采样时的 TexelSize（减半）
                float2 texelSize = _MainTex_TexelSize.xy * 0.5;
                float2 offset = texelSize * (1.0 + _Offset);
                
                output.uv = uv;
                
                // 计算4个角的UV坐标
                output.uv01.xy = uv - offset; // top right
                output.uv01.zw = uv + offset; // bottom left
                output.uv23.xy = uv + float2(-offset.x, offset.y); // top left
                output.uv23.zw = uv + float2(offset.x, -offset.y); // bottom right
                
                return output;
            }
            
            half4 Frag_DownSample(Varyings input) : SV_Target
            {
                // 中心点权重4，4个角各权重1，总和权重8
                half4 sum = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * 4.0;
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv01.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv01.zw);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv23.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv23.zw);
                
                return sum * 0.125; // 1/8
            }
            ENDHLSL
        }
        
        // Pass 1: UpSample
        Pass
        {
            Name "UpSample"
            
            HLSLPROGRAM
            #pragma vertex Vert_UpSample
            #pragma fragment Frag_UpSample
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            // struct Attributes
            // {
            //     float4 positionOS : POSITION;
            //     float2 uv : TEXCOORD0;
            // };
            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float4 uv01 : TEXCOORD1;
                float4 uv23 : TEXCOORD2;
                float4 uv45 : TEXCOORD3;
                float4 uv67 : TEXCOORD4;
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float _Offset;
            
            Varyings Vert_UpSample(Attributes input)
            {
                Varyings output;
                
                // // 全屏三角形顶点处理
                // output.positionCS = float4(input.positionOS.xy, 0.0, 1.0);
                //
                // // 转换 UV 坐标
                // output.texcoord = input.uv;
                //
                // #if UNITY_UV_STARTS_AT_TOP
                //     output.texcoord = output.texcoord * float2(1.0, -1.0) + float2(0.0, 1.0);
                // #endif
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                
                float2 uv = output.texcoord;
                
                // 计算升采样时的 TexelSize（减半）
                float2 texelSize = _MainTex_TexelSize.xy * 0.5;
                float2 offset = float2(1.0 + _Offset, 1.0 + _Offset);
                
                // 计算8个周围点的UV坐标
                output.uv01.xy = uv + float2(-texelSize.x * 2.0, 0.0) * offset;
                output.uv01.zw = uv + float2(-texelSize.x, texelSize.y) * offset;
                output.uv23.xy = uv + float2(0.0, texelSize.y * 2.0) * offset;
                output.uv23.zw = uv + texelSize * offset;
                output.uv45.xy = uv + float2(texelSize.x * 2.0, 0.0) * offset;
                output.uv45.zw = uv + float2(texelSize.x, -texelSize.y) * offset;
                output.uv67.xy = uv + float2(0.0, -texelSize.y * 2.0) * offset;
                output.uv67.zw = uv - texelSize * offset;
                
                return output;
            }
            
            half4 Frag_UpSample(Varyings input) : SV_Target
            {
                // 8个采样点，其中4个权重为2，4个权重为1，总和权重12
                half4 sum = 0;
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv01.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv01.zw) * 2.0;
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv23.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv23.zw) * 2.0;
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv45.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv45.zw) * 2.0;
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv67.xy);
                sum += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv67.zw) * 2.0;
                
                return sum * 0.0833; // 1/12
            }
            ENDHLSL
        }

        // Pass 2: Copy (用于复制原始图像)
        Pass
        {
            Name "Copy"
            
            HLSLPROGRAM
            #pragma vertex Vert_Copy
            #pragma fragment Frag_Copy
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            
            Varyings Vert_Copy(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }
            
            half4 Frag_Copy(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texcoord);
            }
            ENDHLSL
        }

        // Pass 3: Depth of Field Blend (景深混合)
        Pass
        {
            Name "DepthOfField"
            
            HLSLPROGRAM
            #pragma vertex Vert_DOF
            #pragma fragment Frag_DOF
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };
            
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };
            
            TEXTURE2D(_MainTex);          // 模糊图像
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_OriginalTex);      // 原始清晰图像
            SAMPLER(sampler_OriginalTex);
            
            float _FocusDistance;  // 焦点距离
            float _NearRange;      // 近景清晰范围
            float _FarRange;       // 远景模糊范围
            float4 _CameraParams;  // x=near, y=far, z=far-near, w=1/far
            
            Varyings Vert_DOF(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                
                // 计算屏幕空间坐标用于采样深度
                output.screenPos = ComputeScreenPos(output.positionCS);
                
                return output;
            }
            
            // 将深度值转换为线性深度（世界空间距离）
            float LinearizeDepth(float depth)
            {
                float near = _CameraParams.x;
                float far = _CameraParams.y;
                #if UNITY_REVERSED_Z
                    // DirectX风格：深度值越大越远，已经是线性化的
                    depth = 1.0 - depth;
                #endif
                return near * far / (far - depth * (far - near));
            }

            float ExponentialTransition(float t)
            {
                t = saturate(t);
                // 使用指数曲线，让过渡更自然
                return 1.0 - exp(-t * 3.0); // 调整3.0可以控制曲线陡峭程度
            }

            float UltraSmoothTransition(float t)
            {
                t = saturate(t);
                // 使用更柔和的曲线：1 - (1-t)^4
                float oneMinusT = 1.0 - t;
                return 1.0 - oneMinusT * oneMinusT * oneMinusT * oneMinusT;
            }
            
            // 计算景深混合权重
            // 返回值：0表示完全清晰，1表示完全模糊
            float CalculateDOFWeight(float linearDepth)
            {
                // float linearDepth = LinearizeDepth(depth);
                float focusDist = _FocusDistance;
                
                // 计算距离焦点的距离
                float distFromFocus = linearDepth - focusDist;

                // 焦点容差区域：焦点附近保持清晰
                const float focusTolerance = 0.3; // 焦点容差（单位：米）
                if (abs(distFromFocus) < focusTolerance)
                {
                    return 0.0; // 焦点区域完全清晰
                }
                
                // 近景：如果距离小于焦点距离，在近景范围内保持清晰
                // if (linearDepth < focusDist)
                if (distFromFocus < 0.0)
                {
                    float nearBlurStart = focusDist - _NearRange;
                    if (nearBlurStart < 0.0) nearBlurStart = 0.0;
                    
                    if (linearDepth > nearBlurStart)
                    {
                        // 在近景范围内，根据距离计算模糊权重
                        float t = (linearDepth - nearBlurStart) / _NearRange;
                        t = saturate(t);
                        return UltraSmoothTransition(t);
                        // return smoothstep(0.0, 1.0, t);
                    }
                    return 0.0; // 近景清晰
                }
                // 远景：超过焦点距离，根据距离计算模糊权重
                else
                {
                    float farBlurStart = focusDist + _FarRange;
                    if (linearDepth < farBlurStart)
                    {
                        // 在过渡范围内
                        float t = (linearDepth - focusDist) / _FarRange;
                        t = saturate(t);
                        return UltraSmoothTransition(t);
                        // return smoothstep(0.0, 1.0, t);
                    }
                    return 1.0; // 远景完全模糊
                }
            }
            
            half4 Frag_DOF(Varyings input) : SV_Target
            {
                // 采样深度
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float depth = SampleSceneDepth(screenUV);
                // float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, sampler_CameraDepthTexture , screenUV);
                float linearDepth = LinearizeDepth(depth);
                // 计算混合权重
                float blurWeight = CalculateDOFWeight(linearDepth);

                // return half4(blurWeight, blurWeight, blurWeight, 1.0); // 可视化权重
                // return half4(linearDepth / 100.0, 0, 0, 1.0); // 可视化深度
                
                // 采样原始清晰图像和模糊图像
                half4 originalColor = SAMPLE_TEXTURE2D(_OriginalTex, sampler_OriginalTex, input.texcoord);
                half4 blurredColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.texcoord);
                
                // 根据权重混合
                return lerp(originalColor, blurredColor, blurWeight);
            }
            ENDHLSL
        }
    }
}