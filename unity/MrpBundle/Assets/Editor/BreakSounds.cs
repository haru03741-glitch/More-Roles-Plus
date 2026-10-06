using System.IO;
using UnityEditor;
using UnityEngine;

// 壁を壊した音 (noise_*.wav) を AssetBundle に入れる。wav は tools/make-break-sounds.py が
// tools/build-bundle.ps1 の中で Assets/Generated に書き出しておく
public static class BreakSounds
{
    public static void ImportAll(string folder, string bundle)
    {
        string[] files = Directory.GetFiles(folder, "noise_*.wav");
        if (files.Length == 0) Debug.LogError("MrpBundleBuilder: no noise_*.wav (run tools/make-break-sounds.py)");
        foreach (string file in files)
        {
            string path = folder + "/" + Path.GetFileName(file);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.forceToMono = true;
            importer.loadInBackground = false;
            var st = importer.defaultSampleSettings;
            st.loadType = AudioClipLoadType.DecompressOnLoad;
            st.preloadAudioData = true;
            st.compressionFormat = AudioCompressionFormat.Vorbis;
            st.quality = 0.7f;
            st.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            importer.defaultSampleSettings = st;
            importer.assetBundleName = bundle;
            importer.SaveAndReimport();
        }
    }
}
