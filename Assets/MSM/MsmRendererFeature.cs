using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class MsmRendererFeature : ScriptableRendererFeature
{
    [SerializeField] MsmSettings momentsSettings = new();

    private MsmRenderPass msmPass;
    private RestoreShadowKeywordsPass shadowRestorePass;

    static GlobalKeyword MainLightShadows;
    static GlobalKeyword MainLightShadowCascades;
    static GlobalKeyword MainLightShadowScreen;

    public override void Create()
    {
        MainLightShadows = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
        MainLightShadowCascades = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
        MainLightShadowScreen = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_SCREEN");

        msmPass = new MsmRenderPass(momentsSettings)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 1
        };
        shadowRestorePass = new RestoreShadowKeywordsPass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.isPreviewCamera)
            return;

        bool allowMainLightShadows = renderingData.shadowData.supportsMainLightShadows
            && renderingData.lightData.mainLightIndex != -1;

        if (allowMainLightShadows && momentsSettings != null && momentsSettings.computeShader != null)
        {
            renderer.EnqueuePass(msmPass);
            renderer.EnqueuePass(shadowRestorePass);
        }
    }

    public enum BenchmarkMode
    {
        FullMSM,
        AConstant,
        BMoments,
        CMomentsAndSAT
    }

    [Serializable]
    public class MsmSettings
    {
        public ComputeShader computeShader;
        [Tooltip("Temporary A/B/C comparison. All benchmark modes output visibility 0.5.")]
        public BenchmarkMode benchmarkMode = BenchmarkMode.FullMSM;
    }

    private class MsmRenderPass : ScriptableRenderPass
    {
        readonly MsmSettings settings;

        public MsmRenderPass(MsmSettings settings)
        {
            this.settings = settings;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        private class MomentsPassData
        {
            public ComputeShader shader;
            public int kernel;
            public TextureHandle input;
            public TextureHandle output;
            public int groupsX;
            public int groupsY;
        }

        private class TilePassData
        {
            public ComputeShader shader;
            public int kernel;
            public TextureHandle input;
            public TextureHandle output;
            public TextureHandle rows;
            public TextureHandle columns;
            public TextureHandle totals;
            public int groupsX;
            public int groupsY;
        }

        private class ResolvePassData
        {
            public bool fixedVisibility;
            public ComputeShader shader;
            public int kernel;
            public TextureHandle cameraDepthTexture;
            public TextureHandle shadowDepthTexture;
            public TextureHandle satTexture;
            public TextureHandle output;
            public Matrix4x4 view;
            public Matrix4x4 projection;
            public int groupsX;
            public int groupsY;
        }

        static TextureHandle CreateSATTexture(RenderGraph graph, int width, int height, string name)
        {
            return graph.CreateTexture(new TextureDesc(width, height)
            {
                name = name,
                colorFormat = GraphicsFormat.R32G32B32A32_UInt,
                enableRandomWrite = true,
                clearBuffer = false
            });
        }

        // S.1 and S.4 recompute moments to avoid a full intermediate texture.
        TextureHandle RecordTiledSAT(RenderGraph graph, TextureHandle shadowDepth)
        {
            const int tileSize = 16;
            var desc = graph.GetTextureDesc(shadowDepth);
            int tilesX = (desc.width + tileSize - 1) / tileSize;
            int tilesY = (desc.height + tileSize - 1) / tileSize;
            var rows = CreateSATTexture(graph, tilesX, desc.height, "SAT Tile Row Summaries");
            var columns = CreateSATTexture(graph, desc.width, tilesY, "SAT Tile Column Summaries");
            var totals = CreateSATTexture(graph, tilesX, tilesY, "SAT Tile Strip Totals");
            var result = CreateSATTexture(graph, desc.width, desc.height, "SAT Tiled Result");

            using (var builder = graph.AddComputePass<TilePassData>("SAT S.1: Local Boundaries", out var data))
            {
                data.shader = settings.computeShader;
                data.kernel = data.shader.FindKernel("BuildTileSummaries");
                data.input = shadowDepth;
                data.rows = rows;
                data.columns = columns;
                data.groupsX = tilesX;
                data.groupsY = tilesY;
                builder.UseTexture(data.input, AccessFlags.Read);
                builder.UseTexture(data.rows, AccessFlags.Write);
                builder.UseTexture(data.columns, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TilePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_ShadowDepth", data.input);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileRowsOutput", data.rows);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileColumnsOutput", data.columns);
                    context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, data.groupsY, 1);
                });
            }

            using (var builder = graph.AddComputePass<TilePassData>("SAT S.2: Complete Columns", out var data))
            {
                data.shader = settings.computeShader;
                data.kernel = data.shader.FindKernel("CompleteTileColumns");
                data.columns = columns;
                data.totals = totals;
                data.groupsX = tilesX;
                builder.UseTexture(data.columns, AccessFlags.ReadWrite);
                builder.UseTexture(data.totals, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TilePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileColumnsOutput", data.columns);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileTotalsOutput", data.totals);
                    context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, 1, 1);
                });
            }

            using (var builder = graph.AddComputePass<TilePassData>("SAT S.3: Complete Rows", out var data))
            {
                data.shader = settings.computeShader;
                data.kernel = data.shader.FindKernel("CompleteTileRows");
                data.rows = rows;
                data.totals = totals;
                data.groupsX = (desc.height + 63) / 64;
                builder.UseTexture(data.rows, AccessFlags.ReadWrite);
                builder.UseTexture(data.totals, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TilePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileRowsOutput", data.rows);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileTotalsInput", data.totals);
                    context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, 1, 1);
                });
            }

            using (var builder = graph.AddComputePass<TilePassData>("SAT S.4: Reconstruct", out var data))
            {
                data.shader = settings.computeShader;
                data.kernel = data.shader.FindKernel("BuildTileSAT");
                data.input = shadowDepth;
                data.output = result;
                data.rows = rows;
                data.columns = columns;
                data.groupsX = tilesX;
                data.groupsY = tilesY;
                builder.UseTexture(data.input, AccessFlags.Read);
                builder.UseTexture(data.rows, AccessFlags.Read);
                builder.UseTexture(data.columns, AccessFlags.Read);
                builder.UseTexture(data.output, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (TilePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_ShadowDepth", data.input);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileRowsInput", data.rows);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileColumnsInput", data.columns);
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_TileOutput", data.output);
                    context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, data.groupsY, 1);
                });
            }
            return result;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (settings == null || settings.computeShader == null)
                return;

            var resources = frameData.Get<UniversalResourceData>();
            var shadowTexture = resources.mainShadowsTexture;
            if (!shadowTexture.IsValid())
                return;

            TextureHandle satTexture = default;
            if (settings.benchmarkMode == BenchmarkMode.FullMSM
                || settings.benchmarkMode == BenchmarkMode.CMomentsAndSAT)
            {
                satTexture = RecordTiledSAT(renderGraph, shadowTexture);
            }
            else if (settings.benchmarkMode == BenchmarkMode.BMoments)
            {
                var inputDesc = renderGraph.GetTextureDesc(shadowTexture);
                var moments = CreateSATTexture(renderGraph, inputDesc.width, inputDesc.height, "Shadow Moments");
                using (var builder = renderGraph.AddComputePass<MomentsPassData>("Shadow Moments", out var data))
                {
                    data.shader = settings.computeShader;
                    data.kernel = data.shader.FindKernel("CSMain");
                    data.input = shadowTexture;
                    data.output = moments;
                    data.groupsX = (inputDesc.width + 7) / 8;
                    data.groupsY = (inputDesc.height + 7) / 8;
                    builder.UseTexture(data.input, AccessFlags.Read);
                    builder.UseTexture(data.output, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (MomentsPassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_ShadowDepth", data.input);
                        context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_MomentsOutput", data.output, 0);
                        context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, data.groupsY, 1);
                    });
                }
            }

            bool fixedVisibility = settings.benchmarkMode != BenchmarkMode.FullMSM;
            var cameraData = frameData.Get<UniversalCameraData>();
            var cameraDesc = cameraData.cameraTargetDescriptor;
            var visibilityDesc = new TextureDesc(cameraDesc.width, cameraDesc.height)
            {
                name = "Shadow Visibility",
                colorFormat = GraphicsFormat.R16_SFloat,
                enableRandomWrite = true,
                clearBuffer = false
            };
            var visibility = renderGraph.CreateTexture(visibilityDesc);

            using (var builder = renderGraph.AddComputePass<ResolvePassData>("Resolve Screen Space Shadows", out var data))
            {
                data.shader = settings.computeShader;
                data.fixedVisibility = fixedVisibility;
                data.kernel = data.shader.FindKernel(fixedVisibility ? "CSBenchmarkVisibility" : "CSResolveShadows");
                data.cameraDepthTexture = resources.cameraDepthTexture;
                data.shadowDepthTexture = shadowTexture;
                data.satTexture = satTexture;
                data.output = visibility;
                data.view = cameraData.GetViewMatrix();
                data.projection = cameraData.GetProjectionMatrix();
                data.groupsX = (cameraDesc.width + 7) / 8;
                data.groupsY = (cameraDesc.height + 7) / 8;

                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.UseTexture(shadowTexture, AccessFlags.Read);
                if (!fixedVisibility)
                    builder.UseTexture(data.satTexture, AccessFlags.Read);
                builder.UseTexture(data.output, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(visibility, Shader.PropertyToID("_ScreenSpaceShadowmapTexture"));
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (ResolvePassData data, ComputeGraphContext context) =>
                {
                    if (!data.fixedVisibility)
                    {
                        TextureUVOrigin depthOrigin = context.GetTextureUVOrigin(data.cameraDepthTexture);
                        bool isFlipped = (depthOrigin == TextureUVOrigin.BottomLeft);
                        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(data.projection, isFlipped);
                        Matrix4x4 inverseVP = (gpuProjection * data.view).inverse;

                        context.cmd.SetComputeMatrixParam(data.shader, "_CameraInverseVP", inverseVP);
                        context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_SATInput", data.satTexture);
                        context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_ShadowDepth", data.shadowDepthTexture);
                    }
                    context.cmd.SetComputeTextureParam(data.shader, data.kernel, "_ShadowVisibility", data.output);
                    context.cmd.DispatchCompute(data.shader, data.kernel, data.groupsX, data.groupsY, 1);

                    context.cmd.SetKeyword(MainLightShadows, false);
                    context.cmd.SetKeyword(MainLightShadowCascades, false);
                    context.cmd.SetKeyword(MainLightShadowScreen, true);
                });
            }
        }
    }

    private class RestoreShadowKeywordsPass : ScriptableRenderPass
    {
        private class PassData
        {
            internal UniversalShadowData shadowData;
        }

        private static void ExecutePass(UniversalShadowData shadowData, RasterCommandBuffer cmd)
        {
            int cascadesCount = shadowData.mainLightShadowCascadesCount;
            bool mainLightShadows = shadowData.supportsMainLightShadows;
            bool receiveShadowsNoCascade = mainLightShadows && cascadesCount == 1;
            bool receiveShadowsCascades = mainLightShadows && cascadesCount > 1;

            cmd.SetKeyword(MainLightShadowScreen, false);
            cmd.SetKeyword(MainLightShadows, receiveShadowsNoCascade);
            cmd.SetKeyword(MainLightShadowCascades, receiveShadowsCascades);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                "Restore Main Light Shadow Keywords", out var passData))
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                passData.shadowData = frameData.Get<UniversalShadowData>();
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    ExecutePass(data.shadowData, context.cmd));
            }
        }
    }
}
