using System;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// Только для временного проекта проверки. Запускает compute-шейдер DepthSample на реальном GPU редактора:
//  1) синтетическая текстура глубины (2 слоя) по формулам Meta с известной позой камеры -> kernel Sample ->
//     точки сравниваются с геометрической истиной (без использования обратной матрицы);
//  2) обратный порядок глубины (_ZMode = 1) на текстуре с raw' = 1 - raw даёт те же точки;
//  3) фильтр границ: одиночные выбросы глубины отбрасываются, гладкая поверхность остаётся;
//  4) гистограмма сырых значений (_Diag) считает все пиксели;
//  5) kernel BuildQuads пишет квады в вершинный буфер меша, созданного как в DepthPointCloud -> проверка вершин.
// Ошибки HLSL сделают результат неверным (и попадут в лог как "Shader error"), ошибки C# ломают -executeMethod сами.
public static class CompileCheck
{
    const int W = 64;             // размер синтетической текстуры глубины (квадрат)
    const float Near = 0.1f;
    const float Tan = 1.0f;       // tan(45°) со всех четырёх сторон
    static readonly Vector3 CamPos = new Vector3(0.3f, 1.5f, -0.2f);
    static readonly Quaternion CamRot = Quaternion.Euler(-10f, 20f, 5f);

    public static void Run()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        const string path = "Assets/Resources/DepthSample.compute";
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        if (cs == null)
        {
            Debug.LogError("COMPILECHECK: compute shader not found at " + path);
            EditorApplication.Exit(2);
            return;
        }

        int errors = 0;
        try
        {
            errors += TestSampleAndQuads(cs);
        }
        catch (Exception e)
        {
            Debug.LogError("COMPILECHECK: exception " + e);
            errors++;
        }

        var msgs = ShaderUtil.GetComputeShaderMessages(cs);
        foreach (var m in msgs)
        {
            Debug.Log($"COMPILECHECK: {m.severity} {m.platform} {m.file}:{m.line} {m.message} {m.messageDetails}");
            if (m.severity == ShaderCompilerMessageSeverity.Error) errors++;
        }
        Debug.Log($"COMPILECHECK: device={SystemInfo.graphicsDeviceType} compute={SystemInfo.supportsComputeShaders} messages={msgs.Length} errors={errors} {(errors == 0 ? "OK" : "FAILED")} DONE");
        EditorApplication.Exit(errors > 0 ? 3 : 0);
    }

    // Meta EnvironmentDepthUtils: проекция (far = бесконечность) и параметры линеаризации
    static Matrix4x4 Proj()
    {
        float x = 2f / (Tan + Tan), y = 2f / (Tan + Tan);
        return new Matrix4x4 { m00 = x, m11 = y, m22 = -1f, m23 = -2f * Near, m32 = -1f };
    }
    static Matrix4x4 View() => Matrix4x4.TRS(CamPos, CamRot, new Vector3(1, 1, -1)).inverse;
    static Vector4 ZB => new Vector4(-2f * Near, -1f, 0f, 0f);

    static float RawFromLinear(float L) { float ndc = ZB.x / L - ZB.y; return ndc * 0.5f + 0.5f; }
    // гладкое поле глубин 0.9 .. 2.1 м, соседние пиксели отличаются не больше чем на ~1.5 см
    static float LinearAt(int px, int py) => 1.5f + 0.3f * Mathf.Sin(px * 0.05f) * Mathf.Cos(py * 0.04f) + 0.004f * px;
    static bool IsOutlier(int px, int py) => (px * 7 + py * 13) % 50 == 0 && px > 1 && py > 1 && px < W - 2 && py < W - 2;

    // Геометрическая истина: точка на луче пикселя на линейной глубине L (камера смотрит вдоль +forward своего поворота)
    static Vector3 Truth(int px, int py, float L)
    {
        float nx = (px + 0.5f) / W * 2f - 1f, ny = (py + 0.5f) / W * 2f - 1f;
        return CamPos + CamRot * new Vector3(nx * Tan * L, ny * Tan * L, L);
    }

    static Texture2DArray MakeDepthTexture(bool reversed, out int outliers)
    {
        int n = W * W;
        var tex = new Texture2DArray(W, W, 2, TextureFormat.RFloat, false, true);
        var s0 = new float[n]; var s1 = new float[n];
        outliers = 0;
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                s0[y * W + x] = reversed ? 0f : 1f;   // "нет глубины"
                float L = LinearAt(x, y);
                if (IsOutlier(x, y)) { L += 1f; outliers++; }
                float raw = RawFromLinear(L);
                s1[y * W + x] = reversed ? 1f - raw : raw;
            }
        tex.SetPixelData(s0, 0, 0);
        tex.SetPixelData(s1, 0, 1);
        tex.Apply(false, false);
        return tex;
    }

    // точность точек текущего состояния буфера относительно истины; возвращает число провалов
    static int CheckAccuracy(Vector4[] res, string label, out int valid)
    {
        int fails = 0; valid = 0; float maxErr = 0f, maxDistErr = 0f;
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                var r = res[y * W + x];
                if (r.w <= 0f) { fails++; if (fails <= 5) Debug.LogError($"COMPILECHECK: {label}: point {x},{y} invalid (w=0)"); continue; }
                valid++;
                float L = LinearAt(x, y) + (IsOutlier(x, y) ? 1f : 0f);
                var t = Truth(x, y, L);
                float e = Vector3.Distance(new Vector3(r.x, r.y, r.z), t);
                float de = Mathf.Abs(r.w - Vector3.Distance(t, CamPos));
                maxErr = Mathf.Max(maxErr, e);
                maxDistErr = Mathf.Max(maxDistErr, de);
                if (e > 2e-3f || de > 2e-3f)
                {
                    fails++;
                    if (fails <= 5) Debug.LogError($"COMPILECHECK: {label}: point {x},{y} got {r:F4} expected {t:F4} (L={L:F3}) err {e:E2} distErr {de:E2}");
                }
            }
        Debug.Log($"COMPILECHECK: {label}: valid={valid}/{W * W} maxErr={maxErr:E2} m maxDistErr={maxDistErr:E2} m");
        return fails;
    }

    static int TestSampleAndQuads(ComputeShader cs)
    {
        int fails = 0;
        int n = W * W;

        var tex = MakeDepthTexture(false, out int outliers);
        var vp = Proj() * View();
        var points = new ComputeBuffer(n, 16);
        points.SetData(new Vector4[n]);
        var hist = new ComputeBuffer(32, sizeof(uint));
        hist.SetData(new uint[32]);

        int k = cs.FindKernel("Sample");
        cs.SetTexture(k, "_Depth", tex);
        cs.SetBuffer(k, "_Points", points);
        cs.SetBuffer(k, "_RawHist", hist);
        cs.SetMatrix("_InvVP", vp.inverse);
        cs.SetVector("_HeadPos", CamPos);
        cs.SetVector("_ZB", ZB);
        cs.SetInt("_GridW", W); cs.SetInt("_GridH", W);
        cs.SetInt("_DepthW", W); cs.SetInt("_DepthH", W);
        cs.SetInt("_Slice", 1);
        cs.SetInt("_FlipY", 0);
        cs.SetInt("_ZMode", 0);
        cs.SetInt("_Diag", 1);
        cs.SetInt("_BaseIndex", 0);
        cs.SetVector("_Jitter", new Vector2(0.5f, 0.5f));   // центр ячейки: сетка = текстура, пиксель = id
        cs.SetFloat("_MinDist", 0.2f);
        cs.SetFloat("_MaxDist", 6f);
        cs.SetInt("_EdgeFilter", 0);
        cs.SetFloat("_EdgeRelTol", 0.04f);
        cs.SetFloat("_EdgeMinTol", 0.04f);
        cs.Dispatch(k, W / 8, W / 8, 1);
        cs.SetInt("_Diag", 0);

        // 1) обычный порядок, без фильтра: все точки (включая выбросы) восстанавливаются точно
        var res = new Vector4[n];
        points.GetData(res);
        fails += CheckAccuracy(res, "Sample z0 (no filter)", out _);

        // 4) гистограмма сырых значений посчитала все пиксели, и они в верхних корзинах (обычный порядок)
        var h = new uint[32];
        hist.GetData(h);
        uint total = 0, upper = 0;
        for (int i = 0; i < 32; i++) total += h[i];
        for (int i = 25; i < 30; i++) upper += h[i];
        Debug.Log($"COMPILECHECK: raw histogram total={total} (expected {n}), raw>=1: {h[31]}, raw<=0: {h[30]}, bins 25..29: {upper}");
        if (total != (uint)n) { fails++; Debug.LogError("COMPILECHECK: histogram total mismatch"); }
        if (upper < (uint)(n * 0.9f)) { fails++; Debug.LogError("COMPILECHECK: histogram: expected most raw values in upper bins"); }

        // 3) фильтр границ: выбросы отбрасываются, гладкая поверхность остаётся
        cs.SetInt("_EdgeFilter", 1);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);
        int outliersKept = 0, smoothLost = 0, smoothTotal = 0;
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                bool has = res[y * W + x].w > 0f;
                if (IsOutlier(x, y)) { if (has) outliersKept++; }
                else { smoothTotal++; if (!has) smoothLost++; }
            }
        Debug.Log($"COMPILECHECK: edge filter: outliers {outliers}, kept {outliersKept}; smooth lost {smoothLost}/{smoothTotal}");
        if (outliersKept > 0) { fails++; Debug.LogError("COMPILECHECK: edge filter kept outliers"); }
        if (smoothLost > smoothTotal / 50) { fails++; Debug.LogError("COMPILECHECK: edge filter dropped too many smooth points"); }
        cs.SetInt("_EdgeFilter", 0);

        // слой 0 (нет глубины) не должен давать точек
        cs.SetInt("_Slice", 0);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);
        int bad = 0; foreach (var r in res) if (r.w > 0f) bad++;
        if (bad > 0) { fails++; Debug.LogError($"COMPILECHECK: slice 0 (raw=1) produced {bad} points, expected 0"); }
        else Debug.Log("COMPILECHECK: Sample slice0 (no depth) -> 0 points OK");

        // переворот Y: точки должны отличаться от истины (проверка, что флаг вообще действует)
        cs.SetInt("_Slice", 1); cs.SetInt("_FlipY", 1);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);
        int moved = 0;
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++) { var r = res[y * W + x]; if (r.w > 0f && Vector3.Distance(new Vector3(r.x, r.y, r.z), Truth(x, y, LinearAt(x, y))) > 1e-2f) moved++; }
        Debug.Log($"COMPILECHECK: flipY moves {moved}/{n} points (expected most)");
        if (moved < n / 2) { fails++; Debug.LogError("COMPILECHECK: flipY has no effect"); }
        cs.SetInt("_FlipY", 0);

        // 2) обратный порядок глубины: текстура с raw' = 1 - raw и _ZMode = 1 дают те же точки
        var texRev = MakeDepthTexture(true, out _);
        cs.SetTexture(k, "_Depth", texRev);
        cs.SetInt("_ZMode", 1);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);
        fails += CheckAccuracy(res, "Sample z1 (reversed texture)", out _);
        // а с обычным режимом на обратной текстуре точки должны быть неверными (режимы различимы)
        cs.SetInt("_ZMode", 0);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);
        int wrong = 0, validWrong = 0;
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++) { var r = res[y * W + x]; if (r.w > 0f) { validWrong++; if (Vector3.Distance(new Vector3(r.x, r.y, r.z), Truth(x, y, LinearAt(x, y) + (IsOutlier(x, y) ? 1f : 0f))) > 0.05f) wrong++; } }
        Debug.Log($"COMPILECHECK: z0 on reversed texture: valid={validWrong}, wrong(>5cm)={wrong} (expected: most invalid or wrong)");
        if (validWrong - wrong > n / 10) { fails++; Debug.LogError("COMPILECHECK: z modes are not distinguishable"); }
        UnityEngine.Object.DestroyImmediate(texRev);

        // обратно нормальные точки для квадов
        cs.SetTexture(k, "_Depth", tex);
        cs.SetInt("_ZMode", 0);
        cs.Dispatch(k, W / 8, W / 8, 1);
        points.GetData(res);

        // 5) BuildQuads в вершинный буфер меша, как в DepthPointCloud
        var mesh = new Mesh { name = "test", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertexBufferParams(n * 4,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2));
        var idx = new int[n * 6];
        for (int i = 0, v = 0, j = 0; i < n; i++, v += 4) { idx[j++] = v; idx[j++] = v + 1; idx[j++] = v + 2; idx[j++] = v; idx[j++] = v + 2; idx[j++] = v + 3; }
        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
        mesh.SetIndexBufferParams(idx.Length, IndexFormat.UInt32);
        mesh.SetIndexBufferData(idx, 0, 0, idx.Length, flags);
        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, idx.Length, MeshTopology.Triangles), flags);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2000f);
        mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
        mesh.UploadMeshData(false);
        int stride = mesh.GetVertexBufferStride(0);
        Debug.Log($"COMPILECHECK: mesh stride={stride} posOff={mesh.GetVertexAttributeOffset(VertexAttribute.Position)} colOff={mesh.GetVertexAttributeOffset(VertexAttribute.Color)} uvOff={mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0)}");
        if (stride != 20) { fails++; Debug.LogError("COMPILECHECK: vertex stride != 20"); }

        var vb = mesh.GetVertexBuffer(0);
        int kb = cs.FindKernel("BuildQuads");
        Vector3 right = Vector3.right, up = Vector3.up;
        const float size = 0.03f;
        cs.SetBuffer(kb, "_Points", points);
        cs.SetBuffer(kb, "_Verts", vb);
        cs.SetInt("_TotalPoints", n); cs.SetInt("_SlotSize", n); cs.SetInt("_History", 1); cs.SetInt("_WriteSlot", 0);
        cs.SetVector("_CamRight", right); cs.SetVector("_CamUp", up);
        cs.SetFloat("_PointSize", size); cs.SetFloat("_FloorY", 0f); cs.SetFloat("_ColorHeightRange", 2.5f); cs.SetFloat("_MinAlpha", 0.25f);
        cs.Dispatch(kb, Mathf.CeilToInt(n / 64f), 1, 1);

        var req = AsyncGPUReadback.Request(vb);
        req.WaitForCompletion();
        if (req.hasError)
        {
            Debug.LogWarning("COMPILECHECK: vertex buffer readback not available here (hasError), BuildQuads output not verified");
        }
        else
        {
            var bytes = req.GetData<byte>().ToArray();
            int vfails = 0, checkedQuads = 0;
            for (int i = 0; i < n; i++)
            {
                var c = res[i];
                if (c.w <= 0f) continue;
                checkedQuads++;
                var center = new Vector3(c.x, c.y, c.z);
                for (int q = 0; q < 4; q++)
                {
                    int off = (i * 4 + q) * 20;
                    var pos = new Vector3(BitConverter.ToSingle(bytes, off), BitConverter.ToSingle(bytes, off + 4), BitConverter.ToSingle(bytes, off + 8));
                    Vector3 exp = center + (q == 0 ? -right - up : q == 1 ? -right + up : q == 2 ? right + up : right - up) * (size * 0.5f);
                    byte r8 = bytes[off + 12], g8 = bytes[off + 13], b8 = bytes[off + 14], a8 = bytes[off + 15];
                    bool ok = Vector3.Distance(pos, exp) < 1e-4f && a8 == 255 && (r8 + g8 + b8) > 0;
                    if (!ok)
                    {
                        vfails++;
                        if (vfails <= 5) Debug.LogError($"COMPILECHECK: quad {i} vertex {q}: pos {pos:F4} expected {exp:F4}, rgba {r8},{g8},{b8},{a8}");
                    }
                }
            }
            fails += vfails;
            Debug.Log($"COMPILECHECK: BuildQuads checked {checkedQuads} quads, vertex failures {vfails} {(vfails == 0 ? "OK" : "FAILED")}");
        }

        vb.Dispose();
        points.Release();
        hist.Release();
        UnityEngine.Object.DestroyImmediate(mesh);
        UnityEngine.Object.DestroyImmediate(tex);
        return fails;
    }
}
