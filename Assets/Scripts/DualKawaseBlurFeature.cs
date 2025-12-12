using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace XPostProcessing
{
    [System.Serializable]
    public class DualKawaseBlurSettings
    {
        [Range(0.0f, 15.0f)]    // 模糊半径
        public float BlurRadius = 1.0f;

        [Range(1, 10)]     // 迭代次数
        public int Iteration = 2;

        [Range(1, 10)]     // 降采样倍数
        public float RTDownScaling = 2.0f;
        
        // 景深参数
        [Header("Depth of Field")]
        public bool EnableDepthOfField = true;
        [Range(0.1f, 50.0f)]
        public float FocusDistance = 10.0f;  // 焦点距离
        [Range(0.1f, 10.0f)]
        public float NearRange = 0.1f;       // 近景清晰范围
        [Range(0.1f, 20.0f)]
        public float FarRange = 10.0f;        // 远景模糊范围

        public RenderPassEvent renderEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public class DualKawaseBlurFeature : ScriptableRendererFeature
    {
        [SerializeField] private DualKawaseBlurSettings settings = new DualKawaseBlurSettings();
        private DualKawaseBlurPass blurPass;

        public override void Create()
        {
            // 创建 RenderPass 并初始化注入事件和输入
            blurPass = new DualKawaseBlurPass(settings);
            blurPass.renderPassEvent = settings.renderEvent;
            blurPass.ConfigureInput(ScriptableRenderPassInput.Color);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(blurPass);
        }

        protected override void Dispose(bool disposing)
        {
            blurPass?.Dispose();
        }

        class DualKawaseBlurPass : ScriptableRenderPass
        {
            private const string PROFILER_TAG = "DualKawaseBlur";
            private const int k_MaxPyramidSize = 16;

            private static readonly int ShaderIDMainTex = Shader.PropertyToID("_MainTex");
            private static readonly int ShaderIDBlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
            private static readonly MaterialPropertyBlock s_PropertyBlock = new MaterialPropertyBlock();

            private DualKawaseBlurSettings settings;
            private Material blurMaterial;

            private struct Level
            {
                internal int down;
                internal int up;
            }

            private Level[] m_Pyramid;

            private static class ShaderIDs
            {
                internal static readonly int BlurOffset = Shader.PropertyToID("_Offset");
                // 景深相关Shader属性
                internal static readonly int DepthTex = Shader.PropertyToID("_CameraDepthTexture");
                internal static readonly int OriginalTex = Shader.PropertyToID("_OriginalTex");
                internal static readonly int FocusDistance = Shader.PropertyToID("_FocusDistance");
                internal static readonly int NearRange = Shader.PropertyToID("_NearRange");
                internal static readonly int FarRange = Shader.PropertyToID("_FarRange");
                internal static readonly int CameraParams = Shader.PropertyToID("_CameraParams");
            }

            public DualKawaseBlurPass(DualKawaseBlurSettings settings)
            {
                this.settings = settings;
                profilingSampler = new ProfilingSampler(PROFILER_TAG);
                requiresIntermediateTexture = true;     // 告诉 URP 我们需要一个中间纹理

                // 预生成金字塔纹理ID
                m_Pyramid = new Level[k_MaxPyramidSize];
                for (int i = 0; i < k_MaxPyramidSize; i++)
                {
                    m_Pyramid[i] = new Level
                    {
                        down = Shader.PropertyToID("_BlurMipDown" + i),
                        up = Shader.PropertyToID("_BlurMipUp" + i)
                    };
                }

                Shader shader = Shader.Find("Unlit/DualKawaseBlur");
                if (shader != null)
                {
                    blurMaterial = new Material(shader);
                }
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (blurMaterial == null)
                    return;
                
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                if (cameraData.isSceneViewCamera || cameraData.isPreviewCamera)
                {
                    return;
                }

                // Clamp 迭代次数，避免越界
                int iterationCount = Mathf.Clamp(settings.Iteration, 1, k_MaxPyramidSize);

                
                // 获取当前纹理
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                    return;
                

                TextureHandle cameraColor = resourceData.activeColorTexture;
                TextureDesc cameraDesc = renderGraph.GetTextureDesc(cameraColor);
                
                // 保存原始清晰图像（如果启用景深）
                TextureHandle originalColor = cameraColor;
                if (settings.EnableDepthOfField)
                {
                    TextureDesc originalDesc = cameraDesc;
                    originalDesc.name = "DualKawaseOriginal";
                    originalColor = renderGraph.CreateTexture(originalDesc);
        
                    // 复制原始图像
                    using (var builder = renderGraph.AddRasterRenderPass<DualKawasePassData>("Copy Original", out var passData, profilingSampler))
                    {
                        passData.material = blurMaterial;
                        passData.source = cameraColor;
                        passData.passIndex = 2; // 使用Pass 2进行简单复制

                        builder.UseTexture(passData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(originalColor, 0, AccessFlags.Write);

                        builder.SetRenderFunc((DualKawasePassData data, RasterGraphContext context) =>
                        {
                            ExecuteBlurPass(context.cmd, data.source, data.material, data.passIndex);
                        });
                    }
                }

                // 设置Shader模糊偏移
                blurMaterial.SetFloat(ShaderIDs.BlurOffset, settings.BlurRadius);

                TextureHandle[] downTextures = new TextureHandle[iterationCount];
                TextureHandle[] upTextures = new TextureHandle[Mathf.Max(0, iterationCount - 1)];

                // 计算第一层尺寸
                float downScale = Mathf.Max(1f, settings.RTDownScaling);
                int width = cameraDesc.width > 0 ? cameraDesc.width : 1;
                int height = cameraDesc.height > 0 ? cameraDesc.height : 1;
                width = Mathf.Max(1, Mathf.RoundToInt(width / downScale));
                height = Mathf.Max(1, Mathf.RoundToInt(height / downScale));

                TextureHandle lastDown = cameraColor;

                // =============== 降采样 =================
                for (int i = 0; i < iterationCount; ++i)
                {
                    TextureDesc levelDesc = cameraDesc;     // 创建临时纹理
                    levelDesc.name = $"DualKawaseDown{i}";
                    levelDesc.width = width;
                    levelDesc.height = height;
                    levelDesc.msaaSamples = MSAASamples.None;
                    levelDesc.bindTextureMS = false;
                    levelDesc.depthBufferBits = DepthBits.None;
                    levelDesc.clearBuffer = false;
                    levelDesc.useMipMap = false;
                    levelDesc.autoGenerateMips = false;
                    levelDesc.enableRandomWrite = false;

                    TextureHandle downTexture = renderGraph.CreateTexture(levelDesc);
                    downTextures[i] = downTexture;

                    // 添加一个 Raster Render Pass，用 Pass 0 执行降采样
                    using (var builder = renderGraph.AddRasterRenderPass<DualKawasePassData>($"DualKawase Down {i}", out var passData, profilingSampler))
                    {
                        passData.material = blurMaterial;
                        passData.source = lastDown;
                        passData.passIndex = 0;

                        builder.UseTexture(passData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(downTexture, 0, AccessFlags.Write);

                        builder.SetRenderFunc((DualKawasePassData data, RasterGraphContext context) =>
                        {
                            ExecuteBlurPass(context.cmd, data.source, data.material, data.passIndex);
                        });
                    }

                    lastDown = downTexture;

                    // 预先创建对应的上采样纹理
                    if (i < iterationCount - 1)
                    {
                        TextureDesc upDesc = levelDesc;
                        upDesc.name = $"DualKawaseUp{i}";
                        upTextures[i] = renderGraph.CreateTexture(upDesc);
                    }

                    width = Mathf.Max(1, width / 2);
                    height = Mathf.Max(1, height / 2);
                }

                // =============== 升采样 =================
                TextureHandle lastUp = downTextures[iterationCount - 1];

                for (int i = iterationCount - 2; i >= 0; --i)
                {
                    TextureHandle upTexture = upTextures[i];

                    using (var builder = renderGraph.AddRasterRenderPass<DualKawasePassData>($"DualKawase Up {i}", out var passData, profilingSampler))
                    {
                        passData.material = blurMaterial;
                        passData.source = lastUp;
                        passData.passIndex = 1;

                        builder.UseTexture(passData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(upTexture, 0, AccessFlags.Write);

                        builder.SetRenderFunc((DualKawasePassData data, RasterGraphContext context) =>
                        {
                            ExecuteBlurPass(context.cmd, data.source, data.material, data.passIndex);
                        });
                    }

                    lastUp = upTexture;
                }

                // =============== 混合 =================
                // using (var builder = renderGraph.AddRasterRenderPass<DualKawasePassData>("DualKawase Resolve", out var passData, profilingSampler))
                // {
                //     passData.material = blurMaterial;
                //     passData.source = lastUp;
                //     passData.passIndex = 1;
                //
                //     builder.UseTexture(passData.source, AccessFlags.Read);
                //     builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                //
                //     builder.SetRenderFunc((DualKawasePassData data, RasterGraphContext context) =>
                //     {
                //         ExecuteBlurPass(context.cmd, data.source, data.material, data.passIndex);
                //     });
                // }
                if (settings.EnableDepthOfField)
                {
                    // 获取深度纹理
                    TextureHandle depthTexture = resourceData.activeDepthTexture;
                    
                    // 计算相机参数（用于深度转换）
                    float near = cameraData.camera.nearClipPlane;
                    float far = cameraData.camera.farClipPlane;
                    Vector4 cameraParams = new Vector4(near, far, far - near, 1.0f / far);
            
                    // 设置景深参数
                    blurMaterial.SetFloat(ShaderIDs.FocusDistance, settings.FocusDistance);
                    blurMaterial.SetFloat(ShaderIDs.NearRange, settings.NearRange);
                    blurMaterial.SetFloat(ShaderIDs.FarRange, settings.FarRange);
                    blurMaterial.SetVector(ShaderIDs.CameraParams, cameraParams);
            
                    using (var builder = renderGraph.AddRasterRenderPass<DepthOfFieldPassData>("Depth of Field Blend", out var passData, profilingSampler))
                    {
                        passData.material = blurMaterial;
                        passData.originalTex = originalColor;
                        passData.blurredTex = lastUp;
                        passData.depthTex = depthTexture;
                        passData.passIndex = 3; // 使用Pass 3进行景深混合
            
                        builder.UseTexture(passData.originalTex, AccessFlags.Read);
                        builder.UseTexture(passData.blurredTex, AccessFlags.Read);
                        builder.UseTexture(passData.depthTex, AccessFlags.Read);
                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
            
                        builder.SetRenderFunc((DepthOfFieldPassData data, RasterGraphContext context) =>
                        {
                            ExecuteDepthOfFieldPass(context.cmd, data, data.material);
                        });
                    }
                }
                else
                {
                    // 不使用景深，直接输出模糊结果
                    using (var builder = renderGraph.AddRasterRenderPass<DualKawasePassData>("DualKawase Resolve", out var passData, profilingSampler))
                    {
                        passData.material = blurMaterial;
                        passData.source = lastUp;
                        passData.passIndex = 1;
            
                        builder.UseTexture(passData.source, AccessFlags.Read);
                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
            
                        builder.SetRenderFunc((DualKawasePassData data, RasterGraphContext context) =>
                        {
                            ExecuteBlurPass(context.cmd, data.source, data.material, data.passIndex);
                        });
                    }
                }
            }

            // 模糊PassData类
            private class DualKawasePassData
            {
                internal Material material;
                internal TextureHandle source;
                internal int passIndex;
            }
            
            // 景深PassData类
            private class DepthOfFieldPassData
            {
                internal Material material;
                internal TextureHandle originalTex;
                internal TextureHandle blurredTex;
                internal TextureHandle depthTex;
                internal int passIndex;
            }
            
            // 景深执行函数
            private static void ExecuteDepthOfFieldPass(RasterCommandBuffer cmd, DepthOfFieldPassData data, Material material)
            {
                if (material == null)
                    return;

                s_PropertyBlock.Clear();
    
                if (data.originalTex.IsValid())
                    s_PropertyBlock.SetTexture(ShaderIDs.OriginalTex, data.originalTex);
    
                if (data.blurredTex.IsValid())
                    s_PropertyBlock.SetTexture(ShaderIDMainTex, data.blurredTex);
    
                if (data.depthTex.IsValid())
                    s_PropertyBlock.SetTexture(ShaderIDs.DepthTex, data.depthTex);

                s_PropertyBlock.SetVector(ShaderIDBlitScaleBias, new Vector4(1f, 1f, 0f, 0f));

                cmd.DrawProcedural(Matrix4x4.identity, material, data.passIndex, MeshTopology.Triangles, 3, 1, s_PropertyBlock);
            }

            // 模糊执行函数
            private static void ExecuteBlurPass(RasterCommandBuffer cmd, RTHandle source, Material material, int passIndex)
            {
                if (material == null)
                    return;

                s_PropertyBlock.Clear();

                if (source != null)
                    s_PropertyBlock.SetTexture(ShaderIDMainTex, source);

                s_PropertyBlock.SetVector(ShaderIDBlitScaleBias, new Vector4(1f, 1f, 0f, 0f));

                cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1, s_PropertyBlock);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (blurMaterial == null)
                    return;

                int iterationCount = Mathf.Clamp(settings.Iteration, 1, k_MaxPyramidSize);

                CommandBuffer cmd = CommandBufferPool.Get(PROFILER_TAG);
                using (new ProfilingScope(cmd, profilingSampler))
                {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    RTHandle source = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    var desc = renderingData.cameraData.cameraTargetDescriptor;

                    int tw = Mathf.Max(1, (int)(desc.width / settings.RTDownScaling));
                    int th = Mathf.Max(1, (int)(desc.height / settings.RTDownScaling));

                    blurMaterial.SetFloat(ShaderIDs.BlurOffset, settings.BlurRadius);

                    RTHandle lastDown = source;
                    RTHandle[] downHandles = new RTHandle[iterationCount];
                    RTHandle[] upHandles = new RTHandle[iterationCount];

                    // 降采样
                    for (int i = 0; i < iterationCount; i++)
                    {
                        RenderTextureDescriptor rtDesc = desc;
                        rtDesc.width = tw;
                        rtDesc.height = th;
                        rtDesc.depthBufferBits = 0;

                        downHandles[i] = RTHandles.Alloc(rtDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BlurMipDown" + i);
                        upHandles[i] = RTHandles.Alloc(rtDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_BlurMipUp" + i);

                        Blitter.BlitCameraTexture(cmd, lastDown, downHandles[i], blurMaterial, 0);
                        lastDown = downHandles[i];

                        tw = Mathf.Max(tw / 2, 1);
                        th = Mathf.Max(th / 2, 1);
                    }

                    // 升采样
                    RTHandle lastUp = downHandles[iterationCount - 1];

                    for (int i = iterationCount - 2; i >= 0; i--)
                    {
                        Blitter.BlitCameraTexture(cmd, lastUp, upHandles[i], blurMaterial, 1);
                        lastUp = upHandles[i];
                    }

                    // 输出回源颜色缓冲
                    Blitter.BlitCameraTexture(cmd, lastUp, source, blurMaterial, 1);

                    // 释放所有临时 RT
                    for (int i = 0; i < iterationCount; i++)
                    {
                        if (downHandles[i] != null && downHandles[i] != lastUp)
                            RTHandles.Release(downHandles[i]);

                        if (upHandles[i] != null && upHandles[i] != lastUp)
                            RTHandles.Release(upHandles[i]);
                    }
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public void Dispose()
            {
                if (blurMaterial != null)
                {
                    CoreUtils.Destroy(blurMaterial);
                    blurMaterial = null;
                }
            }
        }
    }
}