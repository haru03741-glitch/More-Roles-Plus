// Based on https://github.com/waffle-ful/Aeterna-End-K-not unity/FxBundle/Assets/Editor/FxBundleBuilder.cs (GPL-3.0)
using System.IO;
using UnityEditor;
using UnityEngine;

// AssetBundle mrp_fx のビルド入口 (tools/build-bundle.ps1 から Unity バッチモードで呼ばれる)。
// 毎回 Assets/Generated に素材を作り直してから焼く:
//   noise.png    … 継ぎ目なく敷き詰められる雲状のノイズ (128px・繰り返し)
//   cells.png    … 細胞模様 (ボロノイ・繰り返し)。割れ口を角張らせる
//   terrain.mat  … MRP/TerrainSprite (損傷マスクで穴と焦げを描く部屋の絵用)
//   water.mat    … MRP/Water (床の水たまり・噴き出し。CPU が書いた水の量の絵から見た目を決める)
//   water_wade.mat … MRP/Water の _Mode 2 (水に浸かったクルー・小物の手前に置く水面)
//   firefloor.mat / flame.mat … MRP/Fire (床の照りと油の膜 / 立ち上がる炎。CPU が書いた升ごとの火の値から見た目を決める)
//   noise_*.wav  … 壁を壊した音 (爆発・叩く・崩れる)。tools/make-break-sounds.py が先に書き出しておく
// シェーダはマテリアルから参照されるので一緒に入る。ターゲットごとに描画 API 向けへ変換される
// (Windows = Direct3D11、Android = GLES3 / Vulkan)。
public static class MrpBundleBuilder
{
    private const string Folder = "Assets/Generated";
    private const string BundleName = "mrp_fx";
    private const int NoiseSize = 128;

    public static void Build() => BuildFor(BuildTarget.StandaloneWindows64, "Build");

    public static void BuildAndroid() => BuildFor(BuildTarget.Android, Path.Combine("Build", "android"));

    private static void BuildFor(BuildTarget target, string outDir)
    {
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "Generated");

        Shader terrain = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/TerrainSprite.shader");
        if (terrain == null)
        {
            Debug.LogError("MrpBundleBuilder: TerrainSprite shader not found");
            EditorApplication.Exit(2);
            return;
        }

        Texture2D noise = MakeNoise();
        Texture2D cells = MakeCells();

        string matPath = Folder + "/terrain.mat";
        var mat = new Material(terrain) { name = "terrain" };
        mat.SetTexture("_Noise", noise);
        mat.SetTexture("_Cells", cells);
        mat.SetFloat("_EdgeJag", 0.6f);
        AssetDatabase.DeleteAsset(matPath);
        AssetDatabase.CreateAsset(mat, matPath);
        Tag(matPath);

        Shader waterShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/Water.shader");
        if (waterShader == null)
        {
            Debug.LogError("MrpBundleBuilder: Water shader not found");
            EditorApplication.Exit(2);
            return;
        }
        string waterPath = Folder + "/water.mat";
        var water = new Material(waterShader) { name = "water" };
        water.SetTexture("_Noise", noise);
        AssetDatabase.DeleteAsset(waterPath);
        AssetDatabase.CreateAsset(water, waterPath);
        Tag(waterPath);
        string wadePath = Folder + "/water_wade.mat";
        var wade = new Material(waterShader) { name = "water_wade" };
        wade.SetTexture("_Noise", noise);
        wade.SetFloat("_Mode", 2f);
        AssetDatabase.DeleteAsset(wadePath);
        AssetDatabase.CreateAsset(wade, wadePath);
        Tag(wadePath);

        Shader fireShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/Fire.shader");
        if (fireShader == null)
        {
            Debug.LogError("MrpBundleBuilder: Fire shader not found");
            EditorApplication.Exit(2);
            return;
        }
        foreach (var (file, mode) in new[] { ("firefloor", 0f), ("flame", 1f) })
        {
            string firePath = Folder + "/" + file + ".mat";
            var fire = new Material(fireShader) { name = file };
            fire.SetTexture("_Noise", noise);
            fire.SetFloat("_Mode", mode);
            AssetDatabase.DeleteAsset(firePath);
            AssetDatabase.CreateAsset(fire, firePath);
            Tag(firePath);
        }

        BreakSounds.ImportAll(Folder, BundleName);

        AssetDatabase.SaveAssets();

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, target);
        if (manifest == null)
        {
            Debug.LogError("MrpBundleBuilder: BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        Debug.Log($"MrpBundleBuilder: built [{string.Join(",", manifest.GetAllAssetBundles())}] target={target} assets=[{string.Join(",", AssetDatabase.GetAssetPathsFromAssetBundle(BundleName))}]");
    }

    private static Texture2D MakeNoise()
    {
        string path = Folder + "/noise.png";
        var tex = new Texture2D(NoiseSize, NoiseSize, TextureFormat.RGBA32, false);
        var rnd = new System.Random(20261004);
        int[] cells = { 4, 8, 16, 32 };
        float[] weights = { 0.5f, 0.27f, 0.15f, 0.08f };
        var grids = new float[cells.Length][];
        for (int o = 0; o < cells.Length; o++)
        {
            grids[o] = new float[cells[o] * cells[o]];
            for (int k = 0; k < grids[o].Length; k++) grids[o][k] = (float)rnd.NextDouble();
        }

        for (int y = 0; y < NoiseSize; y++)
        for (int x = 0; x < NoiseSize; x++)
        {
            float v = 0f;
            for (int o = 0; o < cells.Length; o++)
            {
                int n = cells[o];
                float fx = x * n / (float)NoiseSize, fy = y * n / (float)NoiseSize;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = fx - x0, ty = fy - y0;
                tx = tx * tx * (3f - 2f * tx);
                ty = ty * ty * (3f - 2f * ty);
                float[] g = grids[o];
                float a = g[y0 % n * n + x0 % n], b = g[y0 % n * n + (x0 + 1) % n];
                float c = g[(y0 + 1) % n * n + x0 % n], d = g[(y0 + 1) % n * n + (x0 + 1) % n];
                v += weights[o] * Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
            }

            v = Mathf.Clamp01((v - 0.5f) * 1.8f + 0.5f);
            tex.SetPixel(x, y, new Color(v, v, v, 1f));
        }

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Bilinear;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.isReadable = false;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // 細胞模様 (繰り返し可能なボロノイ): 画素 = いちばん近い種点の乱数値。割れ口を細胞の境目に沿った角張った形にする
    private static Texture2D MakeCells()
    {
        const int size = 512, sites = 14;
        string path = Folder + "/cells.png";
        var rnd = new System.Random(7);
        var sx = new float[sites * sites];
        var sy = new float[sites * sites];
        var sv = new float[sites * sites];
        for (int j = 0; j < sites; j++)
        for (int i = 0; i < sites; i++)
        {
            int k = j * sites + i;
            sx[k] = (i + 0.15f + (float)rnd.NextDouble() * 0.7f) / sites;
            sy[k] = (j + 0.15f + (float)rnd.NextDouble() * 0.7f) / sites;
            sv[k] = (float)rnd.NextDouble();
        }
        // 値を順位に置き換える (並びは同じまま、種点ごとに違う 8 bit にする)
        int n = sites * sites;
        var order = new int[n];
        for (int k = 0; k < n; k++) order[k] = k;
        System.Array.Sort(order, (a, b) => sv[a] != sv[b] ? sv[a].CompareTo(sv[b]) : a.CompareTo(b));
        for (int r = 0; r < n; r++) sv[order[r]] = (r + 0.5f) / n;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
            int ci = (int)(u * sites), cj = (int)(v * sites);
            float best = float.MaxValue, val = 0;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ii = (ci + di + sites) % sites, jj = (cj + dj + sites) % sites;
                int k = jj * sites + ii;
                float px = sx[k] + (ci + di - ii) / (float)sites;
                float py = sy[k] + (cj + dj - jj) / (float)sites;
                float d = (u - px) * (u - px) + (v - py) * (v - py);
                if (d < best) { best = d; val = sv[k]; }
            }
            tex.SetPixel(x, y, new Color(val, val, val, 1f));
        }
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Point; // 細胞の境目をくっきり
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.isReadable = false;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static void Tag(string path)
    {
        AssetImporter importer = AssetImporter.GetAtPath(path);
        if (importer == null || importer.assetBundleName == BundleName) return;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();
    }
}
