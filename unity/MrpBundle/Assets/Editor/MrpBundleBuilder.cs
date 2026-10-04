using System.IO;
using UnityEditor;
using UnityEngine;

// AssetBundle mrp_fx のビルド入口 (tools/build-bundle.ps1 から Unity バッチモードで呼ばれる)。
// 毎回 Assets/Generated に素材を作り直してから焼く:
//   noise.png    … 継ぎ目なく敷き詰められる雲状のノイズ (128px・繰り返し)
//   terrain.mat  … MRP/TerrainSprite (損傷マスクで穴と焦げを描く部屋の絵用)
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

        string matPath = Folder + "/terrain.mat";
        var mat = new Material(terrain) { name = "terrain" };
        mat.SetTexture("_Noise", noise);
        AssetDatabase.DeleteAsset(matPath);
        AssetDatabase.CreateAsset(mat, matPath);
        Tag(matPath);

        AssetDatabase.SaveAssets();

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, target);
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

    private static void Tag(string path)
    {
        AssetImporter importer = AssetImporter.GetAtPath(path);
        if (importer == null || importer.assetBundleName == BundleName) return;
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();
    }
}
