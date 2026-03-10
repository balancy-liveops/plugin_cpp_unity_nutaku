#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class NutakuAsmdefGuidFixer
{
    private static readonly string NutakuSdkFolder = "Assets/Plugins/Nutaku";
    private const string DesiredGuid = "54c891bb28f34cabb7760c0a83b1b147";
    private static readonly string DefaultAssemblyName = "Plugins.Nutaku";

    private const string SessionKeyRan = "NutakuAsmdefGuidFixer_RanThisSession";
    private const string SessionKeyPendingLibraryReset = "NutakuAsmdefGuidFixer_PendingLibraryReset";
    
    static NutakuAsmdefGuidFixer()
    {
        EditorApplication.update += InitializeOnLoadCheck;
    }

    private static void InitializeOnLoadCheck()
    {
        EditorApplication.update -= InitializeOnLoadCheck;

        if (SessionState.GetBool(SessionKeyPendingLibraryReset, false))
        {
            SessionState.SetBool(SessionKeyPendingLibraryReset, false);
            Debug.Log("[NutakuAsmdefGuidFixer] Resuming after Library folder clear. Re-running asmdef fix.");
            EditorApplication.delayCall += () => { EditorApplication.delayCall += RunOnceAfterLoad; };
            return;
        }

        if (!SessionState.GetBool(SessionKeyRan, false))
        {
            EditorApplication.update += RunOnceAfterLoad;
        }
    }

    private static void RunOnceAfterLoad()
    {
        EditorApplication.update -= RunOnceAfterLoad;
        SessionState.SetBool(SessionKeyRan, true);
        if (EditorApplication.isCompiling)
        {
            EditorApplication.delayCall += RunOnceAfterLoad;
            return;
        }

        try
        {
            EnsureSdkAsmdefWithGuid();
        }
        catch (Exception ex)
        {
            Debug.LogError("[NutakuAsmdefGuidFixer] Error on auto-run: " + ex);
        }
    }

    [MenuItem("Tools/Nutaku/Fix Asmdef GUID")]
    public static void RunFromMenu()
    {
        try
        {
            EnsureSdkAsmdefWithGuid(true);
            EditorUtility.DisplayDialog("Nutaku SDK", "Asmdef GUID check/repair complete.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("[NutakuAsmdefGuidFixer] " + ex);
            EditorUtility.DisplayDialog("Nutaku SDK", "Error: " + ex.Message, "OK");
        }
    }

    [MenuItem("Tools/Nutaku/Fix Asmdef GUID (Deep Clean)")]
    public static void RunFromMenu_DeepClean()
    {
        bool confirm = EditorUtility.DisplayDialog(
            "Deep Clean Confirmation",
            "This will delete your project's 'Library' folder, forcing Unity to re-import ALL assets. " +
            "This can take a long time. Only use this if other fixes haven't worked.\n\n" +
            "Do you want to proceed?",
            "Yes, Proceed",
            "No, Cancel");

        if (confirm)
        {
            Debug.Log("[NutakuAsmdefGuidFixer] Initiating Deep Clean: Deleting Library folder.");
            SessionState.SetBool(SessionKeyPendingLibraryReset, true);

            string libraryPath = Path.GetFullPath("Library");
            if (Directory.Exists(libraryPath))
            {
                FileUtil.DeleteFileOrDirectory(libraryPath);
            }
        }
    }

    private static void CheckFolder(string folder)
    {
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }

    private static string FindAsmdefInFolder()
    {
        var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { NutakuSdkFolder });
        if (guids != null && guids.Length > 0)
        {
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(path).Equals(DefaultAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return AssetDatabase.GUIDToAssetPath(guids[0]);
        }

        return null;
    }

    private static void EnsureSdkAsmdefWithGuid(bool verbose = false)
    {
        if (string.IsNullOrEmpty(DesiredGuid) || !Regex.IsMatch(DesiredGuid, @"^[0-9a-f]{32}$"))
            throw new InvalidOperationException("DesiredGuid is not set to a 32-char lowercase hex string.");

        CheckFolder(NutakuSdkFolder);

        string nutakuAsmdefPath = Path.Combine(NutakuSdkFolder, DefaultAssemblyName + ".asmdef").Replace("\\", "/");
        string oldGuidOfNutakuAsmdef = null;

        List<string> defsToReimport = new List<string>();


        // --- Step 1: Handle the primary P.Nutaku.asmdef ---
        string currentNutakuAsmdefFile = FindAsmdefInFolder(); // Find actual existing asmdef (could be old name/path)
        string currentNutakuGuid = null;

        if (currentNutakuAsmdefFile != null)
        {
            currentNutakuGuid = AssetDatabase.AssetPathToGUID(currentNutakuAsmdefFile);
            if (string.IsNullOrEmpty(currentNutakuGuid)) // If it has no GUID, it's problematic
            {
                if (verbose)
                    Debug.LogWarning($"[NutakuAsmdefGuidFixer] Existing asmdef '{currentNutakuAsmdefFile}' found but has no GUID. Deleting and recreating.");
                AssetDatabase.DeleteAsset(currentNutakuAsmdefFile);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); // Clear immediately
                currentNutakuAsmdefFile = null; // Mark as deleted
            }
            else if (!currentNutakuGuid.Equals(DesiredGuid, StringComparison.OrdinalIgnoreCase))
            {
                oldGuidOfNutakuAsmdef = currentNutakuGuid; // Store the old GUID for reference updates
                if (verbose)
                    Debug.LogWarning($"[NutakuAsmdefGuidFixer] Existing asmdef '{currentNutakuAsmdefFile}' has GUID '{currentNutakuGuid}', but '{DesiredGuid}' is desired. Deleting and recreating.");
                AssetDatabase.DeleteAsset(currentNutakuAsmdefFile);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                currentNutakuAsmdefFile = null; // Mark as deleted
            }
        }

        if (currentNutakuAsmdefFile == null) // Either didn't exist, or was deleted
        {
            CreateAsmdefFile(nutakuAsmdefPath, DefaultAssemblyName);
            CreateOrRewriteMetaWithGuid(nutakuAsmdefPath, DesiredGuid, verbose);
            AssetDatabase.ImportAsset(nutakuAsmdefPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        else // Existing asmdef, and its GUID was already correct
        {
            if (verbose)
                Debug.Log($"[NutakuAsmdefGuidFixer] Existing asmdef '{nutakuAsmdefPath}' already has desired GUID '{DesiredGuid}'.");
        }

        // Ensure the internal name of the P.Nutaku.asmdef is correct, regardless if it was recreated or not
        string nutakuAsmdefContent = File.ReadAllText(nutakuAsmdefPath, Encoding.UTF8);
        if (!nutakuAsmdefContent.Contains($"\"name\": \"{DefaultAssemblyName}\""))
        {
            nutakuAsmdefContent = Regex.Replace(nutakuAsmdefContent, @"""name"":\s*""[^""]*""", $"\"name\": \"{DefaultAssemblyName}\"");
            File.WriteAllText(nutakuAsmdefPath, nutakuAsmdefContent, new UTF8Encoding(false));
            if (verbose)
                Debug.Log($"[NutakuAsmdefGuidFixer] Corrected internal name of {nutakuAsmdefPath} to {DefaultAssemblyName}.");
            AssetDatabase.ImportAsset(nutakuAsmdefPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); // Import to pick up name change
        }

        AssetDatabase.DisallowAutoRefresh();
        AssetDatabase.StartAssetEditing();
        try
        {
            defsToReimport.Add(nutakuAsmdefPath); // Always ensure it's in the final re-import list

            // --- Step 2: Now fix references in *other* asmdefs ---
            // We pass the old GUID only if the P.Nutaku.asmdef was deleted/recreated or had its GUID forced.
            FixReferencesInOtherAsmdefs(
                oldGuidOfNutakuAsmdef,
                DesiredGuid,
                defsToReimport,
                verbose
            );
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.AllowAutoRefresh();

            foreach (var path in defsToReimport.Distinct())
            {
                if (File.Exists(path))
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    if (verbose)
                        Debug.Log($"[NutakuAsmdefGuidFixer] Re-imported: {path}");
                }
            }

            // Final refresh to ensure everything is synchronized
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            if (verbose)
                Debug.Log("[NutakuAsmdefGuidFixer] AssetDatabase.Refresh completed.");
        }

        defsToReimport = new List<string>();
        EditorApplication.delayCall += () =>
        {
            FixDuplications(defsToReimport, verbose);
            foreach (var path in defsToReimport.Distinct())
            {
                if (File.Exists(path))
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    if (verbose)
                        Debug.Log($"[NutakuAsmdefGuidFixer] Re-imported: {path}");
                }
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            if (verbose)
                Debug.Log("[NutakuAsmdefGuidFixer] Final AssetDatabase.Refresh after duplication checks completed.");
        };
    }

    private static void CreateAsmdefFile(string asmdefPath, string assemblyName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"name\": \"{assemblyName}\",");
        sb.AppendLine("  \"references\": [],");
        sb.AppendLine("  \"includePlatforms\": [],");
        sb.AppendLine("  \"excludePlatforms\": [],");
        sb.AppendLine("  \"allowUnsafeCode\": false,");
        sb.AppendLine("  \"overrideReferences\": false,");
        sb.AppendLine("  \"precompiledReferences\": [],");
        sb.AppendLine("  \"autoReferenced\": true,");
        sb.AppendLine("  \"defineConstraints\": [],");
        sb.AppendLine("  \"noEngineReferences\": false");
        sb.AppendLine("}");

        File.WriteAllText(asmdefPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static void CreateOrRewriteMetaWithGuid(string assetPath, string newGuid, bool verbose)
    {
        string metaPath = assetPath + ".meta";

        var sb = new StringBuilder();
        sb.AppendLine("fileFormatVersion: 2");
        sb.AppendLine($"guid: {newGuid}");
        sb.AppendLine("AssemblyDefinitionImporter:");
        sb.AppendLine("  externalObjects: {}");
        sb.AppendLine("  userData: ");
        sb.AppendLine("  assetBundleName: ");
        sb.AppendLine("  assetBundleVariant: ");
        sb.AppendLine("");
        string metaContent = sb.ToString();

        File.WriteAllText(metaPath, metaContent);
        if (verbose)
            Debug.Log($"[NutakuAsmdefGuidFixer] Created meta '{Path.GetFileName(assetPath)}' with guid'{newGuid}'.");
    }

    private static void FixDuplications(List<string> defsToReimport, bool verbose)
    {
        var allPaths = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets", "Packages" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        string needle = $"\"{DefaultAssemblyName}\"";
        string replacement = "";
        foreach (var path in allPaths)
        {
            if (path.Contains(DefaultAssemblyName))
                continue;

            if (!File.Exists(path))
                continue;

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Contains(needle))
            {
                string newText = text.Replace(needle, replacement);
                if (!ReferenceEquals(newText, text))
                {
                    File.WriteAllText(path, newText, new UTF8Encoding(false));
                    defsToReimport.Add(path);
                    if (verbose)
                        Debug.Log($"[NutakuAsmdefGuidFixer] Fixed duplicated reference in: {path}");
                }
            }
        }
    }

    private static void FixReferencesInOtherAsmdefs(string oldGuidToReplace, string targetGuid, List<string> defsToReimport, bool verbose)
    {
        var targetPath = AssetDatabase.GUIDToAssetPath(DesiredGuid);
        if (verbose)
            Debug.Log($"Target path for GUID: {targetPath}");

        if (string.IsNullOrEmpty(targetPath))
            Debug.LogError("[NutakuAsmdefGuidFixer] nutaku asmdef path is null or empty.");

        var allPaths = AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets", "Packages" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        int changed = 0;
        string needle = "\"GUID:" + oldGuidToReplace + "\"";
        string replacement = "\"GUID:" + targetGuid + "\"";

        foreach (var path in allPaths)
        {
            if (!File.Exists(path))
                continue;

            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Contains(needle))
            {
                string newText = text.Replace(needle, replacement);
                if (!ReferenceEquals(newText, text))
                {
                    File.WriteAllText(path, newText, new UTF8Encoding(false));
                    defsToReimport.Add(path);
                    changed++;
                    if (verbose)
                        Debug.Log($"[NutakuAsmdefGuidFixer] Updated reference in: {path}");
                }
            }
        }

        if (verbose)
            Debug.Log($"[NutakuAsmdefGuidFixer] Updated {changed} asmdef/asmref file(s).");
    }
}
#endif