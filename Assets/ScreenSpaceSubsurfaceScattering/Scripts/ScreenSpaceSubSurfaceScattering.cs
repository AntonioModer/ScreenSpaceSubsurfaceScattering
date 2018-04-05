﻿//MIT License
//
//Copyright(c) 2018 Charles Thomas
//
//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files(the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions :
//
//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.
//
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.
//

using UnityEngine;

[ExecuteInEditMode]
[ImageEffectAllowedInSceneView]
[AddComponentMenu("Xerxes1138/ScreenSpaceSubsurfaceScattering")]
public class ScreenSpaceSubSurfaceScattering : MonoBehaviour
{
    static class Uniforms
    {
        internal static readonly int _MainTex = Shader.PropertyToID("_MainTex"); // Scene color texture
        internal static readonly int _SSSColorTex = Shader.PropertyToID("_SSSColorTex"); // SSS color texture
        internal static readonly int _DiffuseSpecularTex = Shader.PropertyToID("_DiffuseSpecularTex"); // Diffuse and specular lighting texture
        internal static readonly int _SpecularTex = Shader.PropertyToID("_SpecularTex"); // Extracted specular texture
        internal static readonly int _DiffuseTex = Shader.PropertyToID("_DiffuseTex"); // Extracted diffuse texture
        internal static readonly int _DiffuseSSSTex = Shader.PropertyToID("_DiffuseSSSTex"); // SSS diffuse texture
        internal static readonly int _BlueNoiseTex = Shader.PropertyToID("_BlueNoiseTex"); // Jitter texture

        internal static readonly int _SSSParams = Shader.PropertyToID("_SSSParams"); // x = jitter radius, y = distanceToProjectionWindow, z = aspect ratio correction, w = world unit
        internal static readonly int _DiffuseSizeAndDiffuseTexelSize = Shader.PropertyToID("_DiffuseSizeAndDiffuseTexelSize"); // x = diffuse width, y = diffuse height, z = 1.0f / diffuse width, w = 1.0f/ diffuse height
        internal static readonly int _ScreenSizeAndScreenTexelSize = Shader.PropertyToID("_ScreenSizeAndScreenTexelSize"); // x = screen width, y = screen height, z = 1.0f / screen width, w = 1.0f/ screen height
        internal static readonly int _JitterSizeAndOffset = Shader.PropertyToID("_JitterSizeAndOffset"); // x = diffuse width / jitter width, y = diffuse height / jitter height, z = halton sample x, w = halton sample y
        internal static readonly int _SSS_NUM_SAMPLES = Shader.PropertyToID("_SSS_NUM_SAMPLES"); // Number of samples

        internal static readonly int _FadeDistanceAndRadius = Shader.PropertyToID("_FadeDistanceAndRadius"); // x = fade distance, y = fade radius
        internal static readonly int _TemporalAAParams = Shader.PropertyToID("_TemporalAAParams"); // x = sample index, y = sample count, z = jitter offset x, w = jitter offset y
        internal static readonly int _DebugPass = Shader.PropertyToID("_DebugPass");

        internal static readonly int _ProjectionMatrix = Shader.PropertyToID("_ProjectionMatrix");
        internal static readonly int _ViewProjectionMatrix = Shader.PropertyToID("_ViewProjectionMatrix");
        internal static readonly int _InverseProjectionMatrix = Shader.PropertyToID("_InverseProjectionMatrix");
        internal static readonly int _InverseViewProjectionMatrix = Shader.PropertyToID("_InverseViewProjectionMatrix");
        internal static readonly int _WorldToCameraMatrix = Shader.PropertyToID("_WorldToCameraMatrix");
        internal static readonly int _CameraToWorldMatrix = Shader.PropertyToID("_CameraToWorldMatrix");
    }

    public SSSSProfile sreenSpaceSubSurfaceScatteringProfile = null; // uSSS settings stored in a Scriptable Object

    private int dilationPass = 3; // Number of dilation pass

    // Unity's TAA
    private int m_SampleIndex = 0;
    private const int k_SampleCount = 8;

    private int screenWidth;
    private int screenHeight;

    private int diffuseWidth;
    private int diffuseHeight;

    private Texture noise;
    private Vector2 jitterSample;

    private Camera m_camera;

    private Matrix4x4 projectionMatrix;
    private Matrix4x4 viewProjectionMatrix;
    private Matrix4x4 inverseViewProjectionMatrix;
    private Matrix4x4 worldToCameraMatrix;
    private Matrix4x4 cameraToWorldMatrix;

    private RenderBuffer[] DiffuseSpecularBuffer = new RenderBuffer[2];

    static Material m_rendererMaterial = null;
    protected Material rendererMaterial
    {
        get
        {
            if (m_rendererMaterial == null)
            {
                m_rendererMaterial = new Material(Shader.Find("Hidden/Xerxes1138/ScreenSpaceSubsurfaceScattering"));
                m_rendererMaterial.hideFlags = HideFlags.DontSave;
            }
            return m_rendererMaterial;
        }
    }

    // Unity's TAA
    private float GetHaltonValue(int index, int radix)
    {
        float result = 0f;
        float fraction = 1f / (float)radix;

        while (index > 0)
        {
            result += (float)(index % radix) * fraction;

            index /= radix;
            fraction /= (float)radix;
        }

        return result;
    }

    // Unity's TAA
    private Vector2 GenerateRandomOffset()
    {
        var offset = new Vector2(
                GetHaltonValue(m_SampleIndex & 1023, 2),
                GetHaltonValue(m_SampleIndex & 1023, 3));

        if (++m_SampleIndex >= k_SampleCount)
            m_SampleIndex = 0;

        return offset;
    }

    private void SetCamera()
    {
        m_camera = GetComponent<Camera>();

        m_camera.depthTextureMode |= DepthTextureMode.Depth;
    }

    private void SetMatrix()
    {
        worldToCameraMatrix = m_camera.worldToCameraMatrix;
        cameraToWorldMatrix = worldToCameraMatrix.inverse;

        projectionMatrix = GL.GetGPUProjectionMatrix(m_camera.projectionMatrix, false);

        viewProjectionMatrix = projectionMatrix * worldToCameraMatrix;
        inverseViewProjectionMatrix = viewProjectionMatrix.inverse;

        rendererMaterial.SetMatrix(Uniforms._ProjectionMatrix, projectionMatrix);
        rendererMaterial.SetMatrix(Uniforms._ViewProjectionMatrix, viewProjectionMatrix);
        rendererMaterial.SetMatrix(Uniforms._InverseProjectionMatrix, projectionMatrix.inverse);
        rendererMaterial.SetMatrix(Uniforms._InverseViewProjectionMatrix, inverseViewProjectionMatrix);
        rendererMaterial.SetMatrix(Uniforms._WorldToCameraMatrix, worldToCameraMatrix);
        rendererMaterial.SetMatrix(Uniforms._CameraToWorldMatrix, cameraToWorldMatrix);
    }

    private void SetJitter()
    {
        noise = Resources.Load("tex_BlueNoise_256x256_UNI") as Texture2D;
    }

    private void SetScreenSize()
    {
        screenWidth = m_camera.pixelWidth;
        screenHeight = m_camera.pixelHeight;

        diffuseWidth = screenWidth / (int)sreenSpaceSubSurfaceScatteringProfile.samplingResolution;
        diffuseHeight = screenHeight / (int)sreenSpaceSubSurfaceScatteringProfile.samplingResolution;
    }

    private void SetDebugViews()
    {
        switch (sreenSpaceSubSurfaceScatteringProfile.debugPass)
        {
            case DebugPass.Combine:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 0);
                break;
            case DebugPass.DiffuseLighting:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 1);
                break;
            case DebugPass.SpecularLighting:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 2);
                break;
            case DebugPass.Albedo:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 3);
                break;
            case DebugPass.Specular:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 4);
                break;
            case DebugPass.SSSColor:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 5);
                break;
            case DebugPass.ShadingModel:
                rendererMaterial.SetFloat(Uniforms._DebugPass, 6);
                break;
        }
    }

    private void SetParameters(Material material)
    {
        rendererMaterial.shaderKeywords = null;

        rendererMaterial.SetVector(Uniforms._FadeDistanceAndRadius, new Vector4(sreenSpaceSubSurfaceScatteringProfile.fadeDistance, sreenSpaceSubSurfaceScatteringProfile.fadeRadius, 0.0f, 0.0f));

        float distanceToProjectionWindow = m_camera.nonJitteredProjectionMatrix.m11;

        float range = 2.0f;

        Vector4 sssParams = new Vector4(sreenSpaceSubSurfaceScatteringProfile.jitterRadius, distanceToProjectionWindow / range * 0.5f, (float)diffuseWidth * 1.0f / (float)diffuseHeight, sreenSpaceSubSurfaceScatteringProfile.worldUnit);

        rendererMaterial.SetVector(Uniforms._SSSParams, sssParams);

        rendererMaterial.SetVector(Uniforms._ScreenSizeAndScreenTexelSize, new Vector4((float)screenWidth, (float)screenHeight, 1.0f / (float)screenWidth, 1.0f / (float)screenHeight));
        rendererMaterial.SetVector(Uniforms._DiffuseSizeAndDiffuseTexelSize, new Vector4((float)diffuseWidth, (float)diffuseHeight, 1.0f / (float)diffuseWidth, 1.0f / (float)diffuseHeight));

        rendererMaterial.SetVector(Uniforms._JitterSizeAndOffset, new Vector4((float)diffuseWidth / (float)noise.width, (float)diffuseHeight / (float)noise.height, jitterSample.x, jitterSample.y));

        rendererMaterial.SetVector(Uniforms._TemporalAAParams,new Vector4(m_SampleIndex, k_SampleCount, jitterSample.x, jitterSample.y));

        rendererMaterial.SetInt(Uniforms._SSS_NUM_SAMPLES, (int)sreenSpaceSubSurfaceScatteringProfile.sampleQuality);
    }

    // Helpers
    private RenderTexture CreateTempBuffer(int width, int height, int depth, RenderTextureFormat format)
    {
        return RenderTexture.GetTemporary(width, height, depth, format);
    }

    private void ReleaseTempBuffer(RenderTexture renderTexture)
    {
        RenderTexture.ReleaseTemporary(renderTexture);
    }

    private void DrawFullScreenQuad()
    {
        GL.PushMatrix();
        GL.LoadOrtho();

        GL.Begin(GL.QUADS);
        GL.MultiTexCoord2(0, 0.0f, 0.0f);
        GL.Vertex3(0.0f, 0.0f, 0.0f); // BL

        GL.MultiTexCoord2(0, 1.0f, 0.0f);
        GL.Vertex3(1.0f, 0.0f, 0.0f); // BR

        GL.MultiTexCoord2(0, 1.0f, 1.0f);
        GL.Vertex3(1.0f, 1.0f, 0.0f); // TR

        GL.MultiTexCoord2(0, 0.0f, 1.0f);
        GL.Vertex3(0.0f, 1.0f, 0.0f); // TL

        GL.End();
        GL.PopMatrix();
    }

    public static RenderTexture CreateRenderTexture(int w, int h, int d, RenderTextureFormat f, bool useMipMap, bool generateMipMap, FilterMode filterMode)
    {
        RenderTexture r = new RenderTexture(w, h, d, f);
        r.enableRandomWrite = true;
        r.filterMode = filterMode;
        r.useMipMap = useMipMap;
        r.autoGenerateMips = generateMipMap;
        r.Create();
        return r;
    }

    // Rendering
    private void OnPreCull()
    {
        if (sreenSpaceSubSurfaceScatteringProfile == null || m_camera == null)
            return;

        if (sreenSpaceSubSurfaceScatteringProfile.temporalJitter)   
            jitterSample = GenerateRandomOffset();
    }

    private void OnEnable()
    {
        SetJitter();
        SetCamera();

        if (DiffuseSpecularBuffer == null)
            DiffuseSpecularBuffer = new RenderBuffer[2];
    }

    private void OnDisable()
    {
        if (DiffuseSpecularBuffer != null)
            DiffuseSpecularBuffer = null;
    }

    [ImageEffectOpaque]
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (sreenSpaceSubSurfaceScatteringProfile == null || m_camera == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        SetMatrix();

        SetScreenSize();

        SetParameters(rendererMaterial);

        SetDebugViews();

        rendererMaterial.SetTexture(Uniforms._BlueNoiseTex, noise); // Does it need to be in RenderImage ?
        rendererMaterial.SetTexture(Uniforms._MainTex, source);

        // SSS color from full resoltution g-buffer and encoded in YCbCr with a cherckerboard pattern.
        // In some cases the sss diffuse lighting is performed in half resolution,
        // In that case the checkerboard pattern makes it impossible to decode it back to RGB because of the downsampling of the sss pass
        // Until I found something better, I'm using a render texture
        RenderTexture SSSColorTex = CreateTempBuffer(screenWidth, screenHeight, 0, RenderTextureFormat.ARGB32);
        SSSColorTex.filterMode = FilterMode.Point;
        Graphics.SetRenderTarget(SSSColorTex);
        rendererMaterial.SetPass(7); // YCbCr to RGB SSS color
        DrawFullScreenQuad();

        rendererMaterial.SetTexture(Uniforms._SSSColorTex, SSSColorTex); // Send the sss color texture to the shader

        // Diffuse and specular lighting texture in a checkerboard pattern, see : http://xerxes1138.blogspot.fr/2016/04/skin-shading.html
        RenderTexture DiffuseSpecularTex = CreateTempBuffer(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBHalf);
        DiffuseSpecularTex.filterMode = FilterMode.Point; // Make sure we don't get bother by bilinear filtering

        rendererMaterial.SetTexture(Uniforms._DiffuseSpecularTex, DiffuseSpecularTex);

        Graphics.Blit(source, DiffuseSpecularTex, rendererMaterial, 6); // pre pass to avoid sky bleeding on the skin objects

        RenderTexture DiffuseTex = CreateTempBuffer(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBHalf);
        DiffuseTex.filterMode = FilterMode.Point;

        RenderTexture SpecularTex = CreateTempBuffer(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBHalf);
        SpecularTex.filterMode = FilterMode.Point;

        DiffuseSpecularBuffer[0] = DiffuseTex.colorBuffer;
        DiffuseSpecularBuffer[1] = SpecularTex.colorBuffer;
        Graphics.SetRenderTarget(DiffuseSpecularBuffer, DiffuseTex.depthBuffer);
        rendererMaterial.SetPass(1);
        DrawFullScreenQuad();

        ReleaseTempBuffer(DiffuseSpecularTex);

        rendererMaterial.SetTexture(Uniforms._DiffuseTex, DiffuseTex);
        rendererMaterial.SetTexture(Uniforms._SpecularTex, SpecularTex);

        // SSS pass
        RenderTexture SSSPassTex1 = CreateTempBuffer(diffuseWidth, diffuseHeight, 0, RenderTextureFormat.ARGBHalf);
        SSSPassTex1.filterMode = FilterMode.Point;

        RenderTexture SSSPassTex0 = CreateTempBuffer(diffuseWidth, diffuseHeight, 0, RenderTextureFormat.ARGBHalf);
        SSSPassTex0.filterMode = FilterMode.Point;

        Graphics.Blit(DiffuseTex, SSSPassTex0, rendererMaterial, 3);
        Graphics.Blit(SSSPassTex0, SSSPassTex1, rendererMaterial, 4);

        ReleaseTempBuffer(SSSPassTex0);

        // Perform an edge dilation when in half resolution to avoid black pixels near the edges in the sss diffuse texture
        if (sreenSpaceSubSurfaceScatteringProfile.samplingResolution != SamplingResolution.FullRes)
        {
            RenderTexture SSSPassTexDilation = CreateTempBuffer(screenWidth, screenHeight, 0, RenderTextureFormat.ARGBHalf);
            SSSPassTexDilation.filterMode = FilterMode.Point;

            for (int i = 0; i < dilationPass; i++)
            {
                Graphics.Blit(SSSPassTex1, SSSPassTexDilation, rendererMaterial, 5); // Dilation pass
                Graphics.Blit(SSSPassTexDilation, SSSPassTex1, rendererMaterial, 5); // Dilation pass
            }

            rendererMaterial.SetTexture(Uniforms._DiffuseSSSTex, SSSPassTexDilation);

            ReleaseTempBuffer(SSSPassTex1);

            switch (sreenSpaceSubSurfaceScatteringProfile.debugPass)
            {
                case DebugPass.Combine:
                    Graphics.Blit(source, destination, rendererMaterial, 0);
                    break;
                case DebugPass.ShadingModel:
                case DebugPass.SSSColor:
                case DebugPass.Specular:
                case DebugPass.Albedo:
                case DebugPass.SpecularLighting:
                case DebugPass.DiffuseLighting:
                    Graphics.Blit(source, destination, rendererMaterial, 2);
                    break;
            }

            ReleaseTempBuffer(SSSPassTexDilation);
        }
        else
        {
            rendererMaterial.SetTexture(Uniforms._DiffuseSSSTex, SSSPassTex1);

            switch (sreenSpaceSubSurfaceScatteringProfile.debugPass)
            {
                case DebugPass.Combine:
                    Graphics.Blit(source, destination, rendererMaterial, 0);
                    break;
                case DebugPass.ShadingModel:
                case DebugPass.SSSColor:
                case DebugPass.Specular:
                case DebugPass.Albedo:
                case DebugPass.SpecularLighting:
                case DebugPass.DiffuseLighting:
                    Graphics.Blit(source, destination, rendererMaterial, 2);
                    break;
            }

            ReleaseTempBuffer(SSSPassTex1);
        }

        ReleaseTempBuffer(DiffuseTex);
        ReleaseTempBuffer(SpecularTex);
        ReleaseTempBuffer(SSSColorTex);
    }
}
